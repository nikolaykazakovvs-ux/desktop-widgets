using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DesktopWidgets.Limits.Core;
using DesktopWidgets.Limits.Core.Claude;
using DesktopWidgets.Limits.Platform;
using Xunit;

namespace DesktopWidgets.Limits.Tests
{
    sealed class NullLog : ILog { public readonly List<string> Lines = new List<string>(); public void Write(string line) { lock (Lines) Lines.Add(line); } }

    public class ProcessRunnerTests
    {
        static (string Exe, string[] Args) Shell(string script) => OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe", new[] { "/d", "/c", script })
            : ("/bin/sh", new[] { "-c", script });

        [Fact]
        public void ResolvesSystemShellAndMissesUnknownCli()
        {
            var r = new ProcessRunner(new NullLog());
            Assert.NotNull(r.Resolve(null, OperatingSystem.IsWindows() ? "cmd" : "sh"));
            Assert.Null(r.Resolve(null, "definitely-not-a-cli-" + Guid.NewGuid().ToString("N")));
            Assert.Null(r.Resolve("/no/such/path/claude", "claude"));
        }

        [Fact]
        public void UnknownCliMissIsCachedNotResearchedEveryPoll()
        {
            var r = new ProcessRunner(new NullLog());
            string name = "missing-" + Guid.NewGuid().ToString("N");
            r.Resolve(null, name); r.Resolve(null, name); r.Resolve(null, name);
            Assert.Equal(1, r.Searches);
        }

        [Fact]
        public void GuiPathIncludesUserCliDirectories()
        {
            if (OperatingSystem.IsWindows()) return;
            var dirs = ProcessRunner.SearchDirectories();
            Assert.Contains(Path.Combine(Os.Home, ".local", "bin"), dirs);
            Assert.Contains("/opt/homebrew/bin", dirs);
        }

        [Fact]
        public void RunCapturesOutputAndDropsClaudeEnvironment()
        {
            Environment.SetEnvironmentVariable("CLAUDE_XPLAT_CANARY", "leak");
            var (exe, args) = Shell(OperatingSystem.IsWindows() ? "echo hello& set" : "echo hello; env");
            var res = new ProcessRunner(new NullLog()).Run(new CliRequest { Exe = exe, Args = args, TimeoutMs = 10000, DropEnvPrefixes = new[] { "CLAUDE" } });
            Assert.True(res.Ok, res.Describe());
            Assert.Contains("hello", res.StdOut);
            Assert.DoesNotContain("CLAUDE_XPLAT_CANARY", res.StdOut);
        }

        [Fact]
        public void TimeoutKillsWholeProcessTree()
        {
            string marker = "xplat" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var (exe, args) = OperatingSystem.IsWindows()
                ? Shell("ping -n 30 127.0.0.1 >nul")
                : Shell("sleep 30 & sleep 31; echo " + marker);
            var sw = Stopwatch.StartNew();
            var res = new ProcessRunner(new NullLog()).Run(new CliRequest { Exe = exe, Args = args, TimeoutMs = 700 });
            Assert.True(res.TimedOut);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
            if (!OperatingSystem.IsWindows())
            {
                Thread.Sleep(300);
                var ps = Process.Start(new ProcessStartInfo("/bin/sh", new[] { "-c", "ps -ax -o command | grep -c '^sleep 3[01]$' || true" }) { RedirectStandardOutput = true });
                string count = ps.StandardOutput.ReadToEnd().Trim();
                ps.WaitForExit();
                // Другие тесты тоже могут запускать sleep; главное — дети нашего дерева не пережили тайм-аут.
                Assert.Equal("0", count);
            }
        }

        [Fact]
        public void StartErrorIsReported()
        {
            var res = new ProcessRunner(new NullLog()).Run(new CliRequest { Exe = Path.Combine(Path.GetTempPath(), "no-such-exe-" + Guid.NewGuid().ToString("N")) });
            Assert.NotNull(res.StartError);
            Assert.False(res.Ok);
        }
    }

