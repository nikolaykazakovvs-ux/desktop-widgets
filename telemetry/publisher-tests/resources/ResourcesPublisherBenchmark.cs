using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using DesktopWidgets.Telemetry;
using ResourcesWidget;

// Test-only hooks referenced by the additively instrumented writer copy in the isolated run directory.
public static class TelemetryWriterBenchmarkProbe
{
    public const int Capacity = 30;
    public static AutoResetEvent WorkerReadyEvent;
    public static AutoResetEvent PublicationCompleteEvent;
    public static long WorkerThreadStartedAt, WorkerReadyAt;
    public static long[] OperationStartedAt, SerializationEndedAt, WriteStartedAt,
        InitialReparseEndedAt, DirectoryAclEndedAt,
        TempCreateStartedAt, TempCreateEndedAt, TempWriteEndedAt, TempFlushEndedAt, TempAclEndedAt, TempCloseEndedAt,
        FinalReparseStartedAt, FinalReparseEndedAt, TargetAclStartedAt, TargetAclEndedAt, FinalTargetGuardEndedAt,
        ReplaceOrMoveEndedAt, WriteEndedAt, CompletedAt;
    static int current;
    static bool inPublish;
    public static int SharingRetryCount, SharingRetryRequestedMs;
    public static long SharingRetryWaitTicks;
    static long retryStarted;

    public static void Prepare()
    {
        WorkerReadyEvent = new AutoResetEvent(false);
        PublicationCompleteEvent = new AutoResetEvent(false);
        OperationStartedAt = new long[Capacity];
        SerializationEndedAt = new long[Capacity];
        WriteStartedAt = new long[Capacity];
        InitialReparseEndedAt = new long[Capacity];
        DirectoryAclEndedAt = new long[Capacity];
        TempCreateStartedAt = new long[Capacity];
        TempCreateEndedAt = new long[Capacity];
        TempWriteEndedAt = new long[Capacity];
        TempFlushEndedAt = new long[Capacity];
        TempAclEndedAt = new long[Capacity];
        TempCloseEndedAt = new long[Capacity];
        FinalReparseStartedAt = new long[Capacity];
        FinalReparseEndedAt = new long[Capacity];
        TargetAclStartedAt = new long[Capacity];
        TargetAclEndedAt = new long[Capacity];
        FinalTargetGuardEndedAt = new long[Capacity];
        ReplaceOrMoveEndedAt = new long[Capacity];
        WriteEndedAt = new long[Capacity];
        CompletedAt = new long[Capacity];
        WorkerThreadStartedAt = WorkerReadyAt = 0;
        current = 0;
        inPublish = false;
        SharingRetryCount = SharingRetryRequestedMs = 0;
        SharingRetryWaitTicks = 0;
    }

    public static void Dispose()
    {
        if (WorkerReadyEvent != null) WorkerReadyEvent.Dispose();
        if (PublicationCompleteEvent != null) PublicationCompleteEvent.Dispose();
    }

