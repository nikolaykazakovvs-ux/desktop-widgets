using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DesktopWidgets.Telemetry;

namespace LimitsWidget
{
    // One state lock orders provider completion, heartbeat capture and latest-slot submission.
    sealed class TelemetryPublisher : IDisposable
    {
        public const int HeartbeatIntervalMs = 10000;
        readonly object gate = new object();
        readonly Dictionary<string, string> providers = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly TelemetrySnapshotWriter writer;
        readonly Timer heartbeat;
        readonly string mode;
        int pollIntervalMs;
        bool disposed;

        public TelemetryPublisher(string directory, bool fixture, int refreshSeconds)
        {
            mode = fixture ? "fixture" : "live";
            pollIntervalMs = (int)Math.Max(60000L, Math.Min(86400000L, (long)refreshSeconds * 1000));
            writer = new TelemetrySnapshotWriter(new TelemetryWriterOptions { DirectoryPath = directory,
                FileName = "quotas-snapshot.json", Mode = mode, MinIntervalMs = 1000 });
            heartbeat = new Timer(_ => Heartbeat(), null, HeartbeatIntervalMs, HeartbeatIntervalMs);
            lock (gate) SubmitLocked();
        }

        public string TargetPath { get { return writer.TargetPath; } }
        public long PublishedCount { get { return writer.PublishedCount; } }
        public string LastError { get { return writer.LastError; } }

        public void Configure(IEnumerable<string> enabled, int refreshSeconds)
        {
            lock (gate)
            {
                if (disposed) return;
                pollIntervalMs = (int)Math.Max(60000L, Math.Min(86400000L, (long)refreshSeconds * 1000));
                var ids = new HashSet<string>(enabled.Where(IsProvider), StringComparer.Ordinal);
                foreach (var key in providers.Keys.Where(x => !ids.Contains(x)).ToArray()) providers.Remove(key);
                foreach (var id in ids)
                    if (!providers.ContainsKey(id)) providers[id] = Json.Serialize(EmptyProvider(id), false);
                SubmitLocked();
            }
        }

        public void Accept(ServiceSnapshot snap)
        {
            if (snap == null || !IsProvider(snap.Id)) return;
            // Project before releasing the collector result to UI/cache consumers.
            string frozen = Json.Serialize(ProjectProvider(snap), false);
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
            writer.Submit(published => Serialize(frozen, interval, capturedMode, published));
        }

        static bool IsProvider(string id)
        {
            return new[] { "codex", "claude", "antigravity", "gemini", "kimi" }.Contains(id);
        }

        internal static string Stamp(DateTime? value)
        {
            return value.HasValue ? value.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        static Dictionary<string, object> EmptyProvider(string id)
        {
            return new Dictionary<string, object> { { "provider_id", id }, { "measured_at", null }, { "last_attempt_at", null },
                { "status", "unavailable" }, { "reason", "source_unavailable" }, { "pools", new object[0] } };
        }

        internal static Dictionary<string, object> ProjectProvider(ServiceSnapshot snap)
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
                    // Revalidate persisted metadata; legacy UI cache has no typed provenance and is never invented.
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

        internal static string Serialize(string[] frozenProviders, int poll, string mode, DateTime published)
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

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            heartbeat.Dispose();
            writer.Dispose();
        }
    }
}
