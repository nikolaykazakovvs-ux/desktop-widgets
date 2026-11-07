using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LimitsWidget
{
    // Опрашивает сервисы параллельно; при сбое показывает последний удачный снимок с пометкой «устарело».
    // Каждый сервис отдаёт результат сразу по готовности (onResult), не дожидаясь остальных: сначала основные квоты,
    // затем, если есть, догрузку (статистика Codex, кредиты Antigravity). У каждого опроса свой номер (Generation),
    // поздний результат старого опроса не затирает новый ни в кэше, ни в UI.
    sealed class Collector
    {
        readonly Config cfg;
        readonly State state;
        readonly string fixtures;
        readonly TelemetryPublisher telemetry;
        readonly Func<DateTime> utcNow;
        readonly Dictionary<string, IProvider> providers = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();
        long generation;

        public Collector(Config cfg, State state, string fixtures)
            : this(cfg, state, fixtures, null, null, null) { }

        internal Collector(Config cfg, State state, string fixtures, TelemetryPublisher telemetry, IEnumerable<IProvider> sourceProviders, Func<DateTime> utcNow)
        {
            this.telemetry = telemetry;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.cfg = cfg;
            this.state = state;
            this.fixtures = fixtures;
            foreach (var p in sourceProviders ?? new IProvider[] { new ClaudeProvider(), new KimiProvider(), new AntigravityProvider(), new CodexProvider() })
                providers[p.Id] = p;
        }

        public long Generation { get { return Interlocked.Read(ref generation); } }

        public List<ServiceSnapshot> Collect()
        {
            return Collect(null);
        }

        // onResult вызывается из рабочих потоков с независимой копией снимка: ранний (Partial) и итоговый результат сервиса.
        public List<ServiceSnapshot> Collect(Action<ServiceSnapshot> onResult)
        {
            lock (gate)
            {
                cfg.ReloadIfChanged();
                long gen = Interlocked.Increment(ref generation);

                var ids = cfg.Order.Where(id => providers.ContainsKey(id) && cfg.Svc(id).Enabled).ToList();
                try { if (telemetry != null) telemetry.Configure(ids, cfg.RefreshSeconds); }
                catch (Exception ex) { Log.Write("telemetry configure: " + ex.GetType().Name); }
                var tasks = ids.Select(id => Task.Factory.StartNew(() => CollectOne(id, gen, onResult), TaskCreationOptions.LongRunning)).ToArray();
                Task.WaitAll(tasks);
                return tasks.Select(t => t.Result).ToList();
            }
        }

        ServiceSnapshot CollectOne(string id, long gen, Action<ServiceSnapshot> onResult)
        {
            DateTime now = utcNow().ToLocalTime();
            ServiceSnapshot core = null;
            var ctx = new ProviderContext { Config = cfg, Svc = cfg.Svc(id), FixturesDir = fixtures, Now = now, State = state, FixtureOnly = !string.IsNullOrEmpty(fixtures), UtcNow = utcNow, Generation = gen };
            ctx.Core = early =>
            {
                // основные квоты готовы: в кэш, телеметрию и UI сразу; провайдер продолжает догрузку со своим экземпляром
                early.Generation = gen;
                core = Complete(id, early, null, true);
                Deliver(onResult, core, true);
            };
            ServiceSnapshot snap;
            try
            {
                snap = providers[id].Collect(ctx);
            }
            catch (Exception ex)
            {
                Log.Write(id + ": " + ex);
                if (core != null)
                {
                    // ранний результат уже опубликован — догрузка не удалась, квоты остаются действительными
                    Deliver(onResult, core, false);
                    return core;
                }
                snap = ProviderUtil.NewSnap(ctx, id);
                snap.Status = SvcStatus.Error;
                snap.StatusText = "ошибка";
                snap.Message = "Сбой сбора данных (" + ex.GetType().Name + ") — подробности в widget.log";
            }
            snap.Generation = gen;
            var result = Complete(id, snap, core, false);
            Deliver(onResult, result, false);
            return result;
        }

        static void Deliver(Action<ServiceSnapshot> onResult, ServiceSnapshot snap, bool partial)
        {
            if (onResult == null) return;
            var copy = snap.Clone();
            copy.Partial = partial;
            try { onResult(copy); }
            catch (Exception ex) { Log.Write("collect callback: " + ex.GetType().Name); }
        }

        // core — уже опубликованный ранний результат этого же опроса (или null).
        ServiceSnapshot Complete(string id, ServiceSnapshot snap, ServiceSnapshot core, bool early)
        {
            DateTime now = utcNow().ToLocalTime();
            var svc = cfg.Svc(id);
            // время попытки — завершение основного запроса: догрузка его не сдвигает
            snap.LastAttemptAt = core != null ? core.LastAttemptAt : utcNow();
            // провайдер не должен портить уже опубликованные квоты поздней ошибкой догрузки
            if (core != null && snap.Status != SvcStatus.Ok && snap.Status != SvcStatus.Fixture) return core;
            PlanResolver.Apply(snap, svc);
            state.PutAttempt(snap);
            if (snap.Status == SvcStatus.Ok || snap.Status == SvcStatus.Fixture)
            {
                state.PutCache(snap);
                // догрузка не меняет квоты — телеметрия уже получила их из раннего результата
                return core == null ? Published(snap) : snap;
            }
            if (snap.Status == SvcStatus.NeedsPermission) return Published(snap);

            // Fixture failures never consume a live state cache. Live runs never consume fixture cache.
            if (!string.IsNullOrEmpty(fixtures)) return Published(snap);
            var cached = state.GetCache(id);
            if (cached != null && cached.Status == SvcStatus.Fixture) return Published(snap);
            if (cached == null || cached.Bars.Count == 0) return Published(snap);
            cached.Generation = snap.Generation;
            cached.LastAttemptAt = snap.LastAttemptAt;
            cached.Title = snap.Title;
            PlanResolver.Apply(cached, svc);
            cached.Status = SvcStatus.Stale;
            cached.StatusText = cached.DataTime.HasValue ? "данные " + Fmt.Age(cached.DataTime.Value, now) : "устарело";
            cached.Message = snap.Message;
            cached.ActionId = snap.ActionId;
            cached.ActionLabel = snap.ActionLabel;
            cached.Attention = snap.Attention;
            if (cached.Activity != null) cached.Activity.Stale = true;
            return Published(cached);
        }

        ServiceSnapshot Published(ServiceSnapshot snap)
        {
            try { if (telemetry != null) telemetry.Accept(snap); }
            catch (Exception ex) { Log.Write("telemetry projection: " + ex.GetType().Name); }
            return snap;
        }
    }

    // Набор карточек в UI: результат сервиса заменяет прежний, только если он не старше уже показанного.
    static class SnapshotBoard
    {
        public static bool Merge(List<ServiceSnapshot> shown, ServiceSnapshot incoming, IList<string> order)
        {
            int i = shown.FindIndex(s => s.Id == incoming.Id);
            if (i >= 0)
            {
                var cur = shown[i];
                if (incoming.Generation < cur.Generation) return false;
                // ранний результат не откатывает уже показанный итоговый результат того же опроса
                if (incoming.Generation == cur.Generation && incoming.Partial && !cur.Partial) return false;
                shown[i] = incoming;
            }
            else shown.Add(incoming);
            if (order != null)
            {
                var sorted = shown.OrderBy(s => { int k = order.IndexOf(s.Id); return k < 0 ? int.MaxValue : k; }).ToList();
                shown.Clear();
                shown.AddRange(sorted);
            }
            return true;
        }

        // Сервисы, выключенные в настройках, убираются после завершения опроса.
        public static void Retain(List<ServiceSnapshot> shown, ICollection<string> ids)
        {
            shown.RemoveAll(s => !ids.Contains(s.Id));
        }
    }
}
