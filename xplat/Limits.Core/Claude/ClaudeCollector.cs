using System;
using System.IO;
using System.Linq;
using System.Text;

namespace DesktopWidgets.Limits.Core.Claude
{
    // Порядок опроса как в WPF-версии: get_usage (stream-json) → запасной `claude -p "/usage"` → при отсутствии входа —
    // история приложения Claude. Процессы запускает IProcessRunner, пути даёт IAppPaths: ядро не знает, какая это ОС.
    public sealed class ClaudeCollector
    {
        static readonly string[] DropEnv = { "CLAUDE", "ANTHROPIC" };   // внутри сессии Claude CLI ведёт себя иначе
        public const string LoginAction = "claude-login";
        static readonly TimeSpan AuthAnsweredTtl = TimeSpan.FromMinutes(30), AuthFailedTtl = TimeSpan.FromMinutes(10);

        readonly IProcessRunner runner;
        readonly IAppPaths paths;
        readonly ILog log;
        readonly Func<DateTime> utcNow;
        readonly object authGate = new object();
        (bool Answered, string Plan)? authCache;
        DateTime authAt;

        public ClaudeCollector(IProcessRunner runner, IAppPaths paths, ILog log, Func<DateTime> utcNow = null)
        {
            this.runner = runner;
            this.paths = paths;
            this.log = log;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public int AuthQueries { get; private set; }

        public ServiceSnapshot Collect(ServiceConfig svc, long generation)
        {
            var snap = NewSnap(svc, generation);
            try { return CollectCore(snap, svc); }
            finally { snap.LastAttemptAt = utcNow(); }
        }

        static ServiceSnapshot NewSnap(ServiceConfig svc, long generation)
        {
            var s = new ServiceSnapshot { Id = "claude", Title = svc.Title, Generation = generation, ObservationTimeUnknown = true,
                Source = "claude -p \"/usage\" — лимиты тарифа Claude (общие для Claude Code и claude.ai)" };
            PlanResolver.Apply(s, svc);
            return s;
        }

        ServiceSnapshot CollectCore(ServiceSnapshot snap, ServiceConfig svc)
        {
            DateTime now = utcNow().ToLocalTime();
            int timeoutMs = Math.Max(1, svc.TimeoutSeconds) * 1000;
            string exe = runner.Resolve(svc.Command, "claude");
            if (exe == null)
                return Fail(snap, SvcStatus.NoData, "нет CLI", "claude не найден — укажите путь к CLI в настройках (services.claude.command)");

            object data = ReadUsage(exe, timeoutMs, svc.MaxProcesses, out string guError);
            if (data != null && ClaudeParser.FillFromUsage(snap, data, now))
            {
                MarkFresh(snap);
                EnrichPlan(snap, svc, exe, timeoutMs);
                return snap;
            }
            log.Write("claude get_usage: " + (guError ?? "rate_limits недоступны"));
            snap.Bars.Clear(); snap.Extras.Clear(); snap.Attention = false; snap.Message = null;

            var r = runner.Run(new CliRequest { Exe = exe, Args = new[] { "-p", "/usage", "--no-session-persistence", "--strict-mcp-config" },
                TimeoutMs = timeoutMs, MaxProcesses = svc.MaxProcesses, DropEnvPrefixes = DropEnv });
            if (!r.Ok) return Fail(snap, SvcStatus.Error, "ошибка", "claude /usage: " + r.Describe());
            snap.Bars.AddRange(ClaudeParser.ParseUsageText(r.StdOut, now));
            if (snap.Bars.Count == 0)
            {
                var auth = runner.Run(new CliRequest { Exe = exe, Args = new[] { "auth", "status" }, TimeoutMs = timeoutMs, DropEnvPrefixes = DropEnv });
                if (ClaudeParser.LoggedIn(auth.StdOut) == false)
                {
                    snap.ActionId = LoginAction;
                    snap.ActionLabel = "Войти в Claude Code";
                    string history = ReadDesktopHistory();
                    if (history != null && ClaudeParser.FillFromDesktopHistory(snap, history, now))
                    {
                        snap.Status = SvcStatus.Ok;
                        snap.StatusText = Fmt.Clock(snap.DataTime.Value);
                        snap.Attention = true;
                        snap.Message = "Вход Claude Code истёк. Пока показаны данные приложения Claude — без лимитов по моделям и времени сброса.";
                        return snap;
                    }
                    var failed = Fail(snap, SvcStatus.Error, "нужен вход", "Вход Claude Code истёк — нажмите «Войти».");
                    failed.Attention = true;
                    return failed;
                }
                string hint = Text.FirstLine(r.StdOut);
                return Fail(snap, SvcStatus.Error, "ошибка", "claude /usage не вернул лимитов" + (hint != null ? ": " + hint : ""));
            }
            MarkFresh(snap);
            EnrichPlan(snap, svc, exe, timeoutMs);
            return snap;
        }

        static ServiceSnapshot Fail(ServiceSnapshot snap, SvcStatus status, string statusText, string message)
        {
            snap.Status = status; snap.StatusText = statusText; snap.Message = message;
            snap.Bars.Clear();
            snap.Bars.AddRange(ClaudeParser.Placeholders());
            return snap;
        }

        void MarkFresh(ServiceSnapshot snap)
        {
            var completed = utcNow();
            snap.DataTime = snap.ObservationTimeUnknown ? (DateTime?)null : completed;
            foreach (var bar in snap.Bars)
                if (bar.Quota != null && !snap.ObservationTimeUnknown) bar.Quota.MeasuredAt = completed;
            snap.Status = SvcStatus.Ok;
            snap.StatusText = snap.DataTime.HasValue ? Fmt.Clock(snap.DataTime.Value.ToLocalTime()) : "время неизвестно";
        }

        // Служебный запрос get_usage: только чтение, в модель не уходит.
        object ReadUsage(string exe, int timeoutMs, int maxProcesses, out string error)
        {
            error = null;
            ICliSession s;
            try
            {
                s = runner.Start(new CliRequest { Exe = exe, TimeoutMs = timeoutMs, MaxProcesses = maxProcesses, DropEnvPrefixes = DropEnv,
                    Args = new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--no-session-persistence", "--strict-mcp-config" } });
            }
            catch (Exception ex) { error = "не запустился: " + ex.Message; return null; }
            using (s)
            {
                try
                {
                    s.WriteLine("{\"type\":\"control_request\",\"request_id\":\"lw-usage\",\"request\":{\"subtype\":\"get_usage\",\"skip_behaviors\":true}}");
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (true)
                    {
                        int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                        if (left <= 0) { error = "нет ответа на get_usage"; break; }
                        string line = s.ReadLine(left, out bool timedOut);
                        if (line == null) { error = timedOut ? "нет ответа на get_usage" : "CLI завершился без ответа"; break; }
                        var o = Json.TryParse(line);
                        if (Json.Str(Json.Get(o, "type")) != "control_response") continue;
                        if (Json.Str(Json.Get(o, "response.request_id")) != "lw-usage") continue;
                        if (Json.Str(Json.Get(o, "response.subtype")) == "success")
                        {
                            var data = Json.Get(o, "response.response");
                            if (data == null) error = "get_usage вернул пустой ответ";
                            return data;
                        }
                        error = Json.Str(Json.Get(o, "response.error")) ?? "get_usage не удался";
                        break;
                    }
                }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                string hint = s.StdErrHint();
                if (error != null && hint != null) error += " (" + hint + ")";
                return null;
            }
        }

        void EnrichPlan(ServiceSnapshot snap, ServiceConfig svc, string exe, int timeoutMs)
        {
            if (!string.IsNullOrWhiteSpace(svc.Plan)) return;   // ручной тариф главнее — CLI не запускается
            (bool Answered, string Plan) r;
            lock (authGate)
            {
                var now = utcNow();
                if (authCache.HasValue && now >= authAt && now - authAt < (authCache.Value.Answered ? AuthAnsweredTtl : AuthFailedTtl)) r = authCache.Value;
                else
                {
                    AuthQueries++;
                    var res = runner.Run(new CliRequest { Exe = exe, Args = new[] { "auth", "status", "--json" }, TimeoutMs = Math.Min(10000, timeoutMs), DropEnvPrefixes = DropEnv });
                    r = res.Ok ? ClaudeParser.ParseAuth(res.StdOut) : (false, null);
                    if (!res.Ok) log.Write("claude auth status: " + (res.StartError != null ? "не запустился" : res.TimedOut ? "не ответил вовремя" : "код " + res.ExitCode));
                    authCache = r; authAt = now;
                }
            }
            if (!r.Answered) return;
            snap.AutoPlan = r.Plan;
            snap.AutoPlanSource = r.Plan != null ? ClaudeParser.AuthSource : null;
            PlanResolver.Apply(snap, svc);
        }

        string ReadDesktopHistory()
        {
            try
            {
                string path = paths.ClaudeDesktopHistoryCandidates().Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (path == null) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                log.Write("claude desktop history: " + ex.GetType().Name);
                return null;
            }
        }
    }
}