    public class CollectorTests
    {
        sealed class FakeRunner : IProcessRunner
        {
            public string Exe = "/fake/claude", UsageResponse, UsageText = "", AuthStatus = "{\"loggedIn\":true}", AuthJson = "{\"loggedIn\":true,\"subscriptionType\":\"pro\"}";
            public readonly List<string> Calls = new List<string>();
            public string Resolve(string configured, string name) => Exe;
            public CliResult Run(CliRequest r)
            {
                string a = string.Join(" ", r.Args);
                Calls.Add(a);
                if (a.StartsWith("-p /usage")) return new CliResult { ExitCode = 0, StdOut = UsageText };
                if (a == "auth status --json") return new CliResult { ExitCode = 0, StdOut = AuthJson };
                if (a == "auth status") return new CliResult { ExitCode = 0, StdOut = AuthStatus };
                return new CliResult { StartError = "unexpected" };
            }
            public ICliSession Start(CliRequest r) { Calls.Add("session"); return new S(UsageResponse); }
            sealed class S : ICliSession
            {
                string line;
                public S(string usage) => line = usage == null ? null : "{\"type\":\"control_response\",\"response\":{\"request_id\":\"lw-usage\",\"subtype\":\"success\",\"response\":" + usage + "}}";
                public void WriteLine(string l) { }
                public string ReadLine(int t, out bool timedOut) { timedOut = false; var x = line; line = null; return x; }
                public string StdErrHint() => null;
                public void Dispose() { }
            }
        }

        sealed class Paths : IAppPaths
        {
            public string History;
            public string ConfigDirectory => Path.GetTempPath();
            public string TelemetryDirectory => Path.GetTempPath();
            public IEnumerable<string> ClaudeDesktopHistoryCandidates() => History == null ? Array.Empty<string>() : new[] { History };
        }

        static ServiceConfig Svc() { var s = ServiceConfig.ClaudeDefaults(); s.Enabled = true; return s; }

        [Fact]
        public void GetUsagePathWithAutomaticPlan()
        {
            var fake = new FakeRunner { UsageResponse = "{\"rate_limits\":{\"five_hour\":{\"utilization\":10},\"seven_day\":{\"utilization\":20}}}" };
            var c = new ClaudeCollector(fake, new Paths(), new NullLog());
            var s = c.Collect(Svc(), 1);
            Assert.Equal(SvcStatus.Ok, s.Status);
            Assert.Equal(2, s.Bars.Count);
            Assert.Equal("Pro", s.Plan);
            Assert.NotNull(s.LastAttemptAt);
            c.Collect(Svc(), 2);
            Assert.Equal(1, c.AuthQueries);   // тариф кэшируется, CLI не плодится
        }

        [Fact]
        public void FallsBackToUsageText()
        {
            var fake = new FakeRunner { UsageResponse = null, UsageText = "Current session: 5% used\nCurrent week (all models): 7% used" };
            var s = new ClaudeCollector(fake, new Paths(), new NullLog()).Collect(Svc(), 1);
            Assert.Equal(SvcStatus.Ok, s.Status);
            Assert.Contains(fake.Calls, x => x.StartsWith("-p /usage"));
            Assert.Equal(2, s.Bars.Count);
        }

        [Fact]
        public void MissingCliGivesPlaceholdersNotZero()
        {
            var fake = new FakeRunner { Exe = null };
            var s = new ClaudeCollector(fake, new Paths(), new NullLog()).Collect(Svc(), 1);
            Assert.Equal(SvcStatus.NoData, s.Status);
            Assert.All(s.Bars, b => { Assert.True(b.Placeholder); Assert.False(b.Used.HasValue); });
        }

        [Fact]
        public void LoggedOutUsesDesktopHistoryAndOffersLogin()
        {
            string hist = Path.Combine(Path.GetTempPath(), "hist-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(hist, "{\"samples\":[{\"t\":" + DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds() + ",\"u\":{\"fh\":11,\"sd\":22}}]}");
            var fake = new FakeRunner { UsageResponse = null, UsageText = "Total cost: $0.00", AuthStatus = "{\"loggedIn\":false}" };
            var s = new ClaudeCollector(fake, new Paths { History = hist }, new NullLog()).Collect(Svc(), 1);
            Assert.Equal(ClaudeCollector.LoginAction, s.ActionId);
            Assert.Equal(SvcStatus.Ok, s.Status);
            Assert.Equal(new double?[] { 11, 22 }, s.Bars.Select(b => b.Used).ToArray());
            s.LastAttemptAt = DateTime.UtcNow;
            Assert.Equal("ok", QuotasProjection.ProjectProvider(s)["status"]);
        }
    }

