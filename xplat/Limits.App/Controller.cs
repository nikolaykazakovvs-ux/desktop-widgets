using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DesktopWidgets.Limits.Core;
using DesktopWidgets.Limits.Core.Claude;
using DesktopWidgets.Limits.Platform;

namespace DesktopWidgets.Limits.App
{
    // Связка ядра и платформы: периодический опрос Claude, публикация телеметрии, повторный опрос после сна.
    public sealed class Controller : IDisposable
    {
        readonly ServiceConfig svc;
        readonly ClaudeCollector collector;
        readonly Timer refresh;
        readonly SemaphoreSlim busy = new SemaphoreSlim(1, 1);
        long generation;

        public readonly AppPaths Paths;
        public readonly ILog Log;
        public readonly QuotasPublisher Publisher;
        public readonly WakeDetector Wake;
        public readonly IProcessRunner Runner;
        public ServiceSnapshot Last { get; private set; }
        public TimeSpan LastDuration { get; private set; }
        public int Collections { get; private set; }
        public event Action<ServiceSnapshot> Updated;
        public event Action<TimeSpan> WokeUp;

        public Controller(Options o)
        {
            Paths = new AppPaths(o.TelemetryDir, o.ConfigDir);
            Log = new FileLog(Paths.ConfigDirectory);
            bool fixture = o.FixtureUsage != null;
            Runner = fixture ? new FixtureRunner(o.FixtureUsage) : new ProcessRunner(Log);
            svc = ServiceConfig.ClaudeDefaults();
            svc.Enabled = true;
            svc.Command = o.ClaudeCommand;
            collector = new ClaudeCollector(Runner, Paths, Log);
            Publisher = new QuotasPublisher(TelemetrySinks.Create(o.TelemetryDir, fixture), fixture, o.RefreshSeconds);
            Publisher.Configure(new[] { "claude" }, o.RefreshSeconds);
            Wake = new WakeDetector(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20));
            Wake.Woke += slept =>
            {
                Log.Write("пробуждение после ~" + (int)slept.TotalSeconds + " с — повторный опрос");
                WokeUp?.Invoke(slept);
                _ = RefreshAsync();
            };
            refresh = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(o.RefreshSeconds));
        }

        public async Task<ServiceSnapshot> RefreshAsync()
        {
            if (!await busy.WaitAsync(0)) return Last;   // опрос уже идёт — второй не запускаем
            try
            {
                var sw = Stopwatch.StartNew();
                var snap = await Task.Run(() => collector.Collect(svc, Interlocked.Increment(ref generation)));
                LastDuration = sw.Elapsed;
                Collections++;
                Publisher.Accept(snap);
                Last = snap;
                Log.Write($"claude: {snap.Status} {snap.StatusText}, полос {snap.Bars.Count}, {sw.ElapsedMilliseconds} мс");
                Dispatcher.UIThread.Post(() => Updated?.Invoke(snap));
                return snap;
            }
            catch (Exception ex)
            {
                Log.Write("опрос: " + ex.GetType().Name + ": " + ex.Message);
                return Last;
            }
            finally { busy.Release(); }
        }

        public void Dispose()
        {
            refresh.Dispose();
            Wake.Dispose();
            Publisher.Dispose();
        }
    }

    // Тестовый источник: ответ get_usage из файла вместо настоящего CLI (для снимков экрана без личного аккаунта).
    sealed class FixtureRunner : IProcessRunner
    {
        readonly string file;
        public FixtureRunner(string file) { this.file = file; }
        public string Resolve(string configured, string name) => "fixture:" + name;
        public CliResult Run(CliRequest r) => new CliResult { ExitCode = 0, StdOut = "{\"loggedIn\":true,\"subscriptionType\":\"max\"}" };
        public ICliSession Start(CliRequest r) => new Session(File.ReadAllText(file, Encoding.UTF8));

        sealed class Session : ICliSession
        {
            string response;
            public Session(string usage) =>
                response = "{\"type\":\"control_response\",\"response\":{\"request_id\":\"lw-usage\",\"subtype\":\"success\",\"response\":" + usage.Trim() + "}}";
            public void WriteLine(string line) { }
            public string ReadLine(int timeoutMs, out bool timedOut) { timedOut = false; var r = response; response = null; return r; }
            public string StdErrHint() => null;
            public void Dispose() { }
        }
    }
}
