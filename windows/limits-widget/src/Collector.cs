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
        readonly Dictionary<string, IProvider> providers = new Dictionary<string, IProvider>(StringComparer.OrdinalIgnoreCase);
        readonly object gate = new object();

        public Collector(Config cfg, State state, string fixtures)
        {
            this.cfg = cfg;
            this.state = state;
            this.fixtures = fixtures;
            foreach (var p in new IProvider[] { new ClaudeProvider(), new KimiProvider(), new AntigravityProvider(), new CodexProvider() })
                providers[p.Id] = p;
        }

        public List<ServiceSnapshot> Collect()
        {
            lock (gate)
            {
                cfg.ReloadIfChanged();
                DateTime now = DateTime.Now;
                var ids = cfg.Order.Where(id => providers.ContainsKey(id) && cfg.Svc(id).Enabled).ToList();
                var tasks = ids.Select(id => Task.Factory.StartNew(() => CollectOne(id, now), TaskCreationOptions.LongRunning)).ToArray();
                Task.WaitAll(tasks);
                return tasks.Select(t => t.Result).ToList();
            }
        }

        ServiceSnapshot CollectOne(string id, DateTime now)
        {
            var ctx = new ProviderContext { Config = cfg, Svc = cfg.Svc(id), FixturesDir = fixtures, Now = now, State = state };
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

            if (snap.Status == SvcStatus.Ok)
            {
                state.PutCache(snap);
                return snap;
            }
            if (snap.Status == SvcStatus.NeedsPermission || snap.Status == SvcStatus.Fixture) return snap;

            var cached = state.GetCache(id);
            if (cached == null || cached.Bars.Count == 0) return snap;
            cached.Title = snap.Title;
            cached.Url = snap.Url;
            cached.Status = SvcStatus.Stale;
            cached.StatusText = cached.DataTime.HasValue ? "данные " + Fmt.Age(cached.DataTime.Value, now) : "устарело";
            cached.Message = snap.Message;
            cached.ActionId = snap.ActionId;
            cached.ActionLabel = snap.ActionLabel;
            cached.Attention = snap.Attention;
            return cached;
        }
    }
}
