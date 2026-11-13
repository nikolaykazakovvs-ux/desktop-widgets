using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LimitsWidget
{
    // Офлайн-проверки AI Limits 1.1 на настоящих исходниках виджета: только синтетические данные, поддельные процессы
    // kimi/codex и loopback-серверы. Сеть провайдеров, аккаунты, credentials и установленная копия не используются.
    static class LimitsTests
    {
        static string output, fakeKimi, fakeCodex, fakeClaude;
        static int assertions, sections;
        static readonly List<string> report = new List<string>();

        static void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception(message);
        }

        // LIMITS_TESTS_SKIP — разделы через «;», которые не запускать (например, с принудительным завершением процессов).
        // LIMITS_TESTS_NO_HARDKILL=1 — пропустить отдельные случаи «зависший CLI», завершаемые принудительно.
        static readonly bool NoHardKill = Environment.GetEnvironmentVariable("LIMITS_TESTS_NO_HARDKILL") == "1";

        static void Section(string name, Action body)
        {
            string skip = Environment.GetEnvironmentVariable("LIMITS_TESTS_SKIP") ?? "";
            if (skip.Split(';').Any(x => x.Trim().Length > 0 && name.StartsWith(x.Trim(), StringComparison.Ordinal)))
            {
                report.Add("SKIP " + name + " (LIMITS_TESTS_SKIP)");
                Console.WriteLine(report[report.Count - 1]);
                return;
            }
            // LIMITS_TESTS_ONLY — запускать только разделы, имя которых начинается с одного из перечисленных через «;» префиксов.
            string only = Environment.GetEnvironmentVariable("LIMITS_TESTS_ONLY") ?? "";
            if (only.Trim().Length > 0 && !only.Split(';').Any(x => x.Trim().Length > 0 && name.StartsWith(x.Trim(), StringComparison.Ordinal))) return;
            var sw = Stopwatch.StartNew();
            int before = assertions;
            body();
            sections++;
            report.Add(string.Format("PASS {0} ({1} assertions, {2} ms)", name, assertions - before, sw.ElapsedMilliseconds));
            Console.WriteLine(report[report.Count - 1]);
        }

        static string NewDir(string name)
        {
            string d = Path.Combine(output, name);
            if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(d);
            return d;
        }

        static ProviderContext Ctx(Config cfg, string id, string fixtures)
        {
            return new ProviderContext { Config = cfg, Svc = cfg.Svc(id), Now = DateTime.Now, FixturesDir = fixtures, FixtureOnly = fixtures != null, State = new State() };
        }

        // ---------- состояние и модель ----------

        static void ModelRoundtrip()
        {
            var s = new ServiceSnapshot { Id = "claude", Title = "Claude Code", Url = "https://claude.ai/settings/usage", ProfileUrl = "https://claude.ai/settings/billing",
                ActionId = "claude-login", ActionLabel = "Войти", Attention = true, Plan = "Max", PlanSource = "manual", AutoPlan = "Pro", AutoPlanSource = "x",
                Status = SvcStatus.Ok, DataTime = DateTime.UtcNow, LastAttemptAt = DateTime.UtcNow };
            s.Activity = new TokenActivity { MeasuredAt = DateTime.UtcNow, LifetimeTokens = 5, Days = new List<DailyTokens> { new DailyTokens { Date = DateTime.Today, Tokens = null } } };
            var back = ServiceSnapshot.FromDict(Json.Parse(Json.Serialize(s.ToDict(), false)));
            Check(back.Url == s.Url, "url roundtrip");
            Check(back.ProfileUrl == s.ProfileUrl, "profileUrl roundtrip");
            Check(back.ActionId == "claude-login" && back.ActionLabel == "Войти", "action roundtrip");
            Check(back.Attention, "attention roundtrip");
            Check(back.PlanSource == "manual" && back.AutoPlan == "Pro", "plan source roundtrip");
            Check(back.Activity != null && back.Activity.LifetimeTokens == 5 && back.Activity.Days.Count == 1 && !back.Activity.Days[0].Tokens.HasValue, "activity roundtrip keeps null day");
            Check(back.DataTime.Value.Kind == DateTimeKind.Utc && back.LastAttemptAt.Value.Kind == DateTimeKind.Utc, "UTC storage after load");
        }

        static void UtcTooltip()
        {
            // один момент: свежий снимок (UTC) и он же из кэша должны дать одинаковую подсказку
            var moment = new DateTime(2026, 11, 3, 22, 30, 0, DateTimeKind.Utc);
            var fresh = new ServiceSnapshot { Id = "codex", Source = "src", Status = SvcStatus.Ok, DataTime = moment, LastAttemptAt = moment };
            var cached = ServiceSnapshot.FromDict(Json.Parse(Json.Serialize(fresh.ToDict(), false)));
            var now = moment.ToLocalTime().AddHours(3);
            string a = CardView.StatusTip(fresh, now), b = CardView.StatusTip(cached, now);
            Check(a == b, "fresh vs cached tooltip differ: " + a + " | " + b);
            Check(a.Contains(Fmt.Clock(moment.ToLocalTime())), "tooltip shows local clock");
            var unknown = new ServiceSnapshot { Id = "claude", Status = SvcStatus.Ok, ObservationTimeUnknown = true };
            Check(CardView.StatusTip(unknown, DateTime.Now).Contains("неизвестно"), "unknown observation time stays unknown");
            Check(Fmt.NotAfter(DateTime.UtcNow.AddMinutes(-1), DateTime.Now), "kind-independent comparison");
        }

        sealed class Scripted : IProvider
        {
            public string Id { get; set; }
            public Func<ProviderContext, ServiceSnapshot> Body;
            public ServiceSnapshot Collect(ProviderContext ctx) { return Body(ctx); }
        }

        static ServiceSnapshot OkSnap(ProviderContext ctx, string id, double used)
        {
            var s = ProviderUtil.NewSnap(ctx, id);
            var bar = new LimitBar { Chip = "5 ч", Label = "x", Used = used };
            bar.Quota = QuotaObservation.Create(id, "subscription.general", "five_hour", 18000, used, null);
            s.Bars.Add(bar);
            ProviderUtil.MarkFresh(s, ctx, false);
            return s;
        }

        static void LatestAttemptAndState()
        {
            string dir = NewDir("state");
            string path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, "{\"left\":10,\"futureField\":{\"a\":[1,2]},\"cache\":{}}");
            var state = State.Load(path);
            var cfg = new Config();
            cfg.Order = new List<string> { "codex" };
            cfg.Svc("codex").Enabled = true;
            bool fail = false;
            var p = new Scripted { Id = "codex", Body = ctx =>
            {
                if (!fail) return OkSnap(ctx, "codex", 40);
                var e = ProviderUtil.NewSnap(ctx, "codex");
                return ProviderUtil.Fail(e, SvcStatus.Error, "ошибка", "сбой", ProviderUtil.Placeholder("5 ч", "x"));
            } };
            var col = new Collector(cfg, state, null, null, new IProvider[] { p }, null);
            var first = col.Collect()[0];
            Thread.Sleep(1100);
            fail = true;
            var second = col.Collect()[0];
            Check(second.Status == SvcStatus.Stale, "failure falls back to last good");
            Check(second.LastAttemptAt > first.LastAttemptAt, "stale result carries the new attempt time");
            Check(second.DataTime == first.DataTime || Math.Abs((second.DataTime.Value - first.DataTime.Value).TotalSeconds) < 1, "stale keeps original data time");
            var cache = state.GetCache("codex");
            Check(cache.Status == SvcStatus.Ok, "lastGood stays Ok in cache");
            var attempt = state.GetAttempt("codex");
            Check(attempt != null && attempt.Status == "Error" && attempt.Message == "сбой", "latest attempt recorded separately");
            Check(attempt.At.Value > cache.LastAttemptAt.Value.AddMilliseconds(500), "latest attempt newer than last good");
            state.Save();
            var raw = Json.Parse(File.ReadAllText(path));
            Check(Json.Get(raw, "futureField") != null, "unknown state field preserved");
            Check(Json.Get(raw, "attempts.codex.status") as string == "Error", "attempts persisted");
            Check(Json.Get(raw, "cache.codex.status") as string == "Ok", "cache persisted as last good");
            var again = State.Load(path);
            Check(again.GetAttempt("codex").Status == "Error", "attempts reload");
        }

        static void EarlyResultsAndRace()
        {
            var cfg = new Config();
            cfg.Order = new List<string> { "codex", "claude" };
            cfg.Svc("codex").Enabled = true;
            cfg.Svc("claude").Enabled = true;
            var release = new ManualResetEvent(false);
            var p1 = new Scripted { Id = "codex", Body = ctx =>
            {
                var s = OkSnap(ctx, "codex", 10);
                ctx.PublishCore(s);
                release.WaitOne(5000);   // догрузка идёт после ранней публикации
                s.Activity = new TokenActivity { MeasuredAt = DateTime.UtcNow, LifetimeTokens = 1 };
                return s;
            } };
            var p2 = new Scripted { Id = "claude", Body = ctx => OkSnap(ctx, "claude", 20) };
            var events = new List<ServiceSnapshot>();
            var col = new Collector(cfg, new State(), null, null, new IProvider[] { p1, p2 }, null);
            var work = System.Threading.Tasks.Task.Factory.StartNew(() => col.Collect(s => { lock (events) events.Add(s); }));
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until) { lock (events) if (events.Count(e => e.Id == "codex") >= 1 && events.Any(e => e.Id == "claude")) break; Thread.Sleep(20); }
            lock (events)
            {
                Check(events.Any(e => e.Id == "codex" && e.Partial && e.Activity == null), "codex core published before enrichment");
                Check(events.Any(e => e.Id == "claude" && !e.Partial), "other provider not blocked by enrichment");
            }
            Check(!work.IsCompleted, "batch still running during enrichment");
            release.Set();
            Check(work.Wait(5000), "batch completes");
            lock (events) Check(events.Any(e => e.Id == "codex" && !e.Partial && e.Activity != null), "enriched final delivered");

            // UI-доска: поздний результат прошлого опроса и ранний после итогового не затирают новое
            var board = new List<ServiceSnapshot>();
            SnapshotBoard.Merge(board, new ServiceSnapshot { Id = "codex", Generation = 5, Message = "g5" }, cfg.Order);
            Check(!SnapshotBoard.Merge(board, new ServiceSnapshot { Id = "codex", Generation = 4, Message = "g4" }, cfg.Order) && board[0].Message == "g5", "older generation rejected");
            SnapshotBoard.Merge(board, new ServiceSnapshot { Id = "codex", Generation = 6, Message = "final" }, cfg.Order);
            Check(!SnapshotBoard.Merge(board, new ServiceSnapshot { Id = "codex", Generation = 6, Partial = true, Message = "early" }, cfg.Order), "partial after final rejected");
            SnapshotBoard.Merge(board, new ServiceSnapshot { Id = "claude", Generation = 6 }, cfg.Order);
            Check(board[0].Id == "codex" && board[1].Id == "claude", "board keeps config order");
            // кэш: результат старого опроса не затирает новый
            var st = new State();
            st.PutCache(new ServiceSnapshot { Id = "kimi", Generation = 9, Message = "new" });
            Check(!st.PutCache(new ServiceSnapshot { Id = "kimi", Generation = 8, Message = "old" }) && st.GetCache("kimi").Message == "new", "cache generation guard");
        }

        // ---------- провайдеры ----------

        static void ClaudeScoped()
        {
            string fx = NewDir("claude-fx");
            File.WriteAllText(Path.Combine(fx, "claude-get-usage.json"),
                "{\"rate_limits\":{\"five_hour\":{\"resets_at\":\"2030-01-01T00:00:00Z\"},\"seven_day\":{\"utilization\":30},"
                + "\"model_scoped\":[{\"display_name\":\"Fable\",\"resets_at\":\"2030-01-01T00:00:00Z\"},{\"display_name\":\"Opus 9\",\"key\":\"seven_day_opus\",\"utilization\":12}]}}");
            var cfg = new Config();
            var s = new ClaudeProvider().Collect(Ctx(cfg, "claude", fx));
            var five = s.Bars.First(b => b.Label.StartsWith("Сессия"));
            Check(!five.Used.HasValue, "five_hour missing utilization stays unknown (not 0)");
            var fable = s.Bars.First(b => b.Label.Contains("Fable"));
            Check(!fable.Used.HasValue, "model_scoped missing utilization stays unknown (not 0)");
            Check(fable.Quota == null, "unknown scoped label not guessed into a typed pool");
            var opus = s.Bars.First(b => b.Label.Contains("Opus 9"));
            Check(opus.Quota != null && opus.Quota.PoolId == "subscription.opus", "confirmed scoped key keeps typed pool");
            Check(s.Bars.All(b => b.Quota == null || !b.Quota.MeasuredAt.HasValue), "claude observation time stays unknown");
            s.LastAttemptAt = DateTime.UtcNow;
            var proj = TelemetryPublisher.ProjectProvider(s);
            Check((string)proj["status"] != "ok", "telemetry not ok with unknown observations");
            Check(!Json.Serialize(proj, false).Contains("Fable"), "raw scoped label not exported");
        }

        static void KimiRatios()
        {
            string fx = NewDir("kimi-fx");
            var cfg = new Config();
            string[] bad = { "\"NaN\"", "-0.1", "1.2", "\"Infinity\"" };
            foreach (var v in bad)
            {
                File.WriteAllText(Path.Combine(fx, "kimi-server-usage.json"), "{\"data\":{\"kind\":\"ok\",\"quota\":{\"usages\":{\"limit5h\":{\"usedRatio\":" + v + "}}}}}");
                var s = new KimiProvider().Collect(Ctx(cfg, "kimi", fx));
                Check(s.Status == SvcStatus.Error && s.Bars.All(b => b.Placeholder), "invalid ratio rejected: " + v);
            }
            File.WriteAllText(Path.Combine(fx, "kimi-server-usage.json"), "{\"data\":{\"kind\":\"ok\",\"quota\":{\"usages\":{\"limit5h\":{\"usedRatio\":1},\"limit7d\":{\"usedRatio\":\"abc\"}}}}}");
            var ok = new KimiProvider().Collect(Ctx(cfg, "kimi", fx));
            Check(ok.Status == SvcStatus.Fixture && ok.Bars.Count == 1 && ok.Bars[0].Used == 100, "valid ratio accepted; non-numeric window absent, not zero");
            File.Delete(Path.Combine(fx, "kimi-server-usage.json"));
            File.WriteAllText(Path.Combine(fx, "kimi-usages.json"), "{\"usages\":{\"limit_5h\":{\"used_ratio\":-1}}}");
            Check(new KimiProvider().Collect(Ctx(cfg, "kimi", fx)).Status == SvcStatus.Error, "direct API invalid ratio rejected");
        }

        static void KimiEmptyToken()
        {
            string home = NewDir("kimi-home-token");
            Directory.CreateDirectory(Path.Combine(home, "credentials"));
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
            var cfg = new Config();
            cfg.AllowLocalTokens = true;
            cfg.Svc("kimi").StartServer = false;
            cfg.Svc("kimi").BaseUrl = "https://kimi.invalid/never";   // до сети дойти не должно
            foreach (var body in new[] { "{\"access_token\":\"\",\"expires_at\":0}", "{\"access_token\":null}" })
            {
                File.WriteAllText(Path.Combine(home, "credentials", "kimi-code.json"), body);
                var s = new KimiProvider(new KimiServerManager()).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(s.ActionId == KimiProvider.LoginAction && s.StatusText == "нужен вход", "revoked/empty token = login needed before network: " + body);
            }
            // без разрешения на локальный ключ и без серверов — понятное требование, а не чтение ключа
            cfg.AllowLocalTokens = false;
            var n = new KimiProvider(new KimiServerManager()).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
            Check(n.Status == SvcStatus.NeedsPermission, "no server, no opt-in -> needs permission");
        }

        // Мини-HTTP на loopback: фиксированный ответ, счётчик запросов.
        sealed class LoopbackServer : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            public int Port, Hits;
            public LoopbackServer(int status, string body)
            {
                l.Start();
                Port = ((IPEndPoint)l.LocalEndpoint).Port;
                var t = new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient c;
                        try { c = l.AcceptTcpClient(); }
                        catch { return; }
                        Interlocked.Increment(ref Hits);
                        using (c)
                        {
                            try
                            {
                                var s = c.GetStream();
                                var r = new StreamReader(s, Encoding.ASCII);
                                string line;
                                while (!string.IsNullOrEmpty(line = r.ReadLine())) { }
                                var b = Encoding.UTF8.GetBytes(body);
                                var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " X\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
                                s.Write(h, 0, h.Length);
                                s.Write(b, 0, b.Length);
                            }
                            catch { }
                        }
                    }
                }) { IsBackground = true };
                t.Start();
            }
            public void Dispose() { l.Stop(); }
        }

        static void WriteInstance(string home, string name, int port, int pid, double heartbeatMs)
        {
            string dir = Path.Combine(home, "server", "instances");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".json"), "{\"host\":\"127.0.0.1\",\"port\":" + port + ",\"pid\":" + pid + ",\"heartbeat_at\":" + (long)heartbeatMs + "}");
        }

        static double NowMs() { return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        static void KimiCandidates()
        {
            string home = NewDir("kimi-home-candidates");
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
            File.WriteAllText(Path.Combine(home, "server.token"), "synthetic-token");
            int me = Process.GetCurrentProcess().Id;
            string good = "{\"code\":0,\"data\":{\"kind\":\"ok\",\"quota\":{\"usages\":{\"limit5h\":{\"usedRatio\":0.4}}}}}";
            using (var broken = new LoopbackServer(500, "{}"))
            using (var unauth = new LoopbackServer(401, "{}"))
            using (var ok = new LoopbackServer(200, good))
            {
                WriteInstance(home, "a-newest", broken.Port, me, NowMs());
                WriteInstance(home, "b-middle", unauth.Port, me, NowMs() - 1000);
                WriteInstance(home, "c-older", ok.Port, me, NowMs() - 2000);
                WriteInstance(home, "d-dead", 1, 999999, NowMs());   // мёртвый pid — пропускается
                File.WriteAllText(Path.Combine(home, "server", "instances", "e-remote.json"), "{\"host\":\"10.0.0.5\",\"port\":" + ok.Port + ",\"pid\":" + me + ",\"heartbeat_at\":" + (long)NowMs() + "}");
                var list = KimiProvider.FindInstances(home, DateTime.Now);
                Check(list.Count == 3 && list[0].Port == broken.Port, "live loopback candidates ordered by heartbeat; dead/remote skipped");
                var cfg = new Config();
                cfg.Svc("kimi").StartServer = false;
                var s = new KimiProvider(new KimiServerManager()).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(s.Status == SvcStatus.Ok && s.Bars.Count == 1 && Math.Abs(s.Bars[0].Used.Value - 40) < 0.001, "falls back across candidates to a usable server");
                Check(broken.Hits == 1 && unauth.Hits == 1 && ok.Hits >= 1, "each candidate asked once within budget");
            }
            using (var login = new LoopbackServer(200, "{\"code\":0,\"data\":{\"kind\":\"error\",\"status\":401,\"message\":\"x\"}}"))
            {
                Directory.Delete(Path.Combine(home, "server", "instances"), true);
                WriteInstance(home, "login", login.Port, Process.GetCurrentProcess().Id, NowMs());
                var cfg = new Config();
                cfg.Svc("kimi").StartServer = false;
                var s = new KimiProvider(new KimiServerManager()).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(s.ActionId == KimiProvider.LoginAction && s.Attention, "account 401 -> clear login action");
            }
        }

        // Loopback-сервер с ответом по пути (квота и userinfo отдельно).
        sealed class PathServer : IDisposable
        {
            readonly TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            public int Port;
            public readonly List<string> Paths = new List<string>();
            public PathServer(Func<string, KeyValuePair<int, string>> route)
            {
                l.Start();
                Port = ((IPEndPoint)l.LocalEndpoint).Port;
                var t = new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient c;
                        try { c = l.AcceptTcpClient(); }
                        catch { return; }
                        using (c)
                        {
                            try
                            {
                                var s = c.GetStream();
                                var r = new StreamReader(s, Encoding.ASCII);
                                string first = r.ReadLine() ?? "", line;
                                while (!string.IsNullOrEmpty(line = r.ReadLine())) { }
                                string path = first.Split(' ').Length > 1 ? first.Split(' ')[1] : "/";
                                lock (Paths) Paths.Add(path);
                                var resp = route(path);
                                var b = Encoding.UTF8.GetBytes(resp.Value);
                                var h = Encoding.ASCII.GetBytes("HTTP/1.1 " + resp.Key + " X\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
                                s.Write(h, 0, h.Length);
                                s.Write(b, 0, b.Length);
                            }
                            catch { }
                        }
                    }
                }) { IsBackground = true };
                t.Start();
            }
            public void Dispose() { l.Stop(); }
        }

        const string Quota40 = "{\"code\":0,\"data\":{\"kind\":\"ok\",\"quota\":{\"usages\":{\"limit5h\":{\"usedRatio\":0.4}}}}}";
        const string PiiCanaries = "\"userId\":\"UID-CANARY-7731\",\"email\":\"canary.person@example.test\",\"phone\":\"+10000099999\",\"nickname\":\"NICK-CANARY\"";

        static ServiceSnapshot KimiWith(string home, string userinfo, int userinfoStatus, ServiceConfig tweak, State st, List<ServiceSnapshot> cores)
        {
            using (var srv = new PathServer(path => path.StartsWith("/api/v1/oauth/userinfo")
                ? new KeyValuePair<int, string>(userinfoStatus, userinfo)
                : new KeyValuePair<int, string>(200, Quota40)))
            {
                string dir = Path.Combine(home, "server", "instances");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                WriteInstance(home, "level", srv.Port, Process.GetCurrentProcess().Id, NowMs());
                var cfg = new Config();
                cfg.Svc("kimi").StartServer = false;
                if (tweak != null) { cfg.Svc("kimi").Plan = tweak.Plan; cfg.Svc("kimi").ProfileUrl = tweak.ProfileUrl ?? cfg.Svc("kimi").ProfileUrl; }
                var ctx = Ctx(cfg, "kimi", null).Also(c => { c.FixtureOnly = false; c.State = st ?? new State(); });
                if (cores != null) ctx.Core = c => { var k = c.Clone(); k.DataTime = c.DataTime; cores.Add(k); };   // точное время до сериализации
                var s = new KimiProvider(new KimiServerManager()).Collect(ctx);
                Check(srv.Paths.Count(x => x.StartsWith("/api/v1/oauth/usage")) == 1, "one quota request");
                Check(srv.Paths.Count(x => x.StartsWith("/api/v1/oauth/userinfo")) <= 1, "at most one userinfo request");
                return s;
            }
        }

        static void KimiLevel()
        {
            string home = NewDir("kimi-home-level");
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
            File.WriteAllText(Path.Combine(home, "server.token"), "synthetic-level-token");
            Check(KimiProvider.ParseLevel(Json.Parse("{\"data\":{\"kind\":\"ok\",\"userInfo\":{\"userLevelName\":\"  Allegretto \"}}}")) == "Allegretto", "level parsed and trimmed");
            Check(KimiProvider.ParseLevel(Json.Parse("{\"data\":{\"kind\":\"error\",\"message\":\"x\"}}")) == null, "kind error -> no level");
            Check(KimiProvider.ParseLevel(Json.Parse("{\"data\":{\"kind\":\"ok\",\"userInfo\":{\"userLevelName\":\"\"}}}")) == null, "empty level -> none");
            Check(KimiProvider.ParseLevel(Json.Parse("{\"data\":{\"kind\":\"ok\",\"userInfo\":{\"userLevelName\":\"me@example.test\"}}}")) == null, "contact-like level rejected");
            Check(KimiProvider.ParseLevel(Json.Parse("{\"data\":{\"kind\":\"ok\",\"userInfo\":{\"userLevelName\":7}}}")) == null, "non-string level rejected");

            // 1) валидный userinfo: уровень, квоты опубликованы раньше и не изменились, PII нигде не осели
            Program.BaseDir = NewDir("kimi-level-log");
            var st = new State { FilePath = Path.Combine(Program.BaseDir, "state.json") };
            var cores = new List<ServiceSnapshot>();
            var s = KimiWith(home, "{\"code\":0,\"data\":{\"kind\":\"ok\",\"userInfo\":{\"userLevelName\":\"Allegretto\"," + PiiCanaries + "}}}", 200, null, st, cores);
            Check(s.Status == SvcStatus.Ok && s.Bars.Count == 1 && Math.Abs(s.Bars[0].Used.Value - 40) < 0.001, "quota ok with level");
            Check(s.AutoPlan == "Allegretto" && s.AutoPlanIsLevel && s.Plan == "Allegretto" && s.PlanSource == "provider" && s.AutoPlanSource == KimiProvider.LevelSource, "level becomes automatic account level");
            Check(PlanResolver.Describe(s).Contains("Уровень аккаунта") && PlanResolver.Describe(s).Contains("не подтверждение"), "tooltip calls it account level, not payment");
            Check(cores.Count == 1 && cores[0].Status == SvcStatus.Ok && cores[0].AutoPlan == null, "quotas published before userinfo");
            Check(cores[0].DataTime == s.DataTime && cores[0].StatusText == s.StatusText && s.Bars[0].Quota.MeasuredAt == s.DataTime, "userinfo does not change quota freshness/status");
            st.PutCache(s);
            st.Save();
            Log.Write("probe line");   // журнал существует, в нём не должно быть данных профиля
            var proj = Json.Serialize(TelemetryPublisher.ProjectProvider(s.Also(x => x.LastAttemptAt = DateTime.UtcNow)), false);
            var dump = Json.Serialize(s.ToDict(), false);
            foreach (var text in new[] { File.ReadAllText(st.FilePath), File.ReadAllText(Path.Combine(Program.BaseDir, "widget.log")), proj, dump })
                foreach (var canary in new[] { "UID-CANARY", "canary.person", "10000099999", "NICK-CANARY", "userInfo", "userId", "email", "phone" })
                    Check(!text.Contains(canary), "PII/raw userinfo never persisted/logged/exported: " + canary);
            Check(!proj.Contains("Allegretto"), "level not in public telemetry");

            // 2) ручной тариф главнее уровня
            var manual = KimiWith(home, "{\"data\":{\"kind\":\"ok\",\"userInfo\":{\"userLevelName\":\"Allegretto\"}}}", 200, new ServiceConfig { Plan = "Moderato (вручную)" }, null, null);
            Check(manual.Plan == "Moderato (вручную)" && manual.PlanSource == "manual" && manual.AutoPlan == "Allegretto", "manual plan wins over account level");
            Check(PlanResolver.Describe(manual).Contains("Уровень аккаунта у провайдера"), "manual tooltip mentions account level");

            // 3) сбой/нет данных: квоты Ok, уровня нет (или прошлый из последнего удачного снимка)
            foreach (var bad in new[] { new KeyValuePair<int, string>(500, "{}"), new KeyValuePair<int, string>(200, "{\"data\":{\"kind\":\"error\",\"status\":503}}"),
                new KeyValuePair<int, string>(200, "{\"data\":{\"kind\":\"ok\",\"userInfo\":{" + PiiCanaries + "}}}"), new KeyValuePair<int, string>(200, "not json") })
            {
                var f = KimiWith(home, bad.Value, bad.Key, null, null, null);
                Check(f.Status == SvcStatus.Ok && f.Bars.Count == 1 && f.DataTime.HasValue, "userinfo failure never breaks quotas: " + bad.Key);
                Check(f.AutoPlan == null && f.Plan == null && f.PlanSource == null, "no level -> unknown plan: " + bad.Value);
                Check(!Json.Serialize(f.ToDict(), false).Contains("CANARY"), "no PII from failed userinfo");
            }
            var prevState = new State();
            prevState.PutCache(s);
            var kept = KimiWith(home, "{}", 500, null, prevState, null);
            Check(kept.Status == SvcStatus.Ok && kept.AutoPlan == "Allegretto" && kept.AutoPlanIsLevel, "previous level kept when userinfo fails");
            var back = ServiceSnapshot.FromDict(Json.Parse(Json.Serialize(s.ToDict(), false)));
            Check(back.AutoPlanIsLevel && back.AutoPlan == "Allegretto", "level flag roundtrip");
            string logText = File.ReadAllText(Path.Combine(Program.BaseDir, "widget.log"));
            Check(logText.Contains("kimi userinfo: HTTP 500") && !logText.Contains("not json") && !logText.Contains("CANARY"), "failures logged by status only, never raw body");

            // 4) ссылка подписки: подтверждённая по умолчанию, настроенная (другой регион) главнее
            var cfg = new Config();
            Check(cfg.Svc("kimi").ProfileUrl == "https://www.kimi.com/membership/subscription", "verified default Kimi subscription page");
            var region = KimiWith(home, "{}", 500, new ServiceConfig { ProfileUrl = "https://www.kimi.ai/custom/subscription" }, null, null);
            Check(region.ProfileUrl == "https://www.kimi.ai/custom/subscription", "configured other-region link retained, no domain mapping");
            var def = KimiWith(home, "{}", 500, null, null, null);
            Check(def.ProfileUrl == "https://www.kimi.com/membership/subscription" && def.Url == null, "default Kimi header link = subscription page, separate from quota url");
            Program.BaseDir = output;
        }

        static void ClaudeSubscription()
        {
            // разбор: только subscriptionType; logged-out/нет поля — ответ без тарифа; не JSON — ответа нет
            var max = ClaudeAuth.Parse("{\"loggedIn\":true,\"subscriptionType\":\"max\",\"email\":\"x@y.z\"}");
            Check(max.Answered && max.Plan == "Max", "max -> Max (no multiplier)");
            Check(ClaudeAuth.Parse("{\"loggedIn\":false,\"subscriptionType\":\"max\"}").Plan == null, "logged out -> no plan");
            Check(ClaudeAuth.Parse("{\"loggedIn\":true}").Answered && ClaudeAuth.Parse("{\"loggedIn\":true}").Plan == null, "missing field -> answered, no plan");
            Check(!ClaudeAuth.Parse("Logged in (Max)").Answered, "text output -> unsupported");
            Check(ClaudeAuth.Parse("{\"subscriptionType\":\"team_premium\"}").Plan == "Team Premium", "new schema value readable");
            Check(ClaudeAuth.Parse("{\"subscriptionType\":\"me@example.test\"}").Plan == null, "non-identifier value rejected");
            Check(ClaudeAuth.Parse("{\"subscriptionType\":5}").Plan == null, "non-string rejected");

            // настоящий подпроцесс (поддельный claude): поддержка, выход, нет поля, старый CLI, текст, зависание
            Program.BaseDir = NewDir("claude-auth-log");
            string calls = Path.Combine(Program.BaseDir, "calls.txt");
            Environment.SetEnvironmentVariable("FAKE_CLAUDE_LOG", calls);
            Environment.SetEnvironmentVariable("CLAUDE_FAKE_PARENT", "1");
            Environment.SetEnvironmentVariable("ANTHROPIC_FAKE_PARENT", "1");
            var expect = new Dictionary<string, KeyValuePair<bool, string>>
            {
                { "max", new KeyValuePair<bool, string>(true, "Max") }, { "loggedout", new KeyValuePair<bool, string>(true, null) },
                { "missing", new KeyValuePair<bool, string>(true, null) }, { "unsupported", new KeyValuePair<bool, string>(false, null) },
                { "text", new KeyValuePair<bool, string>(false, null) }, { "hang", new KeyValuePair<bool, string>(false, null) }
            };
            foreach (var kv in expect)
            {
                if (NoHardKill && kv.Key == "hang") continue;
                Environment.SetEnvironmentVariable("FAKE_CLAUDE_AUTH", kv.Key);
                var sw = Stopwatch.StartNew();
                var r = ClaudeAuth.Query(fakeClaude, 2000);
                Check(r.Answered == kv.Value.Key && r.Plan == kv.Value.Value, "auth status " + kv.Key);
                Check(sw.ElapsedMilliseconds < 9000, "auth status bounded: " + kv.Key);
            }
            Check(File.ReadAllLines(calls).All(l => l.StartsWith("auth status --json") && !l.Contains("ENV-LEAKED")), "only `auth status --json`, parent CLAUDE*/ANTHROPIC* env not passed");
            Environment.SetEnvironmentVariable("CLAUDE_FAKE_PARENT", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_FAKE_PARENT", null);
            Log.Write("probe line");
            string log = File.ReadAllText(Path.Combine(Program.BaseDir, "widget.log"));
            foreach (var canary in new[] { "canary.claude", "ORG-CANARY", "ACC-CANARY", "Canary Org", "unknown option" })
                Check(!log.Contains(canary), "raw auth output never logged: " + canary);
            Check(log.Contains("claude auth status: код 1") && (NoHardKill || log.Contains("не ответил вовремя")), "failures logged by code/timeout only");

            // кэш: ответ — 30 мин, сбой — 10 мин, другой exe — новый запрос
            var cache = new ClaudeAuth.Cache();
            var t0 = DateTime.UtcNow;
            int n = 0;
            Func<ClaudeAuth.Result> ok = () => { n++; return new ClaudeAuth.Result { Answered = true, Plan = "Max" }; };
            Func<ClaudeAuth.Result> fail = () => { n++; return new ClaudeAuth.Result(); };
            cache.Get("a", t0, ok); cache.Get("a", t0.AddMinutes(29), ok);
            Check(n == 1, "answer cached 30 min");
            cache.Get("a", t0.AddMinutes(31), fail); cache.Get("a", t0.AddMinutes(40), fail);
            Check(n == 2, "failure cached 10 min");
            cache.Get("a", t0.AddMinutes(42), ok); cache.Get("b", t0.AddMinutes(42), ok);
            Check(n == 4, "refresh after failure ttl; exe change re-queries");

            // провайдер (фикстуры): квоты публикуются раньше, их время/статус не меняются; PII нигде; ручной главнее
            string fx = NewDir("claude-auth-fx");
            File.WriteAllText(Path.Combine(fx, "claude-get-usage.json"), "{\"rate_limits\":{\"five_hour\":{\"utilization\":10},\"seven_day\":{\"utilization\":20}}}");
            File.WriteAllText(Path.Combine(fx, "claude-auth-status.json"), "{\"loggedIn\":true,\"subscriptionType\":\"max\",\"email\":\"canary.claude@example.test\",\"orgId\":\"ORG-CANARY-42\",\"accountUuid\":\"ACC-CANARY-UUID\"}");
            var cfg = new Config();
            var cores = new List<ServiceSnapshot>();
            var ctx = Ctx(cfg, "claude", fx);
            ctx.Core = c => { var k = c.Clone(); k.DataTime = c.DataTime; cores.Add(k); };
            var s = new ClaudeProvider().Collect(ctx);
            Check(s.Status == SvcStatus.Fixture && s.AutoPlan == "Max" && s.Plan == "Max" && s.PlanSource == "provider" && s.AutoPlanSource == ClaudeAuth.Source, "automatic Claude plan from subscriptionType");
            Check(cores.Count == 1 && cores[0].AutoPlan == null && cores[0].Status == s.Status && cores[0].StatusText == s.StatusText, "quotas published before auth status");
            Check(s.Bars.All(b => b.Quota == null || !b.Quota.MeasuredAt.HasValue) && !s.DataTime.HasValue, "Claude observation time stays unknown");
            Check(PlanResolver.Describe(s).Contains("subscriptionType"), "source in tooltip");
            s.LastAttemptAt = DateTime.UtcNow;
            var texts = new[] { Json.Serialize(s.ToDict(), false), Json.Serialize(TelemetryPublisher.ProjectProvider(s), false) };
            foreach (var text in texts)
                foreach (var canary in new[] { "canary.claude", "ORG-CANARY", "ACC-CANARY", "email", "orgId", "accountUuid" })
                    Check(!text.Contains(canary), "auth metadata never persisted/exported: " + canary);
            Check(!texts[1].Contains("Max"), "plan not in public telemetry");

            cfg.Svc("claude").Plan = "Max 20×";
            var manual = new ClaudeProvider().Collect(Ctx(cfg, "claude", fx));
            Check(manual.Plan == "Max 20×" && manual.PlanSource == "manual", "manual plan wins (and no auth query needed)");
            cfg.Svc("claude").Plan = null;

            File.Delete(Path.Combine(fx, "claude-auth-status.json"));
            var none = new ClaudeProvider().Collect(Ctx(cfg, "claude", fx));
            Check(none.Status == SvcStatus.Fixture && none.Plan == null && none.PlanSource == null, "no auth answer -> unknown plan, quotas intact");
            var st = new State();
            st.PutCache(s);
            var kept = new ClaudeProvider().Collect(Ctx(cfg, "claude", fx).Also(c => c.State = st));
            Check(kept.AutoPlan == "Max" && kept.PlanSource == "provider", "previous automatic plan kept when no new answer");
            File.WriteAllText(Path.Combine(fx, "claude-auth-status.json"), "{\"loggedIn\":false}");
            var outSnap = new ClaudeProvider().Collect(Ctx(cfg, "claude", fx).Also(c => c.State = st));
            Check(outSnap.AutoPlan == null && outSnap.Plan == null, "logged-out answer clears automatic plan");
            Environment.SetEnvironmentVariable("FAKE_CLAUDE_AUTH", null);
            Environment.SetEnvironmentVariable("FAKE_CLAUDE_LOG", null);
            Program.BaseDir = output;
        }

        // Ревью P1-2: через настоящий Collector (ранняя публикация перезаписывает кэш) прошлая статистика Codex
        // не теряется при сбое или исчерпании бюджета; её время — исходное, время квот — новое и независимое.
        static void CodexActivityCollector()
        {
            var cfg = new Config();
            cfg.Order = new List<string> { "codex" };
            cfg.Svc("codex").Enabled = true;
            cfg.Svc("codex").Command = fakeCodex;
            cfg.Svc("codex").TimeoutSeconds = 20;
            Environment.SetEnvironmentVariable("FAKE_CODEX_LOG", null);
            var state = new State();
            var col = new Collector(cfg, state, null, null, new IProvider[] { new CodexProvider() }, null);

            Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", "ok");
            var first = col.Collect()[0];
            Check(first.Status == SvcStatus.Ok && first.Activity != null && !first.Activity.Stale && first.Activity.MeasuredAt.HasValue, "poll 1: fresh activity");
            DateTime t1 = first.Activity.MeasuredAt.Value, q1 = first.DataTime.Value;
            Thread.Sleep(1100);

            foreach (var mode in new[] { "error", "slow" })
            {
                Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", mode);
                var early = new List<ServiceSnapshot>();
                var r = col.Collect(s => { lock (early) early.Add(s); })[0];
                Check(r.Status == SvcStatus.Ok && r.Bars.Count == 2, mode + ": quotas succeed independently");
                Check(r.DataTime.Value > q1, mode + ": quota time is the new poll's");
                Check(r.Activity != null && r.Activity.Stale && r.Activity.LifetimeTokens == 123456789, mode + ": previous activity kept and marked stale");
                Check(Math.Abs((r.Activity.MeasuredAt.Value - t1).TotalSeconds) < 1, mode + ": activity keeps its original timestamp");
                lock (early) Check(early.Any(e => e.Partial && e.Activity != null), mode + ": early core result does not erase activity");
                var cached = state.GetCache("codex");
                Check(cached.Activity != null && cached.Activity.Stale && Math.Abs((cached.Activity.MeasuredAt.Value - t1).TotalSeconds) < 1, mode + ": last-good cache keeps activity with original time");
            }

            Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", "ok");
            var fresh = col.Collect()[0];
            Check(fresh.Activity != null && !fresh.Activity.Stale && fresh.Activity.MeasuredAt.Value > t1, "next success replaces stale activity");
            Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", null);
        }

        // Ревью P1-1: Kimi 2.4.1 отвечает на отсутствие/отзыв/неудачное продление входа {kind:error, message} без status.
        // Вход отличается от сетевого сбоя только по официальному GET /api/v1/auth (managed_provider.status).
        static void KimiAuthErrors()
        {
            string home = NewDir("kimi-home-auth");
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
            File.WriteAllText(Path.Combine(home, "server.token"), "synthetic-auth-token");
            Program.BaseDir = NewDir("kimi-auth-log");
            var cases = new[]
            {
                new { Name = "no-token", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"No token for managed:kimi-code. Run /login. MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":0,\"data\":{\"models_ready\":true,\"managed_provider\":{\"name\":\"managed:kimi-code\",\"status\":\"unauthenticated\"}}}", Login = true },
                new { Name = "revoked", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"token was rejected; re-login required MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":0,\"data\":{\"managed_provider\":{\"name\":\"managed:kimi-code\",\"status\":\"revoked\"}}}", Login = true },
                new { Name = "refresh-failed", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"refresh rejected MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":0,\"data\":{\"managed_provider\":{\"name\":\"managed:kimi-code\",\"status\":\"expired\"}}}", Login = true },
                new { Name = "network-authenticated", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"fetch failed MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":0,\"data\":{\"managed_provider\":{\"name\":\"managed:kimi-code\",\"status\":\"authenticated\"}}}", Login = false },
                new { Name = "network-auth-unavailable", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"timeout MSG-CANARY\"}}", AuthStatus = 500, Auth = "{}", Login = false },
                // Kimi 2.4.1: выход удаляет managed:kimi-code, до первого входа его нет — отвеченный /auth с явным null = нужен вход
                new { Name = "logged-out-managed-null", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"No token MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":0,\"data\":{\"models_ready\":false,\"providers_count\":0,\"managed_provider\":null}}", Login = true },
                new { Name = "malformed-no-managed-field", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"x MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":0,\"data\":{\"models_ready\":true}}", Login = false },
                new { Name = "malformed-no-data", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"x MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":40001,\"msg\":\"bad\"}", Login = false },
                new { Name = "malformed-not-json", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"x MSG-CANARY\"}}", AuthStatus = 200, Auth = "<html>", Login = false },
                new { Name = "malformed-provider-string", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"x MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":0,\"data\":{\"managed_provider\":\"none\"}}", Login = false },
                new { Name = "auth-401-bearer-mismatch", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"x MSG-CANARY\"}}", AuthStatus = 401, Auth = "{\"code\":40101,\"data\":{\"managed_provider\":null}}", Login = false },
                new { Name = "upstream-503", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"status\":503,\"message\":\"MSG-CANARY\"}}", AuthStatus = 200, Auth = "{\"code\":0,\"data\":{\"managed_provider\":{\"status\":\"unauthenticated\"}}}", Login = false },
                new { Name = "control-status401", Usage = "{\"code\":0,\"data\":{\"kind\":\"error\",\"status\":401,\"message\":\"MSG-CANARY\"}}", AuthStatus = 500, Auth = "{}", Login = true }
            };
            foreach (var c in cases)
            {
                var cc = c;
                using (var srv = new PathServer(path => path.StartsWith("/api/v1/auth") ? new KeyValuePair<int, string>(cc.AuthStatus, cc.Auth)
                    : path.StartsWith("/api/v1/oauth/usage") ? new KeyValuePair<int, string>(200, cc.Usage) : new KeyValuePair<int, string>(404, "{}")))
                {
                    string dir = Path.Combine(home, "server", "instances");
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    WriteInstance(home, "auth", srv.Port, Process.GetCurrentProcess().Id, NowMs());
                    var cfg = new Config();
                    cfg.Svc("kimi").StartServer = false;
                    var sw = Stopwatch.StartNew();
                    var s = new KimiProvider(new KimiServerManager()).Collect(Ctx(cfg, "kimi", null).Also(x => x.FixtureOnly = false));
                    Check(sw.ElapsedMilliseconds < 12000, c.Name + ": finite budget");
                    if (c.Login)
                        Check(s.ActionId == KimiProvider.LoginAction && s.Attention && s.StatusText == "нужен вход", c.Name + ": login action");
                    else
                        Check(s.ActionId == null && s.StatusText != "нужен вход" && s.Status == SvcStatus.Error, c.Name + ": not misclassified as login (" + s.StatusText + ")");
                    bool askedAuth;
                    lock (srv.Paths) askedAuth = srv.Paths.Any(x => x.StartsWith("/api/v1/auth"));
                    Check(askedAuth == (c.Name != "upstream-503" && c.Name != "control-status401"), c.Name + ": /api/v1/auth consulted only for status-less errors");
                }
            }
            Log.Write("probe line");
            string log = File.ReadAllText(Path.Combine(Program.BaseDir, "widget.log"));
            Check(!log.Contains("MSG-CANARY") && !log.Contains("synthetic-auth-token"), "error message and token never logged");

            // разбор /auth: явный null — «нет провайдера»; отсутствие поля/не объект/неизвестный статус — неизвестно
            Check(KimiProvider.ManagedStatusOf(Json.Parse("{\"data\":{\"managed_provider\":null}}")) == "none", "explicit null -> none");
            Check(KimiProvider.ManagedStatusOf(Json.Parse("{\"data\":{}}")) == null, "missing field -> unknown");
            Check(KimiProvider.ManagedStatusOf(Json.Parse("{\"data\":null}")) == null, "null data -> unknown");
            Check(KimiProvider.ManagedStatusOf(Json.Parse("{\"data\":{\"managed_provider\":{\"status\":\"weird\"}}}")) == null, "unknown status -> unknown");
            Check(KimiProvider.ManagedStatusOf(Json.Parse("{\"data\":{\"managed_provider\":{\"status\":\"authenticated\"}}}")) == "authenticated", "authenticated");
            // /auth недоступен (порт закрыт): неизвестно, а не «нужен вход»
            var probeListener = new TcpListener(IPAddress.Loopback, 0);
            probeListener.Start();
            int closedPort = ((IPEndPoint)probeListener.LocalEndpoint).Port;
            probeListener.Stop();
            Check(KimiProvider.ManagedStatus(new KimiProvider.Instance { Host = "127.0.0.1", Port = closedPort }, "t", 1000) == null, "unreachable /auth -> unknown");
            Program.BaseDir = output;
        }

        // Чистая геометрия: окна не создаются и не показываются, конфигурация мониторов синтетическая.
        static void ActivityPopupPlacement()
        {
            var primary = new Rect(0, 0, 1920, 1040);
            var p = ActivityPopup.Placement(new Rect(0, 100, 300, 500), new Size(352, 600), primary);
            Check(p == new Point(294, 100), "stepped monitors: popup stays on owner monitor instead of invisible lower-left gap");
            var upperLeft = new Rect(-1920, -1080, 1920, 1040);
            p = ActivityPopup.Placement(new Rect(-300, -100, 300, 60), new Size(352, 600), upperLeft);
            Check(p == new Point(-646, -640), "negative secondary monitor: left placement and bottom taskbar clamp");
            p = ActivityPopup.Placement(new Rect(1800, -100, 120, 300), new Size(352, 600), primary);
            Check(p == new Point(1454, 0), "right and top edges remain visible");
            p = ActivityPopup.Placement(new Rect(100, 900, 1800, 100), new Size(352, 600), primary);
            Check(p == new Point(1568, 440), "neither side fits: overlap owner and clamp both right and bottom");
            var pixels = new Rect(-2880, -1620, 2880, 1560);
            foreach (double dpiScale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var wa = ActivityPopup.WorkAreaToDip(pixels, new Matrix(1 / dpiScale, 0, 0, 1 / dpiScale, 0, 0));
                Check(Math.Abs(wa.Left - pixels.Left / dpiScale) < 0.001 && Math.Abs(wa.Bottom - pixels.Bottom / dpiScale) < 0.001, "pixel work area converted to owner DIP at " + dpiScale);
                foreach (double x in new[] { wa.Left, wa.Right - 100 })
                foreach (double y in new[] { wa.Top - 20, wa.Bottom - 20 })
                {
                    p = ActivityPopup.Placement(new Rect(x, y, 100, 100), new Size(352, 600), wa);
                    Check(wa.Contains(new Rect(p, new Size(352, 600))), "all popup edges on negative owner monitor at " + dpiScale);
                }
            }
            p = ActivityPopup.Placement(new Rect(5, 5, 30, 30), new Size(352, 600), new Rect(0, 0, 200, 300));
            Check(p == new Point(0, 0), "oversized popup anchors to work area origin");
        }

        // Оператор: виджет без прокрутки; статистика Codex — компактная кнопка в карточке и отдельное окно.
        static void ActivityPopupChecks()
        {
            Exception failure = null;
            string dir = NewDir("popup");
            var thread = new Thread(() =>
            {
                try
                {
                    foreach (var expandedState in new[] { false, true })
                    {
                        // прежнее state.activityExpanded=true не раскрывает карточку
                        var act = new CardActions { OpenUrl = u => { }, ToggleActivity = () => { }, ActivityExpanded = expandedState, Menu = a => { }, Refresh = () => { } };
                        var view = CardView.Build(UiSnaps(), Theme.Resolve("light"), DateTime.Now, DateTime.Now, false, act, false, 2.0);
                        Check(!Logical(view).OfType<ScrollViewer>().Any(), "no ScrollViewer in main tree");
                        var texts = Logical(view).OfType<TextBlock>().Select(x => x.Text ?? "").ToList();
                        Check(!texts.Any(x => x == "Всего токенов" || x == "Самый долгий ход"), "main card has no inline activity summary (expanded state " + expandedState + ")");
                        Check(texts.Any(x => x.StartsWith("Активность Codex · 7 дней · ")), "compact activity line present");
                        var trigger = Logical(view).OfType<Button>().FirstOrDefault(b => (System.Windows.Automation.AutomationProperties.GetName(b) ?? "") == "Показать подробную статистику Codex");
                        Check(trigger != null && trigger.Focusable, "keyboard-focusable activity trigger");
                    }
                    var snaps = UiSnaps();
                    var a1 = snaps.First(s => s.Id == "codex").Activity;
                    var panel = CardView.ActivityPanel(a1, Theme.Resolve("dark"), DateTime.Now, null);
                    var ptexts = Logical(panel).OfType<TextBlock>().Select(x => x.Text ?? "").ToList();
                    Check(ptexts.Contains("Всего токенов") && ptexts.Contains(CreditStats.Number(123456789)) && ptexts.Contains("—"), "popup shows details; null stays dash");
                    var stale = TokenActivity.FromDict(a1.ToDict()); stale.MeasuredAt = a1.MeasuredAt; stale.Stale = true;
                    Check(Logical(CardView.ActivityPanel(stale, Theme.Resolve("light"), DateTime.Now, null)).OfType<TextBlock>().Any(x => (x.Text ?? "").Contains("не удался")), "stale shown in popup");
                    // окно: без панели задач, Esc/×, обновление содержимого без привязки к карточке
                    var popup = new ActivityPopup(null, Theme.Resolve("light"), 1.5);
                    popup.Update(a1, Theme.Resolve("light"), DateTime.Now);
                    var first = popup.Content;
                    popup.Update(stale, Theme.Resolve("light"), DateTime.Now);
                    Check(!popup.ShowInTaskbar && popup.WindowStyle == WindowStyle.None && !ReferenceEquals(first, popup.Content), "owned tool popup, content rebuilt on update");
                    Check(Logical((DependencyObject)popup.Content).OfType<Button>().Any(b => (System.Windows.Automation.AutomationProperties.GetName(b) ?? "").StartsWith("Закрыть статистику")), "explicit close button");
                    popup.Close();
                    foreach (var theme in new[] { "light", "dark" })
                    {
                        var host = new Border { Background = Brushes.Gray, Child = CardView.ActivityPanel(a1, Theme.Resolve(theme), DateTime.Now, () => { }) };
                        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        host.Arrange(new Rect(host.DesiredSize));
                        host.UpdateLayout();
                        var rtb = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * 2), (int)Math.Ceiling(host.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        rtb.Render(host);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(rtb));
                        using (var fs = File.Create(Path.Combine(dir, "activity-popup-" + theme + ".png"))) enc.Save(fs);
                    }
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new Exception("popup: " + failure.Message, failure);
        }

        static bool Alive(int pid)
        {
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; }
            catch { return false; }
        }

        static List<int> Pids(string file)
        {
            if (!File.Exists(file)) return new List<int>();
            return File.ReadAllLines(file).Where(l => l.Trim().Length > 0).Select(int.Parse).ToList();
        }

        static void KimiManagedServer()
        {
            string home = NewDir("kimi-home-managed"), mark = NewDir("kimi-mark");
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
            Environment.SetEnvironmentVariable("FAKE_KIMI_MARK", mark);
            Environment.SetEnvironmentVariable("FAKE_KIMI_MODE", "ok");
            var cfg = new Config();
            cfg.Svc("kimi").Command = fakeKimi;
            cfg.Svc("kimi").StartServer = true;
            cfg.Svc("kimi").TimeoutSeconds = 10;
            // чужой процесс того же образа вне Job виджета: его не трогаем никогда
            var foreign = Process.Start(new ProcessStartInfo(fakeKimi, "child-sleep") { UseShellExecute = false, CreateNoWindow = true });
            var mgr = new KimiServerManager();
            try
            {
                var s = new KimiProvider(mgr).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(s.Status == SvcStatus.Ok && s.Source.Contains("скрытый сервер виджета"), "owned hidden server serves quota: " + s.Message);
                Check(mgr.Launches == 1 && mgr.IsRunning, "one owned server");
                var again = new KimiProvider(mgr).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(again.Status == SvcStatus.Ok && mgr.Launches == 1, "reused, no duplicate launch");
                var servers = Pids(Path.Combine(mark, "server-pids.txt"));
                var children = Pids(Path.Combine(mark, "child-pids.txt"));
                Check(servers.Count == 1 && children.Count == 1 && Alive(servers[0]) && Alive(children[0]), "server and its child running");
                Check(mgr.Owns(servers[0]) && mgr.Owns(children[0]) && !mgr.Owns(foreign.Id), "ownership by Job: own tree only");
                mgr.Dispose();
                Thread.Sleep(500);
                Check(!Alive(servers[0]) && !Alive(children[0]), "owned process tree stopped on exit");
                Check(Alive(foreign.Id), "foreign process untouched");
                string log = Path.Combine(output, "widget.log");
                Check(!File.Exists(log) || !File.ReadAllText(log).Contains("CANARY-BEARER"), "token-bearing startup output never logged");
            }
            finally
            {
                try { foreign.Kill(); } catch { }
                mgr.Dispose();
            }

            // падение при запуске: пауза, без шторма перезапусков
            home = NewDir("kimi-home-crash");
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
            Environment.SetEnvironmentVariable("FAKE_KIMI_MODE", "crash");
            var clock = DateTime.UtcNow;
            var crashMgr = new KimiServerManager();
            crashMgr.UtcNow = () => clock;
            try
            {
                var s1 = new KimiProvider(crashMgr).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(s1.Status != SvcStatus.Ok && crashMgr.Launches == 1 && crashMgr.Failures >= 1, "crash recorded: " + s1.Message);
                for (int i = 0; i < 3; i++) new KimiProvider(crashMgr).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(crashMgr.Launches == 1, "backoff prevents restart storm");
                clock = clock.AddMinutes(3);
                new KimiProvider(crashMgr).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(crashMgr.Launches == 2 && crashMgr.Failures >= 2, "retry after backoff, failure count grows");
                Check(crashMgr.NextAllowedUtc - clock >= TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1), "backoff grows");
            }
            finally { crashMgr.Dispose(); }

            // сервер без проверки токена отклоняется и гасится
            home = NewDir("kimi-home-noauth");
            mark = NewDir("kimi-mark-noauth");
            Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
            Environment.SetEnvironmentVariable("FAKE_KIMI_MARK", mark);
            Environment.SetEnvironmentVariable("FAKE_KIMI_MODE", "noauth");
            var noAuth = new KimiServerManager();
            try
            {
                var s = new KimiProvider(noAuth).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(s.Status != SvcStatus.Ok && noAuth.DisabledReason != null, "unauthenticated server rejected");
                Thread.Sleep(300);
                Check(Pids(Path.Combine(mark, "server-pids.txt")).All(p => !Alive(p)), "rejected server stopped");
                new KimiProvider(noAuth).Collect(Ctx(cfg, "kimi", null).Also(c => c.FixtureOnly = false));
                Check(noAuth.Launches == 1, "rejected server not relaunched");
            }
            finally { noAuth.Dispose(); }
            Environment.SetEnvironmentVariable("FAKE_KIMI_MODE", null);
        }

        static void CodexActivity()
        {
            string log = Path.Combine(output, "codex-requests.log");
            var cfg = new Config();
            cfg.Svc("codex").Command = fakeCodex;
            cfg.Svc("codex").TimeoutSeconds = 20;
            Environment.SetEnvironmentVariable("FAKE_CODEX_LOG", log);

            Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", "ok");
            if (File.Exists(log)) File.Delete(log);
            var ctx = Ctx(cfg, "codex", null).Also(c => c.FixtureOnly = false);
            ServiceSnapshot core = null;
            ctx.Core = c => core = c.Clone();
            var s = new CodexProvider().Collect(ctx);
            Check(s.Status == SvcStatus.Ok && s.Bars.Count == 2, "codex quotas");
            Check(core != null && core.Activity == null && core.Bars.Count == 2, "quotas published before activity");
            Check(s.AutoPlan == "Pro" && s.Plan == "Pro" && s.PlanSource == "provider", "planType -> automatic plan");
            var a = s.Activity;
            Check(a != null && a.LifetimeTokens == 123456789 && a.PeakDailyTokens == 2500000 && a.CurrentStreakDays == 4 && a.LongestStreakDays == 11 && a.LongestRunningTurnSec == 1830, "summary parsed");
            var week = a.LastWeek(DateTime.Today);
            Check(week.Count == 7 && week[6].Tokens == 250000 && week[4].Tokens == 1000000 && !week[5].Tokens.HasValue, "seven days; missing day null, not zero");
            Check(a.MeasuredAt.HasValue && s.DataTime.HasValue && a.MeasuredAt.Value >= s.DataTime.Value, "activity has its own timestamp");
            var lines = File.ReadAllLines(log);
            var usage = lines.First(l => l.Contains("account/usage/read"));
            Check(!usage.Contains("threadId") && usage.Contains("\"params\":{}"), "account-wide usage request without threadId");
            Check(lines.Count(l => l.Contains("\"method\":\"initialize\"")) == 1, "same app-server process for quotas and activity");
            var proj = Json.Serialize(TelemetryPublisher.ProjectProvider(s.Also(x => x.LastAttemptAt = DateTime.UtcNow)), false);
            Check(!proj.Contains("lifetime") && !proj.Contains("123456789") && !proj.Contains("Pro"), "activity and plan not in public telemetry");

            cfg.Svc("codex").Plan = "Team (вручную)";
            var manual = new CodexProvider().Collect(Ctx(cfg, "codex", null).Also(c => c.FixtureOnly = false));
            Check(manual.Plan == "Team (вручную)" && manual.PlanSource == "manual" && manual.AutoPlan == "Pro", "manual plan wins over provider field");
            cfg.Svc("codex").Plan = null;

            Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", "error");
            var err = new CodexProvider().Collect(Ctx(cfg, "codex", null).Also(c => c.FixtureOnly = false));
            Check(err.Status == SvcStatus.Ok && err.Bars.Count == 2 && err.Activity == null, "activity failure never breaks quotas");
            var st = new State();
            var prev = s.Clone();
            prev.Generation = 0;
            st.PutCache(prev);
            var withCache = new CodexProvider().Collect(Ctx(cfg, "codex", null).Also(c => { c.FixtureOnly = false; c.State = st; }));
            Check(withCache.Activity != null && withCache.Activity.Stale && withCache.Activity.LifetimeTokens == 123456789, "previous activity kept and marked stale");

            if (!NoHardKill)
            {
                Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", "hang");
                var sw = Stopwatch.StartNew();
                var hang = new CodexProvider().Collect(Ctx(cfg, "codex", null).Also(c => c.FixtureOnly = false));
                Check(hang.Status == SvcStatus.Ok && hang.Activity == null, "hanging activity read bounded");
                Check(sw.ElapsedMilliseconds < 12000, "activity budget <= 5 s (+ process close): " + sw.ElapsedMilliseconds);
            }

            Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", "nulls");
            var nulls = new CodexProvider().Collect(Ctx(cfg, "codex", null).Also(c => c.FixtureOnly = false));
            Check(nulls.Activity != null && !nulls.Activity.LifetimeTokens.HasValue && nulls.Activity.Days == null && nulls.Activity.LastWeek(DateTime.Today) == null, "null summary stays null");
            Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", null);
        }

        static void AgyCreditsCache()
        {
            var cache = new AntigravityProvider.CreditsCache();
            var t0 = DateTime.UtcNow;
            bool cached;
            int calls = 0;
            Func<double?> q = () => { calls++; return 42; };
            Check(cache.Get(t0, q, out cached) == 42 && !cached, "first credits query");
            Check(cache.Get(t0.AddMinutes(14), q, out cached) == 42 && cached && calls == 1, "credits cached 15 min");
            cache.Get(t0.AddMinutes(16), () => { calls++; return null; }, out cached);
            Check(calls == 2 && !cached, "refresh after ttl");
            Check(cache.Get(t0.AddMinutes(20), q, out cached) == null && cached && calls == 2, "failure cached too (no hammering)");
            Check(AntigravityProvider.CreditsCache.TimeoutMs == 10000, "credits timeout 10 s");
        }

        static void ConfigWriterAndPlans()
        {
            string dir = NewDir("config");
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{\"allowLocalTokens\":false,\"customRoot\":7,\"services\":{\"Codex\":{\"enabled\":true,\"plan\":\"Legacy Pro\",\"futureKey\":[1]},\"kimi\":{\"enabled\":false}}}");
            Check(ConfigFile.Update(path, root =>
            {
                ConfigFile.Set(ConfigFile.Service(root, "kimi"), "plan", "Allegretto");
                ConfigFile.Set(ConfigFile.Service(root, "claude"), "profileUrl", "https://claude.ai/settings/billing");
            }), "config updated");
            var raw = Json.Parse(File.ReadAllText(path));
            Check(Json.Num(Json.Get(raw, "customRoot")) == 7 && Json.Get(raw, "services.Codex.futureKey") != null, "unknown config fields preserved");
            Check((string)Json.Get(raw, "services.Codex.plan") == "Legacy Pro", "legacy manual plan preserved");
            Check(Json.Get(raw, "services.codex") == null, "service key matched case-insensitively");
            var cfg = Config.Load(path);
            Check(cfg.Svc("kimi").Plan == "Allegretto" && cfg.Svc("codex").Plan == "Legacy Pro", "plans reload");
            var snap = new ServiceSnapshot { Id = "codex", AutoPlan = "Pro" };
            PlanResolver.Apply(snap, cfg.Svc("codex"));
            Check(snap.Plan == "Legacy Pro" && snap.PlanSource == "manual", "manual > provider");
            var noManual = new ServiceSnapshot { Id = "antigravity" };
            PlanResolver.Apply(noManual, cfg.Svc("antigravity"));
            Check(noManual.Plan == null && noManual.PlanSource == null, "unknown stays unknown");
            File.WriteAllText(path, "{broken");
            Check(!ConfigFile.Update(path, root => root["x"] = 1) && File.ReadAllText(path) == "{broken", "unparsed config never overwritten");
            Check(PlanResolver.SafeUrl("http://example.com") == null && PlanResolver.SafeUrl("javascript:alert(1)") == null, "only https links");
        }

        static void ProfileLinks()
        {
            var cfg = new Config();
            foreach (var id in new[] { "claude", "kimi", "antigravity", "codex" })
            {
                var svc = cfg.Svc(id);
                foreach (var u in new[] { svc.Url, svc.ProfileUrl })
                {
                    if (u == null) continue;
                    Check(PlanResolver.SafeUrl(u) != null, "https link " + u);
                    string l = u.ToLowerInvariant();
                    Check(!l.Contains("pricing") && !l.Contains("/plans") && !l.Contains("upgrade") && !l.Contains("buy"), "not a sales page: " + u);
                }
                Check(svc.ProfileUrl == null || svc.ProfileUrl != svc.Url, "profile destination separate from quota url: " + id);
                Check(svc.ProfileUrl != null, "every provider has a default profile/subscription link: " + id);
            }
        }

        static void CliCache()
        {
            string dir = NewDir("cli-path");
            string exe = Path.Combine(dir, "lwprobecli.exe");
            File.Copy(fakeCodex, exe);
            string oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", dir + ";" + oldPath);
                int n0 = Cli.SearchCount;
                Check(Cli.Resolve(null, "lwprobecli") == exe, "resolved");
                Check(Cli.Resolve(null, "lwprobecli") == exe && Cli.SearchCount == n0 + 1, "second resolve served from cache");
                Environment.SetEnvironmentVariable("PATH", oldPath + ";" + dir);
                Cli.Resolve(null, "lwprobecli");
                Check(Cli.SearchCount == n0 + 2, "PATH change invalidates cache");
                File.Delete(exe);
                Check(Cli.Resolve(null, "lwprobecli") == null && Cli.SearchCount == n0 + 3, "removed CLI re-searched");
                Cli.Resolve(null, "lwprobecli");
                Check(Cli.SearchCount == n0 + 3, "miss cached (no repeated directory scans)");
                Check(Cli.Resolve(fakeCodex, "x") == fakeCodex, "configured command not cached-away");
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        }

        static void LoginScan()
        {
            string code = null, url = null;
            bool? ok = null;
            var done = new ManualResetEvent(false);
            var login = new CliLogin(fakeKimi, "login");
            login.CodeFound = c => code = c;
            login.UrlFound = u => url = u;
            login.Done = r => { ok = r; done.Set(); };
            login.Start();
            Check(done.WaitOne(15000), "login finished");
            Check(ok == true && code == "WXYZ-1234" && url == "https://auth.example.test/device", "device code and https url parsed: " + code + " " + url);
        }

        static void Version()
        {
            var fv = FileVersionInfo.GetVersionInfo(typeof(Program).Assembly.Location);
            Check(fv.FileVersion == "1.1.0.0" && Program.Version == "1.1.0", "version metadata 1.1.0: " + fv.FileVersion);
            Check(typeof(Program).Assembly.GetName().Version.ToString() == "1.1.0.0", "assembly version");
        }

        // ---------- UI ----------

        static List<ServiceSnapshot> UiSnaps()
        {
            var now = DateTime.Now;
            var list = new List<ServiceSnapshot>();
            var claude = new ServiceSnapshot { Id = "claude", Title = "Claude Code — очень длинное имя сервиса для проверки многоточия", Status = SvcStatus.Ok, StatusText = Fmt.Clock(now),
                Url = "https://claude.ai/settings/usage", ProfileUrl = "https://claude.ai/settings/billing", Plan = "Max", PlanSource = "provider", AutoPlan = "Max", AutoPlanSource = ClaudeAuth.Source };
            claude.Bars.Add(new LimitBar { Chip = "5 ч", Label = "Сессия · окно 5 часов", Used = 35, ResetsAt = now.AddHours(2) });
            claude.Bars.Add(new LimitBar { Chip = "7 д", Label = "Неделя · Fable", Used = null, Note = "нет данных о расходе" });
            list.Add(claude);
            var kimi = new ServiceSnapshot { Id = "kimi", Title = "Kimi", Status = SvcStatus.Error, StatusText = "нужен вход", Message = "Kimi Code просит войти", ActionId = KimiProvider.LoginAction, ActionLabel = "Войти в Kimi Code", Attention = true,
                Plan = "Allegretto", PlanSource = "provider", AutoPlan = "Allegretto", AutoPlanIsLevel = true, AutoPlanSource = KimiProvider.LevelSource, ProfileUrl = "https://www.kimi.com/membership/subscription" };
            kimi.Bars.Add(ProviderUtil.Placeholder("5 ч", "Окно 5 часов · только Kimi Code"));
            list.Add(kimi);
            var agy = new ServiceSnapshot { Id = "antigravity", Title = "Antigravity", Status = SvcStatus.Stale, StatusText = "данные 09:10", ProfileUrl = "https://myaccount.google.com/payments-and-subscriptions" };
            agy.Bars.Add(new LimitBar { Chip = "5 ч", Label = "Gemini · окно 5 часов", Used = 90 });
            list.Add(agy);
            var codex = new ServiceSnapshot { Id = "codex", Title = "GPT / Codex", Status = SvcStatus.Ok, StatusText = Fmt.Clock(now), Plan = "Pro", PlanSource = "provider", AutoPlan = "Pro", AutoPlanSource = "Codex app-server: account/rateLimits/read → planType",
                Url = "https://chatgpt.com/codex/settings/usage", ProfileUrl = "https://chatgpt.com/#settings/Account" };
            codex.Bars.Add(new LimitBar { Chip = "7 д", Label = "Неделя · Codex и «Работа»", Used = 62, ResetsAt = now.AddDays(3) });
            codex.Activity = new TokenActivity { MeasuredAt = DateTime.UtcNow, LifetimeTokens = 123456789, PeakDailyTokens = null, CurrentStreakDays = 3, LongestStreakDays = 9, LongestRunningTurnSec = 4000,
                Days = new List<DailyTokens> { new DailyTokens { Date = DateTime.Today.AddDays(-3), Tokens = 900000 }, new DailyTokens { Date = DateTime.Today, Tokens = 300000 } } };
            list.Add(codex);
            return list;
        }

        static IEnumerable<DependencyObject> Logical(DependencyObject root)
        {
            yield return root;
            foreach (var c in LogicalTreeHelper.GetChildren(root))
            {
                var d = c as DependencyObject;
                if (d == null) continue;
                foreach (var x in Logical(d)) yield return x;
            }
        }

        static void UiMatrix()
        {
            string dir = NewDir("render");
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    foreach (var theme in new[] { "light", "dark" })
                        foreach (var compact in new[] { false, true })
                            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
                                foreach (var expanded in new[] { false, true })
                                {
                                    if (compact && expanded) continue;
                                    var t = Theme.Resolve(theme);
                                    var act = new CardActions { Refresh = () => { }, Menu = a => { }, OpenUrl = u => { }, Run = id => { }, TogglePin = () => { }, Minimize = () => { },
                                        CycleTheme = () => { }, ToggleActivity = () => { }, ActivityExpanded = expanded };
                                    var view = CardView.Build(UiSnaps(), t, DateTime.Now, DateTime.Now, false, act, compact, 1.0);
                                    if (scale != 1) view.LayoutTransform = new ScaleTransform(scale, scale);
                                    var host = new Border { Background = Brushes.Gray, Child = view };
                                    host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                    host.Arrange(new Rect(host.DesiredSize));
                                    host.UpdateLayout();
                                    Check(Math.Abs(host.ActualWidth - (CardView.CardWidth * scale + 28)) < 2,   // поля root не масштабируются LayoutTransform
                                         "card width stable with long names at scale " + scale + ": " + host.ActualWidth);

                                    // шапка: имя сжато многоточием, статус целиком внутри карточки
                                    var titleBlock = Logical(view).OfType<TextBlock>().First(x => x.Text != null && x.Text.StartsWith("Claude Code — очень"));
                                    titleBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                    Check(titleBlock.ActualWidth + 0.5 < titleBlock.DesiredSize.Width, "long name trimmed with ellipsis");
                                    foreach (var card in Logical(view).OfType<Border>().Where(b => b.Tag is string))
                                    {
                                        var status = Logical(card).OfType<StackPanel>().First(p => System.Windows.Automation.AutomationProperties.GetName(p).StartsWith("Состояние"));
                                        var right = status.TranslatePoint(new Point(status.ActualWidth, 0), card).X;
                                        Check(right <= card.ActualWidth + 0.5, "status fully visible in header of " + card.Tag);
                                    }
                                    // клавиатура: все кнопки фокусируемы и названы
                                    var buttons = Logical(view).OfType<Button>().ToList();
                                    Check(buttons.Count >= 10, "buttons present");
                                    foreach (var b in buttons)
                                    {
                                        b.ApplyTemplate();
                                        Check(b.Focusable && b.IsTabStop, "button keyboard-focusable");
                                        Check(!string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b)) || b.Content is TextBlock, "button has accessible name");
                                    }
                                    // шапка по образцу: одна стрелка (профиль/подписка) на карточку, отдельной кнопки лимитов нет
                                    foreach (var card in Logical(view).OfType<Border>().Where(b => b.Tag is string))
                                    {
                                        var head = Logical(card).OfType<ShrinkFirstPanel>().First();
                                        var links = Logical(head).OfType<Button>().ToList();
                                        Check(links.Count == 1 && (System.Windows.Automation.AutomationProperties.GetName(links[0]) ?? "").StartsWith("Профиль и подписка"), "exactly one profile/subscription arrow in header of " + card.Tag);
                                        Check(((TextBlock)links[0].Content).Text == "", "external-arrow glyph");
                                    }
                                    Check(!buttons.Any(b => (System.Windows.Automation.AutomationProperties.GetName(b) ?? "").StartsWith("Страница лимитов")), "no separate quota arrow in header");
                                    var pages = CardView.QuotaPages(UiSnaps());
                                    Check(pages.Count == 2 && pages.All(pg => pg.Key.StartsWith("Страница лимитов · ") && pg.Value.StartsWith("https://")), "quota pages kept as menu actions");
                                    Check(compact || buttons.Any(b => (System.Windows.Automation.AutomationProperties.GetName(b) ?? "").Contains("статистику Codex")), "activity toggle");
                                    var chips = Logical(view).OfType<Border>().Where(b => (System.Windows.Automation.AutomationProperties.GetName(b) ?? "").StartsWith("Тариф")).ToList();
                                    Check(chips.Count == 4, "subscription badge on all four headers");
                                    var chipTexts = chips.Select(c => ((TextBlock)c.Child).Text).ToList();
                                    Check(chipTexts.SequenceEqual(new[] { "Max", "Allegretto", "тариф?", "Pro" }), "badge shows plan name only: " + string.Join("|", chipTexts));
                                    Check(chips.All(c => c.ToolTip is string && ((string)c.ToolTip).Length > 0 && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetHelpText(c))), "source in tooltip/help text");

                                    var rtb = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                                    rtb.Render(host);
                                    var enc = new PngBitmapEncoder();
                                    enc.Frames.Add(BitmapFrame.Create(rtb));
                                    string name = string.Format("card-{0}-{1}-{2:0}{3}.png", theme, compact ? "compact" : "full", scale * 100, expanded ? "-expanded" : "");
                                    using (var fs = File.Create(Path.Combine(dir, name))) enc.Save(fs);
                                }

                    // диалог «Подписки»: строится, поля доступны с клавиатуры, сохраняет только plan/profileUrl
                    string cfgDir = NewDir("dialog");
                    string path = Path.Combine(cfgDir, "config.json");
                    File.WriteAllText(path, "{\"x\":1,\"services\":{\"codex\":{\"enabled\":true,\"plan\":\"Old\"}}}");
                    var dlg = new SubscriptionsDialog(Config.Load(path), UiSnaps(), Theme.Resolve("light"));
                    var boxes = Logical((DependencyObject)dlg.Content).OfType<TextBox>().ToList();
                    Check(boxes.Count == 8 && boxes.All(b => b.Focusable && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(b))), "dialog fields accessible");
                    var labels = Logical((DependencyObject)dlg.Content).OfType<Label>().Where(l => l.Target != null).ToList();
                    Check(labels.Count == 8, "labels bound to fields (access keys)");
                    Check(Logical((DependencyObject)dlg.Content).OfType<TextBlock>().Any(x => x.Text != null && x.Text.Contains("planType")), "automatic source visible in dialog");
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw new Exception("UI: " + failure.Message, failure);
        }

        // ---------- короткие сообщения вместо технических текстов ----------

        // Реальные тексты с экрана оператора 13.11.2026 (интернет был отключён).
        const string ScreenCodex = "codex app-server: failed to fetch codex rate limits: error sending request for url (https://chatgpt.com/backend-api/wham/usage) (2026-11-13T00:17:54.379481Z ERROR codex_app_server: Codex is ignoring 1 unrecognized configuration setting. Check for typos or deprecated se…)";
        const string ScreenAgyTimeout = "agy /usage: не ответил вовремя";
        const string ScreenAgyNet = "agy /usage: код 1: error: /usage failed: retrieving quota summary: failed to retrieve user quota summary: failed to get load code assist response: Post \"https:…";
        const string ScreenClaude = "claude /usage не вернул лимитов: You are currently using your subscription to power your Claude Code usage";
        const string ScreenKimi = "Локальный сервер Kimi Code не найден. Запустите Kimi Code (`kimi web --no-open`), включите services.kimi.startServer или разрешите прямой запрос: allowLocalTokens в config.json";

        // Собственные сообщения виджета — короткие, по-русски: предохранитель их трогать не должен.
        static readonly string[] HumanTexts =
        {
            "Kimi Code просит войти — нажмите «Войти в Kimi Code» (вход выполняет сам Kimi Code).",
            "Вход Claude Code истёк — нажмите «Войти».",
            "Вход Claude Code истёк. Пока показаны данные приложения Claude — без лимитов по моделям и времени сброса.",
            "Лимит исчерпан — Claude Code работает за счёт Usage credits.",
            "Лимит исчерпан — Codex работает за счёт кредитов. Можно применить бесплатный сброс в Codex.",
            "Кредиты рабочей области закончились.",
            "Достигнут лимит использования рабочей области.",
            "Claude Code не найден — установите его или укажите путь в config.json.",
            "Antigravity CLI не найден — установите его или укажите путь в config.json.",
            "Codex не найден — установите его или укажите путь в config.json.",
            "Kimi Code не найден — установите его или укажите путь в config.json.",
            "Локальный сервер Kimi Code не найден — запустите Kimi Code.",
            "Вход Kimi Code продлевает сам Kimi Code — запустите его, виджет подхватит сессию.",
            "Скрытый сервер Kimi Code недавно завершился — повтор после 14:03.",
            "Скрытый сервер Kimi Code не запущен — подробности в widget.log.",
            "Сервер Kimi Code вернул недопустимые значения квот — показан прошлый снимок.",
            "Ответ Kimi без квот",
            "Вход не завершён. Можно повторить в отдельном окне — там же видна ссылка для входа.",
            UserText.NeutralNote
        };

        static string ReadLog()
        {
            string path = Path.Combine(Program.BaseDir, "widget.log");
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
        }

        // Журнал секции — отдельный каталог: проверяем только то, что записано внутри неё.
        static void WithLog(string name, Action body)
        {
            string saved = Program.BaseDir;
            Program.BaseDir = NewDir(name);
            try { body(); }
            finally { Program.BaseDir = saved; }
        }

        static bool Technical(string text) { return UserText.LooksTechnical(text); }

        static bool NoRawMarkers(string t)
        {
            return t != null && t.IndexOf("://", StringComparison.Ordinal) < 0 && t.IndexOf("ERROR", StringComparison.Ordinal) < 0
                && t.IndexOf("код ", StringComparison.Ordinal) < 0 && t.IndexOf("app-server", StringComparison.Ordinal) < 0
                && t.IndexOf("/usage", StringComparison.Ordinal) < 0 && t.IndexOf('`') < 0 && t.IndexOf('{') < 0;
        }

        static void ShortErrorsWording()
        {
            // четыре случая с экрана: каждый — технический, а замена — короткая фраза без кода, адресов и имён процессов
            foreach (var raw in new[] { ScreenCodex, ScreenAgyTimeout, ScreenAgyNet, ScreenClaude, ScreenKimi })
                Check(Technical(raw), "screen text detected as technical: " + raw.Substring(0, 24));
            foreach (var name in new[] { "Codex", "GPT / Codex", "Claude Code", "Antigravity", "Kimi" })
                foreach (var raw in new[] { ScreenCodex, ScreenAgyTimeout, ScreenAgyNet, ScreenClaude, ScreenKimi })
                {
                    string h = UserText.Humanize(name, raw);
                    Check(NoRawMarkers(h) && !Technical(h) && h.Length < 110 && h.Contains(name), "humanized text is short and clean: " + h);
                }
            Check(UserText.Humanize("Codex", ScreenCodex).StartsWith("Нет связи с сервером Codex"), "codex screen text -> no link");
            Check(UserText.Humanize("Antigravity", ScreenAgyNet).StartsWith("Нет связи с сервером Antigravity"), "agy screen text -> no link");
            Check(UserText.Humanize("Antigravity", ScreenAgyTimeout) == "Antigravity не ответил вовремя — повторю при следующем обновлении.", "agy timeout phrase");
            Check(UserText.Humanize("Claude Code", ScreenClaude) == "Не удалось получить данные Claude Code — повторю при следующем обновлении.", "claude screen text -> generic phrase");
            Check(UserText.Humanize("Kimi", ScreenKimi).StartsWith("Не удалось получить данные Kimi"), "kimi long instruction -> generic phrase");
            Check(UserText.Humanize("Kimi", "Kimi usages: HTTP 401") == "Kimi просит войти заново.", "401 -> login");
            Check(UserText.Humanize("Kimi", "forbidden").EndsWith("просит войти заново."), "403/forbidden -> login");
            Check(UserText.Humanize("Codex", "HTTP 401 connection reset").EndsWith("просит войти заново."), "login wins over link");
            Check(UserText.Humanize("Codex", "HTTP 429 too many requests") == "Codex временно ограничил запросы — повторю позже.", "429 -> rate limited");
            Check(UserText.Humanize("Codex", "operation timed out") == "Codex не ответил вовремя — повторю при следующем обновлении.", "timeout phrase");
            Check(UserText.Humanize("Codex", "что-то странное") == "Не удалось получить данные Codex — повторю при следующем обновлении.", "other -> generic phrase");
            Check(UserText.Humanize(null, null) == "Не удалось получить данные сервиса — повторю при следующем обновлении.", "null-safe");
            Check(UserText.ServiceName("claude", null) == "Claude Code" && UserText.ServiceName("kimi", " ") == "Kimi" && UserText.ServiceName("codex", "GPT / Codex") == "GPT / Codex", "service names");

            // человеческие тексты предохранитель не трогает
            foreach (var t in HumanTexts) Check(!Technical(t), "human text kept: " + t);
            foreach (var kind in new[] { FailKind.Other, FailKind.NeedLogin, FailKind.NoLink, FailKind.RateLimited, FailKind.TimedOut })
                foreach (var offline in new[] { false, true })
                    foreach (var name in new[] { "Codex", "GPT / Codex", "Claude Code", "Antigravity", "Kimi" })
                        Check(!Technical(UserText.Phrase(name, kind, offline)), "phrase is never technical: " + UserText.Phrase(name, kind, offline));

            // будущие ошибки, которых нет ни в одном списке
            foreach (var future in new[]
            {
                "Quota backend rejected the request, retry later", "Service temporarily unavailable", "quota exceeded", "Timeout", "ECONNRESET",
                "Сбой: ECONNRESET", "Unexpected token < in JSON at position 0", "HTTP 502", "Ошибка 0x80070005", "at Foo.Bar(String x)", "{\"error\":1}",
                "line one\nline two", "C:\\Users\\x\\file.json не читается", "failed 2026-11-13T00:17:54", "Запуск codex app-server не удался", "claude /usage не ответил", "kimi не отвечает", new string('я', 161)
            })
                Check(Technical(future), "future technical text caught: " + (future.Length > 30 ? future.Substring(0, 30) : future));
            Check(!Technical(null) && !Technical("") && !Technical("OK"), "empty/short values are not technical");

            // журнал: без Bearer, ключей в параметрах, длинных токенов и параметров адресов; адрес без параметров виден
            string red = UserText.Redact(ScreenCodex + " Authorization: Bearer SECRETBEARER123 token=SECRETTOKEN456 https://x.test/a?access_token=SECRETQUERY789&b=1 key " + new string('a', 20) + "1" + new string('b', 20));
            Check(red.Contains("https://chatgpt.com/backend-api/wham/usage") && red.Contains("error sending request"), "log keeps the original text and address");
            Check(!red.Contains("SECRETBEARER") && !red.Contains("SECRETTOKEN") && !red.Contains("SECRETQUERY") && !red.Contains("access_token") && !red.Contains(new string('a', 20)), "log redacts bearer, secrets, query and long tokens: " + red);
            Check(red.Contains("https://x.test/a"), "address kept without parameters");
            Check(UserText.Redact(new string('x', 1000)).Length == 600 && UserText.Redact("a\r\nb\n  c") == "a b c", "log text: one line, at most 600 chars");
            Check(UserText.Redact(null) == "", "redact null");
            Check(UserText.Scrub("{\"token\":\"SECRETJSON\",\"x\":1}") == "{\"token\":\"…\",\"x\":1}", "json secret pair scrubbed: " + UserText.Scrub("{\"token\":\"SECRETJSON\",\"x\":1}"));

            // сам журнал виджета чистит строки при записи
            WithLog("short-log-scrub", () =>
            {
                Log.Write("probe Bearer LOGSECRET1 and https://h.test/p?sig=LOGSECRET2 password=LOGSECRET3");
                string log = ReadLog();
                Check(log.Contains("probe") && log.Contains("https://h.test/p") && !log.Contains("LOGSECRET"), "widget.log scrubbed on write: " + log);
            });
        }

        static void ShortErrorsOfflineAndRetry()
        {
            int probes = 0;
            DateTime clock = new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc);
            NetState.UtcNow = () => clock;
            NetState.Probe = () => { probes++; return true; };
            NetState.Reset();
            try
            {
                bool link;
                string offline = UserText.Failure("Codex", "что-то странное", out link);
                Check(link && offline == "Нет связи с интернетом — Codex обновится, как только появится сеть.", "no network: separate short phrase");
                UserText.Failure("Codex", "и ещё", out link);
                Check(probes == 1, "network check cached");
                clock = clock.AddSeconds(11);
                UserText.Failure("Codex", "и ещё", out link);
                Check(probes == 2, "network check refreshed after 10 s");
                NetState.Reset();
                UserText.Failure("Codex", "и ещё", out link);
                Check(probes == 3, "reset forces a new check");
                string login = UserText.Failure("Codex", "HTTP 401", out link);
                Check(!link && login == "Codex просит войти заново.", "login problem is not reported as no network");
                NetState.Probe = () => { throw new InvalidOperationException("probe broke"); };
                NetState.Reset();
                Check(!NetState.IsOffline(), "broken probe is not 'no network'");
                NetState.Probe = () => false;
                NetState.Reset();
                string noLink = UserText.Failure("Codex", ScreenCodex, out link);
                Check(link && noLink.StartsWith("Нет связи с сервером Codex"), "server unreachable while network is up: link failure");
                string other = UserText.Failure("Codex", "что-то странное", out link);
                Check(!link && other.StartsWith("Не удалось получить данные Codex"), "unknown failure with network up: no link flag");

                // повтор: после сбоя связи — не позже минуты, иначе обычный интервал
                Check(RetryPolicy.DueSeconds(true, 300) == RetryPolicy.LinkRetrySeconds && RetryPolicy.LinkRetrySeconds <= 60, "link failure -> retry within a minute");
                Check(RetryPolicy.DueSeconds(true, 30) == 30 && RetryPolicy.DueSeconds(false, 300) == 300, "retry never slower than the interval; no failure -> normal interval");
                Check(RetryPolicy.AfterNetworkSeconds <= 5, "after the network appears: quick retry");

                // сборщик: сбой связи виден как «нет связи», LinkFailure держится до первого опроса без него
                WithLog("short-link-collector", () =>
                {
                    var cfg = new Config();
                    cfg.Order = new List<string> { "codex" };
                    cfg.Svc("codex").Enabled = true;
                    bool failNow = true;
                    var p = new Scripted { Id = "codex", Body = ctx =>
                    {
                        var s = ProviderUtil.NewSnap(ctx, "codex");
                        if (!failNow) return OkSnap(ctx, "codex", 10);
                        return ProviderUtil.FailTech(s, SvcStatus.Error, "ошибка", ScreenCodex, ProviderUtil.Placeholder("7 д", "Неделя"));
                    } };
                    var st = new State();
                    var col = new Collector(cfg, st, null, null, new IProvider[] { p }, null);
                    Check(!col.LinkFailure, "no failure before the first poll");
                    var r = col.Collect()[0];
                    Check(r.Status == SvcStatus.Error && r.StatusText == "нет связи" && r.Message.StartsWith("Нет связи с сервером") && col.LinkFailure, "link failure: card says no link, quick retry requested");
                    Check(st.GetAttempt("codex").Message == r.Message && NoRawMarkers(st.GetAttempt("codex").Message), "state keeps the short text, not the raw one");
                    failNow = false;
                    var ok = col.Collect()[0];
                    Check(ok.Status == SvcStatus.Ok && !col.LinkFailure, "next poll succeeds: normal rhythm again");
                    failNow = true;
                    var stale = col.Collect()[0];
                    Check(stale.Status == SvcStatus.Stale && stale.Bars.Count > 0 && stale.Message.StartsWith("Нет связи с сервером") && col.LinkFailure, "stale data keeps the bars, shows no-link phrase, asks for quick retry");
                    string log = ReadLog();
                    Check(log.Contains("error sending request for url (https://chatgpt.com/backend-api/wham/usage)"), "technical text is in widget.log");
                });
            }
            finally
            {
                NetState.UtcNow = () => DateTime.UtcNow;
                NetState.Probe = () => false;   // тесты никогда не ходят в сеть
                NetState.Reset();
            }
        }

        // Предохранитель сборщика: от любого провайдера, в том числе от ошибок, о которых никто не подумал.
        static void ShortErrorsBreaker()
        {
            WithLog("short-breaker", () =>
            {
                var cfg = new Config();
                cfg.Order = new List<string> { "claude", "kimi", "antigravity", "codex" };
                foreach (var id in cfg.Order) cfg.Svc(id).Enabled = true;
                Func<string, SvcStatus, string, Scripted> raw = (id, status, message) => new Scripted { Id = id, Body = ctx =>
                    ProviderUtil.Fail(ProviderUtil.NewSnap(ctx, id), status, "ошибка", message, ProviderUtil.Placeholder("5 ч", "x")) };
                var st = new State();
                var providers = new IProvider[]
                {
                    raw("claude", SvcStatus.Error, "Unexpected HTTP 502 from https://api.example.test/v1/usage?token=BREAKERSECRET1: {\"error\":\"bad gateway\"}"),
                    raw("kimi", SvcStatus.NeedsPermission, ScreenKimi),
                    raw("antigravity", SvcStatus.Error, "Quota backend rejected the request, retry later"),
                    new Scripted { Id = "codex", Body = ctx => { throw new InvalidOperationException("boom https://x.test/p?sig=BREAKERSECRET2 deep inside"); } }
                };
                var col = new Collector(cfg, st, null, null, providers, null);
                var res = col.Collect().ToDictionary(s => s.Id);
                foreach (var id in cfg.Order)
                {
                    var s = res[id];
                    Check(!Technical(s.Message) && NoRawMarkers(s.Message) && s.Message.Length < 110, id + ": no technical text reaches the card: " + s.Message);
                    Check(st.GetAttempt(id) != null && !Technical(st.GetAttempt(id).Message), id + ": state keeps the short text");
                }
                Check(res["claude"].Message.StartsWith("Нет связи с сервером Claude Code"), "502 from a provider that forgot FailTech: no link phrase");
                Check(res["antigravity"].Message.StartsWith("Не удалось получить данные Antigravity"), "future English error -> generic phrase");
                Check(res["codex"].Status == SvcStatus.Error && res["codex"].Message.StartsWith("Не удалось получить данные") && !res["codex"].Message.Contains("boom"), "provider exception -> short phrase");
                Check(res["kimi"].Status == SvcStatus.NeedsPermission && !res["kimi"].Message.Contains("startServer"), "long instruction is replaced");
                string log = ReadLog();
                Check(log.Contains("api.example.test/v1/usage") && log.Contains("bad gateway") && log.Contains("Quota backend rejected") && log.Contains("boom"), "raw texts are in widget.log");
                Check(!log.Contains("BREAKERSECRET"), "log has no query secrets: " + log);

                // успешный снимок: служебные пояснения не показываются, человеческие остаются
                var okCol = new Collector(cfg, new State(), null, null, new IProvider[]
                {
                    new Scripted { Id = "claude", Body = ctx =>
                    {
                        var s = OkSnap(ctx, "claude", 5);
                        s.Message = "Server said: please upgrade your plan now";
                        s.Notes.Add("error: something went wrong in module X");
                        s.Notes.Add("Тариф определён вручную");
                        return s;
                    } },
                    new Scripted { Id = "kimi", Body = ctx =>
                    {
                        var s = OkSnap(ctx, "kimi", 5);
                        s.Message = HumanTexts[3];
                        s.Notes.Add("Первая запись");
                        return s;
                    } }
                }, null);
                var ok = okCol.Collect().ToDictionary(s => s.Id);
                Check(ok["claude"].Status == SvcStatus.Ok && ok["claude"].Bars.Count == 1 && ok["claude"].Message == UserText.NeutralNote, "ok snapshot: technical message replaced, data kept");
                Check(ok["claude"].Notes.Count == 1 && ok["claude"].Notes[0] == "Тариф определён вручную", "technical note dropped (same text as the message), human note kept");
                Check(ok["kimi"].Message == HumanTexts[3] && ok["kimi"].Notes.Count == 1 && ok["kimi"].Notes[0] == "Первая запись", "human texts untouched");

                // устаревший снимок из кэша: данные остаются, текст ошибки — короткий
                bool fail = false;
                var staleCol = new Collector(cfg, new State(), null, null, new IProvider[] { new Scripted { Id = "claude", Body = ctx =>
                    fail ? ProviderUtil.Fail(ProviderUtil.NewSnap(ctx, "claude"), SvcStatus.Error, "ошибка", "You are currently using your subscription to power your Claude Code usage") : OkSnap(ctx, "claude", 7) } }, null);
                staleCol.Collect();
                fail = true;
                var stale = staleCol.Collect()[0];
                Check(stale.Status == SvcStatus.Stale && stale.Bars.Count == 1 && stale.Bars[0].Used == 7 && !Technical(stale.Message) && stale.Message.StartsWith("Не удалось получить данные Claude Code"), "stale card: data kept, short text: " + stale.Message);
            });
        }

        // Четыре примера с экрана через настоящие провайдеры и поддельные CLI (без сети и аккаунтов).
        static void ShortErrorsScreenCases()
        {
            WithLog("short-screen", () =>
            {
                Environment.SetEnvironmentVariable("FAKE_CLAUDE_LOG", null);
                Environment.SetEnvironmentVariable("FAKE_CLAUDE_AUTH", "max");
                var cfg = new Config();
                foreach (var id in cfg.Order) cfg.Svc(id).Enabled = true;
                Func<string, IProvider, ServiceSnapshot> run = (id, p) =>
                {
                    cfg.Order = new List<string> { id };
                    return new Collector(cfg, new State(), null, null, new IProvider[] { p }, null).Collect()[0];
                };

                // 1. Codex: ошибка сети от app-server (английский текст, адрес с параметром-секретом, ERROR в stderr)
                cfg.Svc("codex").Command = fakeCodex;
                cfg.Svc("codex").TimeoutSeconds = 20;
                Environment.SetEnvironmentVariable("FAKE_CODEX_USAGE", "ok");
                Environment.SetEnvironmentVariable("FAKE_CODEX_LIMITS", "neterror");
                var codex = run("codex", new CodexProvider());
                Environment.SetEnvironmentVariable("FAKE_CODEX_LIMITS", null);
                Check(codex.Status == SvcStatus.Error && codex.StatusText == "нет связи", "codex: card says no link");
                Check(codex.Message == "Нет связи с сервером GPT / Codex — повторю автоматически." && NoRawMarkers(codex.Message), "codex: short phrase instead of the raw error: " + codex.Message);
                string log = ReadLog();
                Check(log.Contains("failed to fetch codex rate limits: error sending request for url (https://chatgpt.com/backend-api/wham/usage)") && log.Contains("codex_app_server"), "codex: original text in widget.log: " + log);
                Check(!log.Contains("CANARYQUERYSECRET") && !log.Contains("CANARYBEARERTOKEN") && !log.Contains("access_token"), "codex: log has no query secret or bearer token");

                // 2. Claude: CLI не вернул лимитов и печатает английское пояснение
                cfg.Svc("claude").Command = fakeClaude;
                cfg.Svc("claude").TimeoutSeconds = 20;
                Environment.SetEnvironmentVariable("FAKE_CLI_USAGE", "subscription");
                var claude = run("claude", new ClaudeProvider());
                Check(claude.Status == SvcStatus.Error && claude.Message == "Не удалось получить данные Claude Code — повторю при следующем обновлении." && NoRawMarkers(claude.Message), "claude: generic short phrase: " + claude.Message);
                Check(ReadLog().Contains("You are currently using your subscription to power your Claude Code usage"), "claude: original text in widget.log");
                NetState.Probe = () => true;
                NetState.Reset();
                try
                {
                    var offline = run("claude", new ClaudeProvider());
                    Check(offline.Message == "Нет связи с интернетом — Claude Code обновится, как только появится сеть." && offline.StatusText == "нет связи", "claude with no network: no-link state: " + offline.Message);
                }
                finally
                {
                    NetState.Probe = () => false;
                    NetState.Reset();
                }

                // 3. Antigravity: сетевая ошибка agy (английский текст с адресом) и зависание
                cfg.Svc("antigravity").Command = fakeClaude;   // поддельный CLI отвечает на -p "/usage" так же, как agy
                cfg.Svc("antigravity").TimeoutSeconds = 20;
                Environment.SetEnvironmentVariable("FAKE_CLI_USAGE", "agy-net");
                var agy = run("antigravity", new AntigravityProvider());
                Check(agy.Status == SvcStatus.Error && agy.Message == "Нет связи с сервером Antigravity — повторю автоматически." && NoRawMarkers(agy.Message), "agy: network error -> short no-link phrase: " + agy.Message);
                log = ReadLog();
                Check(log.Contains("antigravity: agy /usage: код 1: error: /usage failed") && !log.Contains("CANARYQUERYSECRET"), "agy: main path is logged, without secrets");
                if (!NoHardKill)
                {
                    cfg.Svc("antigravity").TimeoutSeconds = 2;
                    Environment.SetEnvironmentVariable("FAKE_CLI_USAGE", "hang");
                    var hang = run("antigravity", new AntigravityProvider());
                    Check(hang.Message == "Antigravity не ответил вовремя — повторю при следующем обновлении." && hang.StatusText == "ошибка", "agy: timeout phrase without process names: " + hang.Message);
                    Check(ReadLog().Contains("antigravity: agy /usage: не ответил вовремя"), "agy: timeout is logged");
                }
                Environment.SetEnvironmentVariable("FAKE_CLI_USAGE", null);

                // 4. Kimi: нет сервера — одна короткая фраза вместо инструкции
                string home = NewDir("kimi-home-short");
                Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
                cfg.Svc("kimi").StartServer = false;
                var kimi = run("kimi", new KimiProvider(new KimiServerManager()));
                Check(kimi.Status == SvcStatus.NeedsPermission && kimi.Message == "Локальный сервер Kimi Code не найден — запустите Kimi Code.", "kimi: short phrase instead of the instruction: " + kimi.Message);
                Check(NoRawMarkers(kimi.Message) && kimi.Message.IndexOf("startServer", StringComparison.Ordinal) < 0 && kimi.Message.Length < 80, "kimi: no config keys or commands in the card");
                Check(ReadLog().Contains("services.kimi.startServer"), "kimi: how to enable the server is in widget.log");
            });
        }

        // Хвосты приёмки: исключения чтения ответа Kimi не роняют карточку; собственный сервер спрашивается раз за опрос.
        static void ShortErrorsKimiTails()
        {
            WithLog("short-kimi-tails", () =>
            {
                int status;
                string body = KimiProvider.Get("not a valid url", null, 1000, false, out status);
                Check(body == null && status == 0, "invalid address does not throw");

                // обрыв соединения посреди тела ответа
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                var t = new Thread(() =>
                {
                    while (true)
                    {
                        TcpClient c;
                        try { c = l.AcceptTcpClient(); }
                        catch { return; }
                        using (c)
                        {
                            try
                            {
                                var s = c.GetStream();
                                var r = new StreamReader(s, Encoding.ASCII);
                                string line;
                                while (!string.IsNullOrEmpty(line = r.ReadLine())) { }
                                var h = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 500\r\nConnection: close\r\n\r\n{\"partial\":");
                                s.Write(h, 0, h.Length);
                                s.Flush();
                                Thread.Sleep(300);
                                c.Client.LingerState = new LingerOption(true, 0);   // закрытие с RST: чтение тела оборвётся исключением
                            }
                            catch { }
                        }
                    }
                }) { IsBackground = true };
                t.Start();
                try
                {
                    string cut = null;
                    Exception thrown = null;
                    try { cut = KimiProvider.Get("http://127.0.0.1:" + port + "/api/v1/oauth/usage", "tok", 3000, false, out status); }
                    catch (Exception ex) { thrown = ex; }
                    Check(thrown == null, "connection reset while reading the body does not throw: " + (thrown == null ? "" : thrown.GetType().Name));

                    string home = NewDir("kimi-home-cut");
                    Environment.SetEnvironmentVariable("KIMI_CODE_HOME", home);
                    File.WriteAllText(Path.Combine(home, "server.token"), "synthetic-token");
                    WriteInstance(home, "cut", port, Process.GetCurrentProcess().Id, NowMs());
                    var cfg = new Config();
                    cfg.Order = new List<string> { "kimi" };
                    cfg.Svc("kimi").Enabled = true;
                    cfg.Svc("kimi").StartServer = false;
                    var r = new Collector(cfg, new State(), null, null, new IProvider[] { new KimiProvider(new KimiServerManager()) }, null).Collect()[0];
                    Check(r.Status == SvcStatus.Error && !r.Message.Contains("Сбой сбора") && !Technical(r.Message) && r.Message.Contains("Kimi"), "read failure does not drop the card: " + r.Message);
                }
                finally { l.Stop(); }

                // один опрос собственного сервера за цикл
                string ownHome = NewDir("kimi-home-once"), mark = NewDir("kimi-mark-once");
                Environment.SetEnvironmentVariable("KIMI_CODE_HOME", ownHome);
                Environment.SetEnvironmentVariable("FAKE_KIMI_MARK", mark);
                Environment.SetEnvironmentVariable("FAKE_KIMI_MODE", "ok");
                Environment.SetEnvironmentVariable("FAKE_KIMI_USAGE", "{\"code\":0,\"data\":{\"kind\":\"error\",\"message\":\"upstream down\"}}");
                var kcfg = new Config();
                kcfg.Order = new List<string> { "kimi" };
                kcfg.Svc("kimi").Enabled = true;
                kcfg.Svc("kimi").Command = fakeKimi;
                kcfg.Svc("kimi").StartServer = true;
                kcfg.Svc("kimi").TimeoutSeconds = 10;
                var mgr = new KimiServerManager();
                try
                {
                    var col = new Collector(kcfg, new State(), null, null, new IProvider[] { new KimiProvider(mgr) }, null);
                    string hits = Path.Combine(mark, "usage-hits.txt");
                    Func<int> count = () => File.Exists(hits) ? File.ReadAllLines(hits).Count(x => x.Trim().Length > 0) : 0;
                    var first = col.Collect()[0];
                    Check(mgr.Launches == 1 && count() == 1, "cycle 1: server started, asked once (hits " + count() + ")");
                    var second = col.Collect()[0];
                    Check(mgr.Launches == 1 && count() == 2, "cycle 2: running server asked once, not twice (hits " + count() + ")");
                    var third = col.Collect()[0];
                    Check(count() == 3, "cycle 3: still one request per cycle (hits " + count() + ")");
                    foreach (var s in new[] { first, second, third })
                        Check(s.Status == SvcStatus.Error && !Technical(s.Message) && NoRawMarkers(s.Message), "failed cycle shows a short phrase: " + s.Message);
                }
                finally
                {
                    mgr.Dispose();
                    Environment.SetEnvironmentVariable("FAKE_KIMI_USAGE", null);
                    Environment.SetEnvironmentVariable("FAKE_KIMI_MARK", null);
                }
            });
        }

        public static int Main(string[] args)
        {
            output = Path.GetFullPath(args[0]);
            fakeKimi = Path.GetFullPath(args[1]);
            fakeCodex = Path.GetFullPath(args[2]);
            fakeClaude = Path.GetFullPath(args[3]);
            Program.BaseDir = output;
            NetState.Probe = () => false;   // проверка сети в тестах подменена: настоящая ходит в сеть
            try
            {
                Section("model roundtrip (url/actions/attention/plan/activity)", ModelRoundtrip);
                Section("UTC storage, local tooltip", UtcTooltip);
                Section("latestAttempt separate from lastGood; unknown state fields", LatestAttemptAndState);
                Section("per-provider early results, enrichment race, generations", EarlyResultsAndRace);
                Section("Claude scoped/missing utilization", ClaudeScoped);
                Section("Kimi ratio validation", KimiRatios);
                Section("Kimi empty/revoked token", KimiEmptyToken);
                Section("Kimi candidate fallback", KimiCandidates);
                Section("Kimi status-less auth errors via /api/v1/auth (review P1-1)", KimiAuthErrors);
                Section("Kimi account level (userinfo) and subscription link", KimiLevel);
                Section("Kimi owned hidden server lifecycle", KimiManagedServer);
                Section("Codex activity in same app-server", CodexActivity);
                Section("Codex activity stale fallback through Collector (review P1-2)", CodexActivityCollector);
                Section("Claude subscriptionType from auth status", ClaudeSubscription);
                Section("Antigravity credits cache", AgyCreditsCache);
                Section("config writer and plan priority", ConfigWriterAndPlans);
                Section("profile/quota links", ProfileLinks);
                Section("CLI resolution cache", CliCache);
                Section("CLI login device code scan", LoginScan);
                Section("version metadata", Version);
                Section("UI render matrix, keyboard, Subscriptions dialog", UiMatrix);
                Section("compact widget: no scroll, activity in owned popup", ActivityPopupChecks);
                Section("popup owner monitor placement and DPI", ActivityPopupPlacement);
                Section("short errors: wording, technical-text detector, log redaction", ShortErrorsWording);
                Section("short errors: no-network state and quick retry", ShortErrorsOfflineAndRetry);
                Section("short errors: collector breaker for any provider", ShortErrorsBreaker);
                Section("short errors: four screen cases through real providers", ShortErrorsScreenCases);
                Section("short errors: Kimi read exceptions and one poll per cycle", ShortErrorsKimiTails);
                File.WriteAllLines(Path.Combine(output, "limits-tests.txt"), report);
                Console.WriteLine("PASS: " + sections + " sections, " + assertions + " assertions; offline fakes only");
                return 0;
            }
            catch (Exception e)
            {
                report.Add("FAIL: " + e.Message);
                File.WriteAllLines(Path.Combine(output, "limits-tests.txt"), report);
                Console.Error.WriteLine("FAIL: " + e);
                return 1;
            }
            finally
            {
                KimiServerManager.Shared.Dispose();
            }
        }
    }

    static class TestExt
    {
        public static T Also<T>(this T x, Action<T> f) { f(x); return x; }
    }
}
