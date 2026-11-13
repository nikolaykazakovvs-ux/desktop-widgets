using System;
using System.Collections.Concurrent;
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
        // Сбой связи по сервисам текущего опроса и итог последнего завершённого опроса: при сбое повтор делается скоро.
        ConcurrentDictionary<string, bool> cycleLink = new ConcurrentDictionary<string, bool>();
        volatile bool linkFailure;

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

        // В последнем опросе хотя бы один сервис не ответил из-за сети.
        public bool LinkFailure { get { return linkFailure; } }

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
                cycleLink = new ConcurrentDictionary<string, bool>();
                try { if (telemetry != null) telemetry.Configure(ids, cfg.RefreshSeconds); }
                catch (Exception ex) { Log.Write("telemetry configure: " + ex.GetType().Name); }
                var tasks = ids.Select(id => Task.Factory.StartNew(() => CollectOne(id, gen, onResult), TaskCreationOptions.LongRunning)).ToArray();
                Task.WaitAll(tasks);
                linkFailure = cycleLink.Values.Any(v => v);
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
                Log.Write(id + ": " + ex);   // Log.Write вычищает секреты и параметры адресов
                if (core != null)
                {
                    // ранний результат уже опубликован — догрузка не удалась, квоты остаются действительными
                    Deliver(onResult, core, false);
                    return core;
                }
                snap = ProviderUtil.NewSnap(ctx, id);
                snap.Status = SvcStatus.Error;
                bool link;
                snap.Message = UserText.Failure(UserText.ServiceName(id, snap.Title), ex.GetType().Name + ": " + ex.Message, out link);
                snap.StatusText = link ? "нет связи" : "ошибка";
                snap.LinkDown = link;   // подробности уже в widget.log
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
            Sanitize(snap);
            // время попытки — завершение основного запроса: догрузка его не сдвигает
            snap.LastAttemptAt = core != null ? core.LastAttemptAt : utcNow();
            // провайдер не должен портить уже опубликованные квоты поздней ошибкой догрузки
            if (core != null && snap.Status != SvcStatus.Ok && snap.Status != SvcStatus.Fixture) return core;
            if (!early) cycleLink[id] = snap.LinkDown && snap.Status != SvcStatus.Ok && snap.Status != SvcStatus.Fixture;
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

        // Предохранитель: технический текст (исключение, stderr CLI, URL, JSON, английская фраза) в карточку не попадает ни от
        // одного провайдера, даже от того, кто забыл FailTech или добавит новую ошибку. Подробности — в widget.log, пользователю —
        // короткая фраза по смыслу. Работает над сообщением и пояснениями; кэш и статусы не меняет, кроме подписи «нет связи».
        internal static void Sanitize(ServiceSnapshot snap)
        {
            if (snap == null) return;
            string name = UserText.ServiceName(snap.Id, snap.Title);
            bool failed = snap.Status != SvcStatus.Ok && snap.Status != SvcStatus.Fixture;
            if (UserText.LooksTechnical(snap.Message)) snap.Message = Replacement(snap, name, snap.Message, failed);
            for (int i = snap.Notes.Count - 1; i >= 0; i--)
            {
                if (!UserText.LooksTechnical(snap.Notes[i])) continue;
                string text = Replacement(snap, name, snap.Notes[i], failed);
                if (text == snap.Message || (snap.Notes.IndexOf(text) >= 0 && snap.Notes.IndexOf(text) != i)) snap.Notes.RemoveAt(i);
                else snap.Notes[i] = text;
            }
        }

        static string Replacement(ServiceSnapshot snap, string name, string raw, bool failed)
        {
            Log.Write(snap.Id + ": " + UserText.Redact(raw));
            if (!failed) return UserText.NeutralNote;
            bool link;
            string text = UserText.Failure(name, raw, out link);
            if (link)
            {
                snap.LinkDown = true;
                if (snap.Status == SvcStatus.Error && snap.StatusText == "ошибка") snap.StatusText = "нет связи";
            }
            return text;
        }

        ServiceSnapshot Published(ServiceSnapshot snap)
        {
            Sanitize(snap);
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
