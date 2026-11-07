using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DesktopWidgets.Telemetry;
using ResourcesWidget;

static class ResourcesPublisherTests
{
    static int checks;
    static void Check(bool condition, string id) { if (!condition) throw new Exception(id); checks++; }
    static void Until(Func<bool> check, string id) { var timer = Stopwatch.StartNew(); while (!check()) { if (timer.ElapsedMilliseconds > 3500) throw new Exception(id); Thread.Sleep(5); } }
    static DateTime At = new DateTime(2026, 10, 18, 23, 0, 0, DateTimeKind.Utc);
    static Sample Fake()
    {
        return new Sample {
            Time = At.ToLocalTime(), CollectedAtUtc = At,
            Cpu = new CpuInfo { Total = 25, UsageMeasuredAtUtc = At, Temp = 50, TempMeasuredAtUtc = At.AddSeconds(-2), Name = "PRIVATE_CPU" },
            Mem = new MemInfo { Total = 32000, Available = 16000, CommitTotal = 40000, CommitLimit = 100000, MeasuredAtUtc = At },
            Gpu = new GpuInfo { Load = 20, UsageMeasuredAtUtc = At, Temp = 55, TempMeasuredAtUtc = At.AddSeconds(-3), MemTotal = 8000, MemUsed = 2000, MemoryMeasuredAtUtc = At, FullName = "PRIVATE_GPU" },
            Net = new NetInfo { DownBps = 123, UpBps = 45, MeasuredAtUtc = At, Name = "PRIVATE_ADAPTER" },
            NetworkHealth = new NetHealth { PingMs = 20, GatewayMs = 2, PingMeasuredAtUtc = At.AddSeconds(-1), GatewayMeasuredAtUtc = At.AddSeconds(-2), ProbeIntervalMs = 2000,
                ObservationAtUtc = At, WindowStartUtc = At.Date.AddHours(21), WindowEndUtc = At.Date.AddDays(1).AddHours(21), PingHost = "PRIVATE_HOST" },
            NetworkDay = new DayPeaks { Seconds = 100, PingSent = 10, PingLost = 2, GwSent = 10, GwLost = 1, Outages = 1, Disconnects = 0 }
        };
    }
    static IDictionary<string, object> Parse(string json) { return Json.Obj(Json.TryParse(json)); }
    static IDictionary<string, object> Metric(IDictionary<string, object> root, string id) { return Json.Obj(Json.Get(Json.Obj(root["metrics"]), id)); }
    static int Main(string[] args)
    {
        try
        {
            if (args[0] == "--live-smoke") return LiveSmoke(Path.GetFullPath(args[1]));
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            var sample = Fake();
            // 02:00 local Oct 19 belongs to day starting 21:00 UTC Oct 18.
            sample.NetworkHealth.WindowStartUtc = At.Date.AddHours(21); sample.NetworkHealth.WindowEndUtc = At.Date.AddDays(1).AddHours(21);
            var frozen = HardwareTelemetryProjection.Capture(sample, 500, "fixture");
            sample.Mem.Available = 1; sample.Cpu.Total = 99; sample.NetworkDay.PingLost = 9;
            string text = frozen.Serialize(At.AddSeconds(1)); var root = Parse(text);
            Check(Json.Num(Metric(root, "ram_available_bytes")["value"]) == 16000, "immutable_memory");
            Check(Json.Num(Metric(root, "cpu_usage_percent")["value"]) == 25, "immutable_cpu");
            Check(Metric(root, "cpu_temperature_celsius")["measured_at"] as string == HardwareTelemetryProjection.Utc(At.AddSeconds(-2)), "thermal_original_time");
            Check(Metric(root, "gateway_rtt_ms")["measured_at"] as string == HardwareTelemetryProjection.Utc(At.AddSeconds(-2)), "rtt_original_time");
            Check(!text.Contains("PRIVATE_"), "allowlisted_projection");
            File.WriteAllText(Path.Combine(output, "dedicated.json"), text);
            sample = Fake(); sample.NetworkHealth.WindowStartUtc = At.Date.AddHours(21); sample.NetworkHealth.WindowEndUtc = At.Date.AddDays(1).AddHours(21);
            sample.Gpu.MemShared = true;
            root = Parse(HardwareTelemetryProjection.Capture(sample, 5000, "fixture").Serialize(At.AddSeconds(1)));
            Check(root["gpu_memory_kind"] as string == "shared" && Metric(root, "vram_total_bytes")["value"] == null && Json.Num(Metric(root, "gpu_memory_total_bytes")["value"]) == 8000, "shared_not_dedicated");
            File.WriteAllText(Path.Combine(output, "shared.json"), HardwareTelemetryProjection.Capture(sample, 5000, "fixture").Serialize(At.AddSeconds(1)));
            sample.Cpu.TempMeasuredAtUtc = null; sample.Gpu.TempMeasuredAtUtc = At.AddSeconds(2); sample.Gpu.MemoryMeasuredAtUtc = null;
            root = Parse(HardwareTelemetryProjection.Capture(sample, 1000, "fixture").Serialize(At.AddSeconds(1)));
            Check(Metric(root, "cpu_temperature_celsius")["value"] == null && Metric(root, "gpu_temperature_celsius")["value"] == null, "unknown_future_timestamp_rejected");
            Check(root["gpu_memory_kind"] as string == "unknown", "unknown_memory_kind");
            File.WriteAllText(Path.Combine(output, "unknown-times.json"), HardwareTelemetryProjection.Capture(sample, 1000, "fixture").Serialize(At.AddSeconds(1)));
            // DST end day in Kyiv: Oct 25 00:00 +03 to Oct 26 00:00 +02 is 25 hours.
            sample = Fake(); sample.CollectedAtUtc = new DateTime(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc);
            sample.NetworkHealth.ObservationAtUtc = sample.CollectedAtUtc;
            sample.NetworkHealth.WindowStartUtc = new DateTime(2026, 10, 24, 21, 0, 0, DateTimeKind.Utc); sample.NetworkHealth.WindowEndUtc = new DateTime(2026, 10, 25, 22, 0, 0, DateTimeKind.Utc);
            File.WriteAllText(Path.Combine(output, "dst-day.json"), HardwareTelemetryProjection.Capture(sample, 1000, "fixture").Serialize(sample.CollectedAtUtc.AddSeconds(1)));
            using (var journal = new NetJournal(null, true, new Config { PingHost = "" }))
            {
                DateTime midnight = new DateTime(2026, 10, 19, 0, 0, 1, DateTimeKind.Local);
                var adapter = new NetInfo { Name = "SYNTHETIC", LinkBps = 1000 };
                NetHealth health; journal.Update(adapter, midnight, 0, out health);
                var previous = new Pinger.Result { At = midnight.AddSeconds(-2), Gateway = false, Ms = 25 };
                journal.ApplyProbe(adapter, previous);
                journal.ApplyProbe(adapter, new Pinger.Result { At = midnight, Gateway = false, Ms = 20 });
                journal.ApplyProbe(adapter, new Pinger.Result { At = midnight, Gateway = true, Ms = -1 });
                var counts = journal.Update(adapter, midnight.AddSeconds(1), 0, out health);
                Check(counts.PingSent == 1 && counts.PingLost == 0 && counts.GwSent == 1 && counts.GwLost == 1, "cross_calendar_probe_not_counted_in_new_day");
                var field = typeof(NetJournal).GetField("lastPingAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                journal.ApplyProbe(adapter, previous);
                Check((DateTime)field.GetValue(journal) == previous.At, "previous_day_rtt_original_timestamp");
            }
            var knownGateway = Fake();
            knownGateway.NetworkHealth.GatewayMeasuredAtUtc = At.AddSeconds(-30);
            root = Parse(HardwareTelemetryProjection.Capture(knownGateway, 1000, "fixture").Serialize(At.AddSeconds(1)));
            var reachability = Metric(root, "network_reachability");
            Check(reachability["status"] as string == "ok" && reachability["value"] as string == "reachable" && reachability["scope"] as string == "gateway"
                && reachability["measured_at"] as string == HardwareTelemetryProjection.Utc(At.AddSeconds(-30)), "known_old_gateway_preserves_measurement");
            var staleGateway = Fake();
            staleGateway.NetworkHealth.GatewayMs = null; staleGateway.NetworkHealth.GatewayMeasuredAtUtc = null;
            root = Parse(HardwareTelemetryProjection.Capture(staleGateway, 1000, "fixture").Serialize(At.AddSeconds(1)));
            reachability = Metric(root, "network_reachability");
            Check(reachability["status"] as string == "unknown" && reachability["value"] == null && reachability["measured_at"] == null
                && reachability["scope"] as string == "not_measured" && reachability["provider_id"] == null, "stale_gateway_not_measured");
            var empty = new Sample { CollectedAtUtc = At };
            root = Parse(HardwareTelemetryProjection.Capture(empty, 1000, "fixture").Serialize(At.AddSeconds(1)));
            Check(root["status"] as string == "unavailable" && root["measured_at"] == null, "all_unknown");
            reachability = Metric(root, "network_reachability");
            Check(reachability["status"] as string == "unknown" && reachability["value"] == null && reachability["measured_at"] == null
                && reachability["scope"] as string == "not_measured" && reachability["provider_id"] == null, "empty_gateway_not_measured");
            File.WriteAllText(Path.Combine(output, "unavailable.json"), HardwareTelemetryProjection.Capture(empty, 1000, "fixture").Serialize(At.AddSeconds(1)));
            using (var publisher = new TelemetryPublisher(500, Path.Combine(output, "writer"), "fixture"))
            {
                var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
                publisher.Writer.Submit(_ => { entered.Set(); release.WaitOne(); return "{}"; }); Check(entered.WaitOne(3000), "publication_worker_started");
                sample = Fake(); publisher.Submit(sample); sample.Cpu.Total = 88; sample.NetworkDay.PingSent = 99; release.Set();
                Until(() => publisher.Writer.PublishedCount == 2, "post_submit_publish");
                root = Parse(File.ReadAllText(publisher.Writer.TargetPath)); Check(Json.Num(Metric(root, "cpu_usage_percent")["value"]) == 25, "mutation_after_submit");
                var pace = Stopwatch.StartNew(); long before = publisher.Writer.PublishedCount;
                for (int n = 0; n < 5000; n++) publisher.Submit(sample);
                Check(pace.ElapsedMilliseconds < 1500, "bounded_submit_pressure");
                Until(() => publisher.Writer.PublishedCount > before, "pressure_published");
                Check(publisher.Writer.PublishedCount - before <= 2, "one_hz_cap");
                entered.Dispose(); release.Dispose();
            }
            // Original helper timestamps survive parsing, rather than read time being substituted.
            string sensor; DateTime? measured;
            string helper = "{\"schema\":1,\"userSid\":\"S-1-5-21-test\",\"utc\":\"2026-10-18T22:59:58.0000000Z\",\"temperatureC\":50,\"sensor\":\"CPU\",\"status\":\"ok\"}";
            Check(LocalSensorSnapshot.Parse(helper, "S-1-5-21-test", At, out sensor, out measured) == 50 && measured == At.AddSeconds(-2), "helper_timestamp_preserved");
            Console.WriteLine("PASS resources-publisher " + checks + " assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
    }

    static int LiveSmoke(string output)
    {
        Directory.CreateDirectory(output);
        Program.BaseDir = output;
        foreach (int interval in new[] { 500, 2500 })
        {
            var config = new Config { IntervalMs = interval, HistorySeconds = 20, LhmUrl = "", PingHost = "", TopProcesses = 1 };
            using (var sampler = new Sampler(config, null, true))
            {
                var field = typeof(Sampler).GetField("telemetry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Check(field.GetValue(sampler) == null, "readonly_sampler_has_no_default_publisher");
                // Test-only reflection binds the real normal Loop to an explicitly isolated live writer.
                // There is no production command-line switch, daemon or alternate collector.
                var publisher = new TelemetryPublisher(interval, Path.Combine(output, "interval-" + interval), "live");
                field.SetValue(sampler, publisher);
                int updates = 0; sampler.Updated += _ => Interlocked.Increment(ref updates);
                sampler.Detailed = true;
                sampler.Start();
                Thread.Sleep(interval == 500 ? 2200 : 2800);
                sampler.Detailed = false;
                Thread.Sleep(interval == 500 ? 2400 : 2800);
                Check(updates >= (interval == 500 ? 5 : 2), "normal_loop_updates_" + interval);
                Check(publisher.Writer.PublishedCount >= 2, "hidden_loop_publishes_" + interval);
                Check(publisher.Writer.PublishedCount <= (interval == 500 ? 5 : updates), "interval_publication_bound_" + interval);
                using (var stream = new FileStream(publisher.Writer.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = new StreamReader(stream)) File.WriteAllText(Path.Combine(output, "sample-" + interval + ".json"), reader.ReadToEnd());
                Console.WriteLine("LIVE interval_ms=" + interval + " updates=" + updates + " publications=" + publisher.Writer.PublishedCount + " hidden=true no_ping=true");
            }
        }
        return 0;
    }
}