    public class WakeDetectorTests
    {
        [Fact]
        public void DetectsGapBetweenTicks()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var w = new WakeDetector(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), () => now, startTimer: false);
            TimeSpan? slept = null;
            w.Woke += s => slept = s;
            now = now.AddSeconds(5); w.Tick();
            Assert.Null(slept);
            now = now.AddSeconds(14); w.Tick();   // задержка таймера под нагрузкой — не сон
            Assert.Null(slept);
            now = now.AddMinutes(30); w.Tick();
            Assert.NotNull(slept);
            Assert.Equal(1, w.WakeCount);
        }
    }

    public class TelemetryWriterTests
    {
        static string NewDir() => Path.Combine(Path.GetTempPath(), "xplat-tele-" + Guid.NewGuid().ToString("N"));

        static bool WaitFor(Func<bool> cond, int ms = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(20); }
            return cond();
        }

        [Fact]
        public void PublishesPrivatelyAndAtomically()
        {
            string dir = NewDir();
            using var w = TelemetrySinks.Create(dir, fixture: true);
            w.Submit(t => "{\"n\":1}");
            Assert.True(WaitFor(() => w.PublishedCount == 1), w.LastError);
            w.Submit(t => "{\"n\":2}");
            Assert.True(WaitFor(() => w.PublishedCount == 2), w.LastError);   // не чаще раза в секунду, но доходит
            Assert.Equal("{\"n\":2}", File.ReadAllText(w.TargetPath));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(w.TargetPath));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
            }
        }

        [Fact]
        public void SecondWriterForSameFileRefuses()
        {
            string dir = NewDir();
            using var a = TelemetrySinks.Create(dir, fixture: true);
            a.Submit(t => "{}");
            Assert.True(WaitFor(() => a.PublishedCount == 1), a.LastError);
            using var b = TelemetrySinks.Create(dir, fixture: true);
            Assert.True(WaitFor(() => b.LastError == "writer_already_running"), b.LastError);
        }

        [Fact]
        public void FixtureCannotWriteIntoLiveDirectory()
        {
            Assert.ThrowsAny<ArgumentException>(() => TelemetrySinks.Create(AppPaths.StandardTelemetryDirectory(), fixture: true));
        }

        [Fact]
        public void UnixRefusesSharedDirectoryAndSymlinkTarget()
        {
            if (OperatingSystem.IsWindows()) return;
            string dir = NewDir();
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, (UnixFileMode)Convert.ToInt32("755", 8));
            using (var w = TelemetrySinks.Create(dir, fixture: true))
                Assert.True(WaitFor(() => w.LastError == "access_denied"), w.LastError);

            string dir2 = NewDir();
            Directory.CreateDirectory(dir2);
            File.SetUnixFileMode(dir2, (UnixFileMode)Convert.ToInt32("700", 8));
            string victim = Path.Combine(Path.GetTempPath(), "victim-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(victim, "keep");
            File.CreateSymbolicLink(Path.Combine(dir2, "quotas-snapshot.json"), victim);
            using (var w = TelemetrySinks.Create(dir2, fixture: true))
            {
                w.Submit(t => "{}");
                Assert.True(WaitFor(() => w.LastError == "access_denied"), w.LastError);
            }
            Assert.Equal("keep", File.ReadAllText(victim));
        }

        [Fact]
        public void PublisherHeartbeatEndToEndIsContractShaped()
        {
            string dir = NewDir();
            using var pub = new QuotasPublisher(TelemetrySinks.Create(dir, fixture: true), fixture: true, refreshSeconds: 300);
            pub.Configure(new[] { "claude" }, 300);
            Assert.True(WaitFor(() => File.Exists(Path.Combine(dir, "quotas-snapshot.json"))), pub.Sink.LastError);
            var doc = Json.Obj(Json.Parse(File.ReadAllText(Path.Combine(dir, "quotas-snapshot.json"))));
            Assert.Equal("1.0.0", doc["schemaVersion"]);
            Assert.Equal("fixture", doc["mode"]);
            Assert.Equal("limits-widget", doc["source_id"]);
            Assert.Equal(10000, doc["heartbeat_interval_ms"]);
        }
    }
}
