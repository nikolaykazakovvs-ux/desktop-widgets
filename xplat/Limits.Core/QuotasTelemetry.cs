using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace DesktopWidgets.Limits.Core
{
    // Проекция в публичный контракт quotas-snapshot 1.0.0 (telemetry/schema). Логика перенесена из
    // windows/limits-widget/src/TelemetryPublisher.cs без изменений: те же поля, порядок ключей, правила статусов.
    // Bridge и S.I.G.M.A. читают этот файл одинаково, на какой бы ОС он ни был записан.
    public static class QuotasProjection
    {
        public const int HeartbeatIntervalMs = 10000;
        static readonly string[] Providers = { "codex", "claude", "antigravity", "gemini", "kimi" };

        public static bool IsProvider(string id) => Providers.Contains(id);

        public static string Stamp(DateTime? value) =>
            value.HasValue ? value.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) : null;

        public static Dictionary<string, object> EmptyProvider(string id) => new Dictionary<string, object>
        {
            { "provider_id", id }, { "measured_at", null }, { "last_attempt_at", null },
            { "status", "unavailable" }, { "reason", "source_unavailable" }, { "pools", new object[0] }
        };

        public static Dictionary<string, object> ProjectProvider(ServiceSnapshot snap)
        {
            var p = EmptyProvider(snap.Id);
            p["last_attempt_at"] = Stamp(snap.LastAttemptAt);
            var pools = new List<object>();
            DateTime? latest = null;
            bool known = false, missing = false;
            foreach (var group in snap.Bars.Where(b => b.Quota != null).GroupBy(b => b.Quota.PoolId).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var models = QuotaObservation.Models(snap.Id, group.Key);
                if (models == null) { missing = true; continue; }
                var windows = new List<object>();
                var seen = new HashSet<string>();
                foreach (var bar in group)
                {
                    var q = bar.Quota;
                    if (!seen.Add(q.WindowId)) { missing = true; continue; }
                    var safe = QuotaObservation.Create(snap.Id, q.PoolId, q.WindowId, q.DurationSeconds, q.UsedPercent, q.ResetsAt);
                    if (safe == null) { missing = true; continue; }
                    bool valid = safe.UsedPercent.HasValue && q.MeasuredAt.HasValue && snap.LastAttemptAt.HasValue
                        && q.MeasuredAt.Value.ToUniversalTime() <= snap.LastAttemptAt.Value.ToUniversalTime();
                    known |= valid; missing |= !valid;
                    if (valid && (!latest.HasValue || q.MeasuredAt.Value.ToUniversalTime() > latest.Value)) latest = q.MeasuredAt.Value.ToUniversalTime();
                    windows.Add(new Dictionary<string, object> { { "window_id", safe.WindowId }, { "duration_seconds", safe.DurationSeconds },
                        { "status", valid ? "ok" : "unknown" }, { "reason", valid ? "none" : "source_unavailable" },
                        { "measured_at", valid ? Stamp(q.MeasuredAt) : null }, { "last_attempt_at", Stamp(snap.LastAttemptAt) },
                        { "used_percent", valid ? safe.UsedPercent : null }, { "remaining_percent", valid ? (double?)(100 - safe.UsedPercent.Value) : null },
                        { "resets_at", Stamp(safe.ResetsAt) } });
                }
                if (windows.Count > 0) pools.Add(new Dictionary<string, object> { { "pool_id", group.Key }, { "model_ids", models }, { "windows", windows } });
            }
            if (snap.Bars.Any(b => b.Quota == null)) missing = true;
            bool success = snap.Status == SvcStatus.Ok || snap.Status == SvcStatus.Fixture;
            p["pools"] = pools;
            p["measured_at"] = Stamp(latest);
            p["status"] = known ? (success && !missing ? "ok" : "partial") : (success && pools.Count > 0 ? "partial" : snap.Status == SvcStatus.Error ? "error" : "unavailable");
            p["reason"] = known ? (success && !missing ? "none" : success ? "source_partial" : "collection_failed") : (snap.Status == SvcStatus.Error ? "collection_failed" : "source_unavailable");
            return p;
        }

        public static string Serialize(string[] frozenProviders, int poll, string mode, DateTime published)
        {
            var items = frozenProviders.Select(s => Json.Obj(Json.Parse(s))).ToArray();
            DateTime? measured = null, attempted = null;
            foreach (var p in items)
            {
                var m = Json.Time(p["measured_at"]); var a = Json.Time(p["last_attempt_at"]);
                if (m.HasValue && (!measured.HasValue || m.Value > measured.Value)) measured = m;
                if (a.HasValue && (!attempted.HasValue || a.Value > attempted.Value)) attempted = a;
            }
            bool ok = items.Length > 0 && items.All(p => Json.Str(p["status"]) == "ok");
            bool usable = measured.HasValue;
            bool error = items.Any(p => Json.Str(p["status"]) == "error");
            string status = ok ? "ok" : usable || items.Any(p => Json.Str(p["status"]) == "partial") ? "partial" : error ? "error" : "unavailable";
            return Json.Serialize(new Dictionary<string, object> { { "schemaVersion", "1.0.0" }, { "mode", mode }, { "kind", "quotas-snapshot" },
                { "source_id", "limits-widget" }, { "measured_at", Stamp(measured) }, { "published_at", Stamp(published) },
                { "last_attempt_at", Stamp(attempted) }, { "status", status }, { "reason", ok ? "none" : error ? "collection_failed" : status == "partial" ? "source_partial" : "source_unavailable" },
                { "poll_interval_ms", poll }, { "heartbeat_interval_ms", HeartbeatIntervalMs }, { "providers", items } }, false);
        }
    }

    // Порядок «завершение провайдера → heartbeat → отправка в слот писателя» под одной блокировкой, как в WPF-версии.
    // Писатель (ITelemetrySink) — платформенный: Windows ACL или Unix-права 0700/0600.
    public sealed class QuotasPublisher : IDisposable
    {
        readonly object gate = new object();
        readonly Dictionary<string, string> providers = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly ITelemetrySink sink;
        readonly Timer heartbeat;
        readonly string mode;
        int pollIntervalMs;
        bool disposed;

        public QuotasPublisher(ITelemetrySink sink, bool fixture, int refreshSeconds)
        {
            this.sink = sink;
            mode = fixture ? "fixture" : "live";
            pollIntervalMs = Clamp(refreshSeconds);
            heartbeat = new Timer(_ => Heartbeat(), null, QuotasProjection.HeartbeatIntervalMs, QuotasProjection.HeartbeatIntervalMs);
            lock (gate) SubmitLocked();
        }

        public ITelemetrySink Sink => sink;

        static int Clamp(int refreshSeconds) => (int)Math.Max(60000L, Math.Min(86400000L, (long)refreshSeconds * 1000));

        public void Configure(IEnumerable<string> enabled, int refreshSeconds)
        {
            lock (gate)
            {
                if (disposed) return;
                pollIntervalMs = Clamp(refreshSeconds);
                var ids = new HashSet<string>(enabled.Where(QuotasProjection.IsProvider), StringComparer.Ordinal);
                foreach (var key in providers.Keys.Where(x => !ids.Contains(x)).ToArray()) providers.Remove(key);
                foreach (var id in ids)
                    if (!providers.ContainsKey(id)) providers[id] = Json.Serialize(QuotasProjection.EmptyProvider(id), false);
                SubmitLocked();
            }
        }

        public void Accept(ServiceSnapshot snap)
        {
            if (snap == null || !QuotasProjection.IsProvider(snap.Id)) return;
            string frozen = Json.Serialize(QuotasProjection.ProjectProvider(snap), false);
            lock (gate)
            {
                if (disposed) return;
                providers[snap.Id] = frozen;
                SubmitLocked();
            }
        }

        public void Heartbeat()
        {
            lock (gate) { if (!disposed) SubmitLocked(); }
        }

        void SubmitLocked()
        {
            string[] frozen = providers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value).ToArray();
            int interval = pollIntervalMs;
            string capturedMode = mode;
            sink.Submit(published => QuotasProjection.Serialize(frozen, interval, capturedMode, published));
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            heartbeat.Dispose();
            sink.Dispose();
        }
    }
}
