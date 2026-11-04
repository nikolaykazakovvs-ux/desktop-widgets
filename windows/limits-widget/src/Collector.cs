using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LimitsWidget
{
    // Опрашивает сервисы параллельно; при сбое показывает последний удачный снимок с пометкой «устарело».
    sealed class Collector
    {
        readonly Config cfg;
        readonly State state;
        readonly string fixtures;
        readonly TelemetryPublisher telemetry;
        readonly Func<DateTime> utcNow;
        readonly Dictionary<string, IProvider> providers = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();

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

        public List<ServiceSnapshot> Collect()
        {
            lock (gate)
            {
                cfg.ReloadIfChanged();

                var ids = cfg.Order.Where(id => providers.ContainsKey(id) && cfg.Svc(id).Enabled).ToList();
                try { if (telemetry != null) telemetry.Configure(ids, cfg.RefreshSeconds); }
                catch (Exception ex) { Log.Write("telemetry configure: " + ex.GetType().Name); }
                var tasks = ids.Select(id => Task.Factory.StartNew(() => CollectOne(id), TaskCreationOptions.LongRunning)).ToArray();
                Task.WaitAll(tasks);
                return tasks.Select(t => t.Result).ToList();
            }
        }

        ServiceSnapshot CollectOne(string id)
        {
            DateTime now = utcNow().ToLocalTime();
            var ctx = new ProviderContext { Config = cfg, Svc = cfg.Svc(id), FixturesDir = fixtures, Now = now, State = state, FixtureOnly = !string.IsNullOrEmpty(fixtures), UtcNow = utcNow };
            ServiceSnapshot snap;
            try
            {
                snap = providers[id].Collect(ctx);
            }
            catch (Exception ex)
            {
                Log.Write(id + ": " + ex);
                snap = ProviderUtil.NewSnap(ctx, id);
                snap.Status = SvcStatus.Error;
                snap.StatusText = "ошибка";
                snap.Message = "Сбой сбора данных (" + ex.GetType().Name + ") — подробности в widget.log";
            }

            snap.LastAttemptAt = utcNow();
            if (snap.Status == SvcStatus.Ok || snap.Status == SvcStatus.Fixture)
            {
                state.PutCache(snap);
                return Completed(snap);
            }
            if (snap.Status == SvcStatus.NeedsPermission) return Completed(snap);

            // Fixture failures never consume a live state cache. Live runs never consume fixture cache.
            if (!string.IsNullOrEmpty(fixtures)) return Completed(snap);
            var cached = state.GetCache(id);
            if (cached != null && cached.Status == SvcStatus.Fixture) return Completed(snap);
            if (cached == null || cached.Bars.Count == 0) return Completed(snap);
            cached.LastAttemptAt = snap.LastAttemptAt;
            cached.Title = snap.Title;
            cached.Url = snap.Url;
            cached.Status = SvcStatus.Stale;
            cached.StatusText = cached.DataTime.HasValue ? "данные " + Fmt.Age(cached.DataTime.Value, now) : "устарело";
            cached.Message = snap.Message;
            cached.ActionId = snap.ActionId;
            cached.ActionLabel = snap.ActionLabel;
            cached.Attention = snap.Attention;
            return Completed(cached);
        }

        ServiceSnapshot Completed(ServiceSnapshot snap)
        {
            try { if (telemetry != null) telemetry.Accept(snap); }
            catch (Exception ex) { Log.Write("telemetry projection: " + ex.GetType().Name); }
            return snap;
        }
    }
}