    public static void OnWorkerThreadStarted() { WorkerThreadStartedAt = Stopwatch.GetTimestamp(); }
    public static void OnWorkerReady() { WorkerReadyAt = Stopwatch.GetTimestamp(); WorkerReadyEvent.Set(); }
    public static void OnOperationStart() { OperationStartedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnSerializationEnd() { SerializationEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnWriteStart() { inPublish = true; WriteStartedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnInitialReparseEnd() { InitialReparseEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnDirectoryAclEnd() { DirectoryAclEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnTempCreateStart() { TempCreateStartedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnTempCreateEnd() { TempCreateEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnTempWriteEnd() { TempWriteEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnTempFlushEnd() { TempFlushEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnTempAclEnd() { TempAclEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnTempCloseEnd() { TempCloseEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnFinalReparseStart()
    {
        TargetAclStartedAt[current] = 0;
        TargetAclEndedAt[current] = 0;
        FinalReparseStartedAt[current] = Stopwatch.GetTimestamp();
    }
    public static void OnFinalReparseEnd() { FinalReparseEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnTargetAclStart() { if (inPublish) TargetAclStartedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnTargetAclEnd() { if (inPublish) TargetAclEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnFinalTargetGuardEnd()
    {
        long at = Stopwatch.GetTimestamp();
        if (TargetAclStartedAt[current] == 0)
        {
            TargetAclStartedAt[current] = at;
            TargetAclEndedAt[current] = at;
        }
        FinalTargetGuardEndedAt[current] = at;
    }
    public static void OnReplaceOrMoveEnd() { ReplaceOrMoveEndedAt[current] = Stopwatch.GetTimestamp(); }
    public static void OnWriteEnd() { WriteEndedAt[current] = Stopwatch.GetTimestamp(); inPublish = false; }
    public static void OnRetryStart(int milliseconds) { SharingRetryCount++; SharingRetryRequestedMs += milliseconds; retryStarted = Stopwatch.GetTimestamp(); }
    public static void OnRetryEnd() { SharingRetryWaitTicks += Stopwatch.GetTimestamp() - retryStarted; }
    public static void OnPublicationComplete()
    {
        long at = Stopwatch.GetTimestamp();
        CompletedAt[current] = at;
        current++;
        PublicationCompleteEvent.Set();
    }
}

// Finite synthetic measurement of the actual production projection + additively instrumented actual .NET file writer.
// There are no Sampler/sensor/UI/provider objects and no default LocalAppData publication.
static class ResourcesPublisherBenchmark
{
    const int PureCount = 50, PublicationCount = TelemetryWriterBenchmarkProbe.Capacity, RunBudgetMs = 45000;
    static readonly DateTime SourceAt = new DateTime(2026, 10, 18, 23, 0, 0, DateTimeKind.Utc);

    sealed class Observation
    {
        public long CaptureStart, Ready, Submitted, ObserverWoke;
    }

    static Sample Fixture()
    {
        return new Sample {
            Time = SourceAt.ToLocalTime(), CollectedAtUtc = SourceAt,
            Cpu = new CpuInfo { Total = 25, UsageMeasuredAtUtc = SourceAt, Temp = 50, TempMeasuredAtUtc = SourceAt.AddSeconds(-2) },
            Mem = new MemInfo { Total = 34359738368L, Available = 17179869184L, CommitTotal = 25769803776L, CommitLimit = 68719476736L, MeasuredAtUtc = SourceAt },
            Gpu = new GpuInfo { Load = 20, UsageMeasuredAtUtc = SourceAt, Temp = 55, TempMeasuredAtUtc = SourceAt.AddSeconds(-3), MemTotal = 8589934592L, MemUsed = 2147483648L, MemoryMeasuredAtUtc = SourceAt },
            Net = new NetInfo { DownBps = 65536, UpBps = 8192, MeasuredAtUtc = SourceAt },
            NetworkHealth = new NetHealth { PingMs = 20, GatewayMs = 2, PingMeasuredAtUtc = SourceAt.AddSeconds(-1), GatewayMeasuredAtUtc = SourceAt.AddSeconds(-2), ProbeIntervalMs = 2000,
                ObservationAtUtc = SourceAt, WindowStartUtc = SourceAt.Date.AddHours(21), WindowEndUtc = SourceAt.Date.AddDays(1).AddHours(21) },
            NetworkDay = new DayPeaks { Seconds = 3600, PingSent = 100, PingLost = 1, GwSent = 100, GwLost = 0, Outages = 1, Disconnects = 0 }
        };
    }

    static long Clock() { return Stopwatch.GetTimestamp(); }
    static double Ms(long start, long end) { return (end - start) * 1000.0 / Stopwatch.Frequency; }
    static double Mean(List<double> values) { double total = 0; foreach (var value in values) total += value; return total / values.Count; }
    static Dictionary<string, object> Distribution(List<double> values)
    {
        var ordered = values.ToArray(); Array.Sort(ordered);
        return new Dictionary<string, object> { { "n", ordered.Length }, { "unit", "milliseconds" }, { "p50", ordered[(int)Math.Ceiling(ordered.Length * 0.50) - 1] },
            { "p95", ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1] }, { "min", ordered[0] }, { "max", ordered[ordered.Length - 1] }, { "mean", Mean(values) } };
    }

    static List<double> Segment(long[] starts, long[] ends, int first, int count)
    {
        var values = new List<double>();
        for (int n = first; n < first + count; n++) values.Add(Ms(starts[n], ends[n]));
        return values;
    }

    static List<double> CombinedSegment(long[] startsA, long[] endsA, long[] startsB, long[] endsB, int first, int count)
    {
        var values = new List<double>();
        for (int n = first; n < first + count; n++) values.Add(Ms(startsA[n], endsA[n]) + Ms(startsB[n], endsB[n]));
        return values;
    }

    static List<double> Intervals(long[] timestamps, int first, int count)
    {
        var values = new List<double>();
        for (int n = first + 1; n < first + count; n++) values.Add(Ms(timestamps[n - 1], timestamps[n]));
        return values;
    }

    static Dictionary<string, object> PublicationMetrics(Observation[] observations, int first, int count)
    {
        var capture = new List<double>(); var submit = new List<double>(); var captureSubmit = new List<double>();
        var waitForWorker = new List<double>(); var observer = new List<double>();
        for (int n = first; n < first + count; n++)
        {
            var observation = observations[n];
            capture.Add(Ms(observation.CaptureStart, observation.Ready));
            submit.Add(Ms(observation.Ready, observation.Submitted));
            captureSubmit.Add(Ms(observation.CaptureStart, observation.Submitted));
            waitForWorker.Add(Ms(observation.Submitted, TelemetryWriterBenchmarkProbe.OperationStartedAt[n]));
            observer.Add(Ms(TelemetryWriterBenchmarkProbe.CompletedAt[n], observation.ObserverWoke));
        }
        var publishPhases = new Dictionary<string, object> {
            { "ancestor_reparse_initial", Distribution(Segment(TelemetryWriterBenchmarkProbe.WriteStartedAt, TelemetryWriterBenchmarkProbe.InitialReparseEndedAt, first, count)) },
            { "directory_acl_owner", Distribution(Segment(TelemetryWriterBenchmarkProbe.InitialReparseEndedAt, TelemetryWriterBenchmarkProbe.DirectoryAclEndedAt, first, count)) },
            { "temp_name_setup", Distribution(Segment(TelemetryWriterBenchmarkProbe.DirectoryAclEndedAt, TelemetryWriterBenchmarkProbe.TempCreateStartedAt, first, count)) },
            { "temp_create", Distribution(Segment(TelemetryWriterBenchmarkProbe.TempCreateStartedAt, TelemetryWriterBenchmarkProbe.TempCreateEndedAt, first, count)) },
            { "temp_write", Distribution(Segment(TelemetryWriterBenchmarkProbe.TempCreateEndedAt, TelemetryWriterBenchmarkProbe.TempWriteEndedAt, first, count)) },
            { "temp_flush", Distribution(Segment(TelemetryWriterBenchmarkProbe.TempWriteEndedAt, TelemetryWriterBenchmarkProbe.TempFlushEndedAt, first, count)) },
            { "temp_acl_owner", Distribution(Segment(TelemetryWriterBenchmarkProbe.TempFlushEndedAt, TelemetryWriterBenchmarkProbe.TempAclEndedAt, first, count)) },
            { "temp_close", Distribution(Segment(TelemetryWriterBenchmarkProbe.TempAclEndedAt, TelemetryWriterBenchmarkProbe.TempCloseEndedAt, first, count)) },
            { "ancestor_reparse_final", Distribution(Segment(TelemetryWriterBenchmarkProbe.FinalReparseStartedAt, TelemetryWriterBenchmarkProbe.FinalReparseEndedAt, first, count)) },
            { "ancestor_reparse_checks_total", Distribution(CombinedSegment(TelemetryWriterBenchmarkProbe.WriteStartedAt, TelemetryWriterBenchmarkProbe.InitialReparseEndedAt, TelemetryWriterBenchmarkProbe.FinalReparseStartedAt, TelemetryWriterBenchmarkProbe.FinalReparseEndedAt, first, count)) },
            { "final_target_guards_total", Distribution(Segment(TelemetryWriterBenchmarkProbe.FinalReparseEndedAt, TelemetryWriterBenchmarkProbe.FinalTargetGuardEndedAt, first, count)) },
            { "final_target_exists_reparse", Distribution(Segment(TelemetryWriterBenchmarkProbe.FinalReparseEndedAt, TelemetryWriterBenchmarkProbe.TargetAclStartedAt, first, count)) },
            { "final_target_acl_owner", Distribution(Segment(TelemetryWriterBenchmarkProbe.TargetAclStartedAt, TelemetryWriterBenchmarkProbe.TargetAclEndedAt, first, count)) },
            { "replace_or_move", Distribution(Segment(TelemetryWriterBenchmarkProbe.FinalTargetGuardEndedAt, TelemetryWriterBenchmarkProbe.ReplaceOrMoveEndedAt, first, count)) },
            { "finally_cleanup_check", Distribution(Segment(TelemetryWriterBenchmarkProbe.ReplaceOrMoveEndedAt, TelemetryWriterBenchmarkProbe.WriteEndedAt, first, count)) }
        };
        var result = new Dictionary<string, object> {
            { "publication_n", count },
            { "capture", Distribution(capture) }, { "submit_only", Distribution(submit) }, { "capture_and_submit", Distribution(captureSubmit) },
            { "submit_to_worker_operation_start_including_rate_wait", Distribution(waitForWorker) },
            { "worker_serialization", Distribution(Segment(TelemetryWriterBenchmarkProbe.OperationStartedAt, TelemetryWriterBenchmarkProbe.SerializationEndedAt, first, count)) },
            { "worker_utf8_encode_validate", Distribution(Segment(TelemetryWriterBenchmarkProbe.SerializationEndedAt, TelemetryWriterBenchmarkProbe.WriteStartedAt, first, count)) },
            { "writer_publish", Distribution(Segment(TelemetryWriterBenchmarkProbe.WriteStartedAt, TelemetryWriterBenchmarkProbe.WriteEndedAt, first, count)) },
            { "publish_phases", publishPhases },
            { "worker_operation", Distribution(Segment(TelemetryWriterBenchmarkProbe.OperationStartedAt, TelemetryWriterBenchmarkProbe.CompletedAt, first, count)) },
            { "completion_notification_to_observer", Distribution(observer) }
        };
        if (count > 1) result.Add("worker_operation_start_interval", Distribution(Intervals(TelemetryWriterBenchmarkProbe.OperationStartedAt, first, count)));
        return result;
    }

    static List<Dictionary<string, object>> MonotonicPhaseTraces(int count)
    {
        var traces = new List<Dictionary<string, object>>();
        for (int n = 0; n < count; n++)
        {
            if (!(TelemetryWriterBenchmarkProbe.OperationStartedAt[n] <= TelemetryWriterBenchmarkProbe.SerializationEndedAt[n]
                && TelemetryWriterBenchmarkProbe.SerializationEndedAt[n] <= TelemetryWriterBenchmarkProbe.WriteStartedAt[n]
                && TelemetryWriterBenchmarkProbe.WriteStartedAt[n] <= TelemetryWriterBenchmarkProbe.InitialReparseEndedAt[n]
                && TelemetryWriterBenchmarkProbe.InitialReparseEndedAt[n] <= TelemetryWriterBenchmarkProbe.DirectoryAclEndedAt[n]
                && TelemetryWriterBenchmarkProbe.DirectoryAclEndedAt[n] <= TelemetryWriterBenchmarkProbe.TempCreateStartedAt[n]
                && TelemetryWriterBenchmarkProbe.TempCreateStartedAt[n] <= TelemetryWriterBenchmarkProbe.TempCreateEndedAt[n]
                && TelemetryWriterBenchmarkProbe.TempCreateEndedAt[n] <= TelemetryWriterBenchmarkProbe.TempWriteEndedAt[n]
                && TelemetryWriterBenchmarkProbe.TempWriteEndedAt[n] <= TelemetryWriterBenchmarkProbe.TempFlushEndedAt[n]
                && TelemetryWriterBenchmarkProbe.TempFlushEndedAt[n] <= TelemetryWriterBenchmarkProbe.TempAclEndedAt[n]
                && TelemetryWriterBenchmarkProbe.TempAclEndedAt[n] <= TelemetryWriterBenchmarkProbe.TempCloseEndedAt[n]
                && TelemetryWriterBenchmarkProbe.TempCloseEndedAt[n] <= TelemetryWriterBenchmarkProbe.FinalReparseStartedAt[n]
                && TelemetryWriterBenchmarkProbe.FinalReparseStartedAt[n] <= TelemetryWriterBenchmarkProbe.FinalReparseEndedAt[n]
                && TelemetryWriterBenchmarkProbe.FinalReparseEndedAt[n] <= TelemetryWriterBenchmarkProbe.TargetAclStartedAt[n]
                && TelemetryWriterBenchmarkProbe.TargetAclStartedAt[n] <= TelemetryWriterBenchmarkProbe.TargetAclEndedAt[n]
                && TelemetryWriterBenchmarkProbe.TargetAclEndedAt[n] <= TelemetryWriterBenchmarkProbe.FinalTargetGuardEndedAt[n]
                && TelemetryWriterBenchmarkProbe.FinalTargetGuardEndedAt[n] <= TelemetryWriterBenchmarkProbe.ReplaceOrMoveEndedAt[n]
                && TelemetryWriterBenchmarkProbe.ReplaceOrMoveEndedAt[n] <= TelemetryWriterBenchmarkProbe.WriteEndedAt[n]
                && TelemetryWriterBenchmarkProbe.WriteEndedAt[n] <= TelemetryWriterBenchmarkProbe.CompletedAt[n]))
            {
                throw new Exception("non_monotonic_phase_timestamps_at_" + n);
            }
            traces.Add(new Dictionary<string, object> {
                { "publication_index", n },
                { "operation_start_ticks", TelemetryWriterBenchmarkProbe.OperationStartedAt[n] },
                { "serialization_end_ticks", TelemetryWriterBenchmarkProbe.SerializationEndedAt[n] },
                { "write_start_ticks", TelemetryWriterBenchmarkProbe.WriteStartedAt[n] },
                { "initial_reparse_end_ticks", TelemetryWriterBenchmarkProbe.InitialReparseEndedAt[n] },
                { "directory_acl_end_ticks", TelemetryWriterBenchmarkProbe.DirectoryAclEndedAt[n] },
                { "temp_create_start_ticks", TelemetryWriterBenchmarkProbe.TempCreateStartedAt[n] },
                { "temp_create_end_ticks", TelemetryWriterBenchmarkProbe.TempCreateEndedAt[n] },
                { "temp_write_end_ticks", TelemetryWriterBenchmarkProbe.TempWriteEndedAt[n] },
                { "temp_flush_end_ticks", TelemetryWriterBenchmarkProbe.TempFlushEndedAt[n] },
                { "temp_acl_end_ticks", TelemetryWriterBenchmarkProbe.TempAclEndedAt[n] },
                { "temp_close_end_ticks", TelemetryWriterBenchmarkProbe.TempCloseEndedAt[n] },
                { "final_reparse_start_ticks", TelemetryWriterBenchmarkProbe.FinalReparseStartedAt[n] },
                { "final_reparse_end_ticks", TelemetryWriterBenchmarkProbe.FinalReparseEndedAt[n] },
                { "target_acl_start_ticks", TelemetryWriterBenchmarkProbe.TargetAclStartedAt[n] },
                { "target_acl_end_ticks", TelemetryWriterBenchmarkProbe.TargetAclEndedAt[n] },
                { "final_target_guard_end_ticks", TelemetryWriterBenchmarkProbe.FinalTargetGuardEndedAt[n] },
                { "replace_or_move_end_ticks", TelemetryWriterBenchmarkProbe.ReplaceOrMoveEndedAt[n] },
                { "write_end_ticks", TelemetryWriterBenchmarkProbe.WriteEndedAt[n] },
                { "completed_ticks", TelemetryWriterBenchmarkProbe.CompletedAt[n] }
            });
        }
        return traces;
    }

    static TimeSpan OwnProcessCpu()
    {
        using (var process = Process.GetCurrentProcess()) return process.TotalProcessorTime;
    }

    static Dictionary<string, object> RssSample(string scope)
    {
        using (var process = Process.GetCurrentProcess())
            return new Dictionary<string, object> { { "scope", scope }, { "working_set_bytes", process.WorkingSet64 } };
    }

    static int Remaining(Stopwatch whole) { return Math.Max(1, RunBudgetMs - (int)whole.ElapsedMilliseconds); }

    static void PublishOne(TelemetrySnapshotWriter writer, Sample sample, Observation[] observations, int n, Stopwatch whole)
    {
        var observation = new Observation(); observations[n] = observation;
        observation.CaptureStart = Clock();
        var frozen = HardwareTelemetryProjection.Capture(sample, 1000, "fixture");
        observation.Ready = Clock();
        if (!writer.Submit(frozen.Serialize)) throw new Exception("submit_rejected");
        observation.Submitted = Clock();
        if (!TelemetryWriterBenchmarkProbe.PublicationCompleteEvent.WaitOne(Remaining(whole)))
            throw new Exception("publication_timeout:" + (writer.LastError ?? "none"));
        observation.ObserverWoke = Clock();
    }

    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("usage: ResourcesPublisherBenchmark.exe RUN_DIRECTORY ORIGINAL_WRITER_SHA256");
            string root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
            var whole = Stopwatch.StartNew();
            var sample = Fixture();
            // Warm JIT for the pure mapping/serializer only. Writer startup and first publication remain separate.
            HardwareTelemetryProjection.Capture(sample, 1000, "fixture").Serialize(DateTime.UtcNow);
            var capture = new List<double>(); var serialize = new List<double>();
            int bytes = 0;
            for (int n = 0; n < PureCount; n++)
            {
                long start = Clock(); var value = HardwareTelemetryProjection.Capture(sample, 1000, "fixture"); long mapped = Clock();
                string json = value.Serialize(DateTime.UtcNow); long end = Clock();
                capture.Add(Ms(start, mapped)); serialize.Add(Ms(mapped, end)); bytes = System.Text.Encoding.UTF8.GetByteCount(json);
            }

            TelemetryWriterBenchmarkProbe.Prepare();
            try
            {
                var rss = new List<Dictionary<string, object>>();
                rss.Add(RssSample("before_cold_startup"));
                TimeSpan cpuColdStart = OwnProcessCpu();
                long constructorStart = Clock();
                using (var writer = new TelemetrySnapshotWriter(new TelemetryWriterOptions { DirectoryPath = Path.Combine(root, "snapshot"), FileName = "hardware-snapshot.json", Mode = "fixture", MinIntervalMs = 1000 }))
                {
                    if (!TelemetryWriterBenchmarkProbe.WorkerReadyEvent.WaitOne(Remaining(whole)))
                        throw new Exception("writer_ready_timeout:" + (writer.LastError ?? "none"));
                    long readyObserved = Clock();
                    TimeSpan cpuColdEnd = OwnProcessCpu();
                    rss.Add(RssSample("after_cold_startup"));

                    var observations = new Observation[PublicationCount];
                    TimeSpan cpuFirstStart = OwnProcessCpu();
                    PublishOne(writer, sample, observations, 0, whole);
                    TimeSpan cpuFirstEnd = OwnProcessCpu();
                    rss.Add(RssSample("after_first_publication"));

                    TimeSpan cpuSteadyStart = OwnProcessCpu();
                    for (int n = 1; n < PublicationCount; n++) PublishOne(writer, sample, observations, n, whole);
                    TimeSpan cpuSteadyEnd = OwnProcessCpu();
                    rss.Add(RssSample("after_steady_publications"));

                    var phaseTraces = MonotonicPhaseTraces(PublicationCount);
                    var coldStartup = new Dictionary<string, object> {
                        { "constructor_to_worker_ready_ms", Ms(constructorStart, TelemetryWriterBenchmarkProbe.WorkerReadyAt) },
                        { "worker_entry_to_ready_ms", Ms(TelemetryWriterBenchmarkProbe.WorkerThreadStartedAt, TelemetryWriterBenchmarkProbe.WorkerReadyAt) },
                        { "ready_notification_to_observer_ms", Ms(TelemetryWriterBenchmarkProbe.WorkerReadyAt, readyObserved) }
                    };
                    double steadyCpu = (cpuSteadyEnd - cpuSteadyStart).TotalMilliseconds;
                    var cpu = new Dictionary<string, object> {
                        { "cold_startup_ms", (cpuColdEnd - cpuColdStart).TotalMilliseconds },
                        { "cold_first_publication_ms", (cpuFirstEnd - cpuFirstStart).TotalMilliseconds },
                        { "steady_publication_n", PublicationCount - 1 },
                        { "steady_publications_total_ms", steadyCpu },
                        { "steady_publications_mean_per_publication_ms", steadyCpu / (PublicationCount - 1) },
                        { "scope", "whole process/all threads: main Capture/Submit + writer + runtime background; startup scope includes constructor/ready wait; publication scopes include event waits and 1 Hz idle rate waits" },
                        { "exclusions", "pure microbenchmark, report/readback and every WorkingSet64/RSS sample are outside these CPU intervals; no Process.Refresh is used" },
                        { "granularity", "Windows TotalProcessorTime is quantized; TimeSpan 100ns ticks are representation, not actual measurement resolution; raw deltas are reported without calibration subtraction" },
                        { "boundary_note", "fresh TotalProcessorTime samples occur only at scope boundaries; residual boundary/observer cost is not subtracted" }
                    };
                    var report = new Dictionary<string, object> {
                        { "kind", "resources-publisher-benchmark" }, { "mode", "fixture" }, { "generated_at", HardwareTelemetryProjection.Utc(DateTime.UtcNow) },
                        { "scope", "actual HardwareTelemetryProjection.Capture/Serialize + additively instrumented copy of actual TelemetrySnapshotWriter logic; synthetic frozen projections; no sensors/UI/providers/network" },
                        { "clock", "Stopwatch.GetTimestamp monotonic; nearest-rank percentiles from 30 publications (29 steady) are descriptive, not a hard bound" },
                        { "stopwatch_frequency", Stopwatch.Frequency },
                        { "writer_source_sha256", args[1] }, { "instrumentation_manifest", "benchmark-instrumentation.json" },
                        { "instrumentation_note", "only additive Stopwatch/event hooks in the isolated copy; hook/timestamp overhead remains in the respective wall scopes and is not subtracted" },
                        { "wall_scope_note", "writer_publish includes real ACL checks, temp write/flush, Move/Replace, retry delay and cleanup; publish_phases decomposes writer_publish into monotonic sub-phases; worker_operation adds serialization, UTF-8 and publication bookkeeping" },
                        { "writer_min_interval_ms", 1000 }, { "publication_n", PublicationCount }, { "snapshot_utf8_bytes", bytes },
                        { "sharing_retry_count", TelemetryWriterBenchmarkProbe.SharingRetryCount },
                        { "sharing_retry_requested_ms", TelemetryWriterBenchmarkProbe.SharingRetryRequestedMs },
                        { "sharing_retry_observed_wait_ms", TelemetryWriterBenchmarkProbe.SharingRetryWaitTicks * 1000.0 / Stopwatch.Frequency },
                        { "pure_scope", "n=50 Capture and n=50 Serialize after one untimed projection/serializer warm-up; no writer/file/process observation inside these intervals" },
                        { "pure_capture", Distribution(capture) }, { "pure_serialize", Distribution(serialize) },
                        { "cold_startup", coldStartup },
                        { "cold_first_publication", PublicationMetrics(observations, 0, 1) },
                        { "steady_publications", PublicationMetrics(observations, 1, PublicationCount - 1) },
                        { "all_publications", PublicationMetrics(observations, 0, PublicationCount) },
                        { "publication_monotonic_timestamps", phaseTraces },
                        { "own_process_cpu", cpu },
                        { "sampled_working_set", rss },
                        { "working_set_note", "four sampled whole-process RSS observations at scope boundaries, not peak and not writer-exclusive allocation" },
                        { "observer_note", "observer uses blocking AutoResetEvent waits, no Thread.Sleep and no PublishedCount polling; worker_operation ends before completion Set, and completion_notification_to_observer is separate; real writer retry sleep remains measured" },
                        { "elapsed_ms", whole.Elapsed.TotalMilliseconds }
                    };
                    File.WriteAllText(Path.Combine(root, "benchmark-results.json"), Json.Serialize(report, true));
                    Console.WriteLine(Json.Serialize(report, false));

                    // Validate one complete file outside every timed writer/CPU scope.
                    if (writer.LastError != null || writer.PublishedCount != PublicationCount) throw new Exception("publication_state_invalid");
                    var document = Json.Obj(Json.TryParse(File.ReadAllText(writer.TargetPath)));
                    if (document == null || document["mode"] as string != "fixture") throw new Exception("readback_invalid");
                }
            }
            finally { TelemetryWriterBenchmarkProbe.Dispose(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL benchmark " + ex.Message); return 1; }
    }
}
