using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopWidgets.Telemetry;

namespace ResourcesWidget
{
    // Allowlisted primitive projection. No names, paths, UI state or mutable sensor references escape.
    sealed class HardwareTelemetryProjection
    {
        readonly Dictionary<string, object> root;
        HardwareTelemetryProjection(Dictionary<string, object> root) { this.root = root; }

        public string Serialize(DateTime publishedAtUtc)
        {
            var publication = new Dictionary<string, object>(root);
            publication["published_at"] = Utc(publishedAtUtc);
            return Json.Serialize(publication, false);
        }

        internal static string Utc(DateTime value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        }

        public static HardwareTelemetryProjection Capture(Sample sample, int intervalMs, string mode)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            DateTime cutoff = sample.CollectedAtUtc;
            if (cutoff.Kind != DateTimeKind.Utc) throw new ArgumentException("collection_utc_required");
            DateTime? latest = null;
            int known = 0;
            var metrics = new Dictionary<string, object>();
            Action<string, object, string, DateTime?> metric = (id, value, unit, at) =>
            {
                bool valid = value != null && at.HasValue && at.Value.Kind == DateTimeKind.Utc && at.Value <= cutoff;
                if (value is double) valid = valid && !double.IsNaN((double)value) && !double.IsInfinity((double)value);
                if (valid) { known++; if (!latest.HasValue || latest.Value < at.Value) latest = at; }
                metrics[id] = new Dictionary<string, object> {
                    { "value", valid ? value : null }, { "unit", unit }, { "status", valid ? "ok" : "unknown" },
                    { "reason", valid ? "none" : "sensor_unavailable" }, { "measured_at", valid ? Utc(at.Value) : null }
                };
            };
            var cpu = sample.Cpu;
            metric("cpu_usage_percent", cpu != null && Percent(cpu.Total) ? (object)cpu.Total : null, "percent", cpu != null ? cpu.UsageMeasuredAtUtc : null);
            metric("cpu_temperature_celsius", cpu != null && Temperature(cpu.Temp) ? (object)cpu.Temp.Value : null, "celsius", cpu != null ? cpu.TempMeasuredAtUtc : null);
            var mem = sample.Mem;
            bool physical = mem != null && mem.Total > 0 && Bytes(mem.Total) && Bytes(mem.Available) && mem.Available <= mem.Total;
            bool commit = mem != null && mem.CommitLimit > 0 && Bytes(mem.CommitTotal) && Bytes(mem.CommitLimit) && mem.CommitTotal <= mem.CommitLimit;
            DateTime? memAt = mem != null ? mem.MeasuredAtUtc : null;
            metric("ram_total_bytes", physical ? (object)mem.Total : null, "bytes", memAt);
            metric("ram_available_bytes", physical ? (object)mem.Available : null, "bytes", memAt);
            metric("commit_total_bytes", commit ? (object)mem.CommitTotal : null, "bytes", memAt);
            metric("commit_limit_bytes", commit ? (object)mem.CommitLimit : null, "bytes", memAt);
            metric("commit_headroom_bytes", commit ? (object)(mem.CommitLimit - mem.CommitTotal) : null, "bytes", memAt);
            // Existing collector has no actual pagefile-usage observation. Commit minus RAM is not swap.
            metric("swap_used_bytes", null, "bytes", null);
            var gpu = sample.Gpu;
            bool memory = gpu != null && gpu.MemTotal > 0 && Bytes(gpu.MemTotal) && Bytes(gpu.MemUsed) && gpu.MemUsed <= gpu.MemTotal && gpu.MemoryMeasuredAtUtc.HasValue && gpu.MemoryMeasuredAtUtc <= cutoff;
            string kind = memory ? (gpu.MemShared ? "shared" : "dedicated") : "unknown";
            DateTime? gpuAt = gpu != null ? gpu.MemoryMeasuredAtUtc : null;
            metric("gpu_usage_percent", gpu != null && Percent(gpu.Load) ? (object)gpu.Load : null, "percent", gpu != null ? gpu.UsageMeasuredAtUtc : null);
            metric("gpu_temperature_celsius", gpu != null && Temperature(gpu.Temp) ? (object)gpu.Temp.Value : null, "celsius", gpu != null ? gpu.TempMeasuredAtUtc : null);
            metric("gpu_memory_total_bytes", memory ? (object)gpu.MemTotal : null, "bytes", gpuAt);
            metric("gpu_memory_available_bytes", memory ? (object)(gpu.MemTotal - gpu.MemUsed) : null, "bytes", gpuAt);
            metric("vram_total_bytes", memory && kind == "dedicated" ? (object)gpu.MemTotal : null, "bytes", gpuAt);
            metric("vram_available_bytes", memory && kind == "dedicated" ? (object)(gpu.MemTotal - gpu.MemUsed) : null, "bytes", gpuAt);
            var net = sample.Net;
            metric("network_receive_bytes_per_second", net != null && Rate(net.DownBps) ? (object)net.DownBps : null, "bytes_per_second", net != null ? net.MeasuredAtUtc : null);
            metric("network_send_bytes_per_second", net != null && Rate(net.UpBps) ? (object)net.UpBps : null, "bytes_per_second", net != null ? net.MeasuredAtUtc : null);
            var health = sample.NetworkHealth;
            bool probe = health != null && health.ProbeIntervalMs >= 1000 && health.ProbeIntervalMs <= 60000;
            metric("gateway_rtt_ms", probe && Rtt(health.GatewayMs) ? (object)health.GatewayMs.Value : null, "milliseconds", health != null ? health.GatewayMeasuredAtUtc : null);
            metric("internet_rtt_ms", probe && Rtt(health.PingMs) ? (object)health.PingMs.Value : null, "milliseconds", health != null ? health.PingMeasuredAtUtc : null);
            var gateway = (Dictionary<string, object>)metrics["gateway_rtt_ms"];
            gateway["scope"] = "gateway"; gateway["probe_interval_ms"] = probe ? (object)health.ProbeIntervalMs : null;
            var internet = (Dictionary<string, object>)metrics["internet_rtt_ms"];
            internet["scope"] = "internet_probe"; internet["probe_interval_ms"] = probe ? (object)health.ProbeIntervalMs : null;
            // A successful gateway probe establishes gateway reachability only, never provider availability.
            bool gatewayMeasured = gateway["status"] as string == "ok";
            metric("network_reachability", gatewayMeasured ? "reachable" : null, "reachability", health != null ? health.GatewayMeasuredAtUtc : null);
            var reachable = (Dictionary<string, object>)metrics["network_reachability"];
            reachable["scope"] = gatewayMeasured ? "gateway" : "not_measured"; reachable["provider_id"] = null;
            var quality = Quality(sample.NetworkDay, health, cutoff);
            var qualityAt = quality["measured_at"] as string;
            if (qualityAt != null && health.ObservationAtUtc.HasValue && (!latest.HasValue || latest.Value < health.ObservationAtUtc.Value)) latest = health.ObservationAtUtc;
            bool available = known > 0 || qualityAt != null;
            var root = new Dictionary<string, object> {
                { "schemaVersion", "1.0.0" }, { "mode", mode }, { "kind", "hardware-snapshot" }, { "source_id", "resources-widget" },
                { "measured_at", latest.HasValue ? Utc(latest.Value) : null }, { "last_attempt_at", Utc(cutoff) },
                { "status", available ? "partial" : "unavailable" }, { "reason", "sensor_unavailable" },
                { "sample_interval_ms", intervalMs }, { "metrics", metrics }, { "gpu_memory_kind", kind }, { "network_quality", quality }
            };
            return new HardwareTelemetryProjection(root);
        }

