using System;
using System.Diagnostics;
using System.Threading;
using DesktopWidgets.Limits.Core;
using DesktopWidgets.Limits.Platform.WindowsTelemetry;

namespace DesktopWidgets.Limits.Platform
{
    public static class TelemetrySinks
    {
        // Выбор писателя по ОС. Формат файла одинаковый (контракт 1.0.0), различается только защита каталога и файла.
        public static ITelemetrySink Create(string directory, bool fixture)
        {
            string mode = fixture ? "fixture" : "live";
            if (OperatingSystem.IsWindows())
                return new WindowsSink(new TelemetrySnapshotWriter(new TelemetryWriterOptions { DirectoryPath = directory, FileName = "quotas-snapshot.json", Mode = mode, MinIntervalMs = 1000 }));
            return new UnixTelemetryWriter(directory, "quotas-snapshot.json", mode, 1000);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        sealed class WindowsSink : ITelemetrySink
        {
            readonly TelemetrySnapshotWriter w;
            public WindowsSink(TelemetrySnapshotWriter w) { this.w = w; }
            public string TargetPath => w.TargetPath;
            public string LastError => w.LastError;
            public long PublishedCount => w.PublishedCount;
            public bool Submit(Func<DateTime, string> f) => w.Submit(f);
            public void Dispose() => w.Dispose();
        }
    }

    // Сон и пробуждение. Таймер ОС во сне не срабатывает, а настенные часы идут дальше: если между тиками прошло намного
    // больше периода, процесс проспал. Это одинаково работает на Windows, macOS и Linux без нативных подписок
    // (WM_POWERBROADCAST / NSWorkspaceDidWakeNotification / logind) — их стоит добавить при полном переносе для мгновенной реакции.
    public sealed class WakeDetector : IDisposable
    {
        readonly Timer timer;
        readonly Func<DateTime> utcNow;
        readonly TimeSpan period, threshold;
        readonly object gate = new object();
        DateTime last;

        public event Action<TimeSpan> Woke;   // аргумент — длительность сна (оценка)
        public int WakeCount { get; private set; }

        public WakeDetector(TimeSpan period, TimeSpan threshold, Func<DateTime> utcNow = null, bool startTimer = true)
        {
            this.period = period;
            this.threshold = threshold;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            last = this.utcNow();
            if (startTimer) timer = new Timer(_ => Tick(), null, period, period);
        }

        public void Tick()
        {
            TimeSpan gap;
            lock (gate)
            {
                var now = utcNow();
                gap = now - last;
                last = now;
            }
            if (gap > period + threshold)
            {
                WakeCount++;
                Woke?.Invoke(gap - period);
            }
        }

        public void Dispose() => timer?.Dispose();
    }

    public static class ProcessMemory
    {
        // Рабочий набор (RSS) и приватная память текущего процесса, МБ.
        public static (double WorkingSetMb, double PrivateMb, double ManagedMb) Sample()
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            return (p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0);
        }
    }
}