        static Dictionary<string, object> Quality(DayPeaks day, NetHealth health, DateTime cutoff)
        {
            bool valid = day != null && health != null && health.ObservationAtUtc.HasValue && health.ObservationAtUtc.Value.Kind == DateTimeKind.Utc && health.ObservationAtUtc.Value <= cutoff
                && health.WindowStartUtc.Kind == DateTimeKind.Utc && health.WindowEndUtc.Kind == DateTimeKind.Utc
                && health.WindowStartUtc <= health.ObservationAtUtc.Value && health.ObservationAtUtc.Value < health.WindowEndUtc
                && (health.WindowEndUtc - health.WindowStartUtc).TotalHours >= 1 && (health.WindowEndUtc - health.WindowStartUtc).TotalHours <= 26
                && day.Seconds >= 0 && !double.IsNaN(day.Seconds) && !double.IsInfinity(day.Seconds)
                && day.PingSent >= day.PingLost && day.PingLost >= 0 && day.GwSent >= day.GwLost && day.GwLost >= 0 && day.Outages >= 0 && day.Disconnects >= 0;
            // The legacy journal can load rounded coverage or include a sample straddling midnight.
            // Unknown is preferable to silently adjusting the original observed duration.
            double? coverage = valid && day.Seconds <= (health.ObservationAtUtc.Value - health.WindowStartUtc).TotalSeconds ? (double?)day.Seconds : null;
            bool all = valid && coverage.HasValue && day.PingSent > 0 && day.GwSent > 0;
            return new Dictionary<string, object> {
                { "status", all ? "ok" : valid ? "partial" : "unknown" }, { "reason", all ? "none" : "sensor_unavailable" },
                { "measured_at", valid ? Utc(health.ObservationAtUtc.Value) : null }, { "window_kind", "local_calendar_day" },
                { "window_start", valid ? Utc(health.WindowStartUtc) : null }, { "window_end", valid ? Utc(health.WindowEndUtc) : null },
                { "coverage_scope", "widget_observed_only" }, { "coverage_seconds", coverage }, { "coverage_unit", "seconds" }, { "count_unit", "count" }, { "loss_unit", "percent" },
                { "internet_sent", valid ? (object)day.PingSent : null }, { "internet_lost", valid ? (object)day.PingLost : null },
                { "gateway_sent", valid ? (object)day.GwSent : null }, { "gateway_lost", valid ? (object)day.GwLost : null },
                { "internet_loss_percent", valid && day.PingSent > 0 ? (object)(100.0 * day.PingLost / day.PingSent) : null },
                { "gateway_loss_percent", valid && day.GwSent > 0 ? (object)(100.0 * day.GwLost / day.GwSent) : null },
                { "outages_count", valid ? (object)day.Outages : null }, { "disconnect_count", valid ? (object)day.Disconnects : null }
            };
        }
        static bool Bytes(long value) { return value >= 0 && value <= 9007199254740991L; }
        static bool Percent(double value) { return !double.IsNaN(value) && value >= 0 && value <= 100; }
        static bool Temperature(double? value) { return value.HasValue && !double.IsNaN(value.Value) && value.Value >= -50 && value.Value <= 200; }
        static bool Rtt(double? value) { return value.HasValue && !double.IsNaN(value.Value) && value.Value >= 0 && value.Value <= 60000; }
        static bool Rate(double value) { return !double.IsNaN(value) && value >= 0 && value <= 9007199254740991D; }
    }

    sealed class TelemetryPublisher : IDisposable
    {
        readonly TelemetrySnapshotWriter writer;
        readonly int interval;
        readonly string mode;
        internal TelemetrySnapshotWriter Writer { get { return writer; } }
        public TelemetryPublisher(int intervalMs, string directoryPath, string mode)
        {
            interval = intervalMs; this.mode = mode;
            writer = new TelemetrySnapshotWriter(new TelemetryWriterOptions { DirectoryPath = directoryPath, FileName = "hardware-snapshot.json", Mode = mode, MinIntervalMs = 1000 });
        }
        public void Submit(Sample sample)
        {
            try { var frozen = HardwareTelemetryProjection.Capture(sample, interval, mode); writer.Submit(frozen.Serialize); }
            catch { /* Export failure must not suppress Updated or break collection. */ }
        }
        public void Dispose() { writer.Dispose(); }
    }
}
