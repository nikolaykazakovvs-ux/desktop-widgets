using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LimitsWidget
{
    // `claude auth status --json` (Claude Code 2.1.318: loggedIn, subscriptionType) — только чтение статуса входа самим CLI.
    // Из ответа берётся одно поле subscriptionType; остальной вывод (аккаунт, организация, email) отбрасывается сразу,
    // в журнал попадают только код выхода/тайм-аут. «max» подтверждает тариф Max, но не множитель (5×/20×).
    // Ответ кэшируется: удача — на 30 минут, сбой или неподдерживаемый флаг — на 10 минут, чтобы не плодить процессы.
    static class ClaudeAuth
    {
        public const string Source = "Claude Code: claude auth status --json → subscriptionType";
        public const int TimeoutMs = 10000;
        public static readonly TimeSpan AnsweredTtl = TimeSpan.FromMinutes(30), FailedTtl = TimeSpan.FromMinutes(10);
        static readonly Regex Token = new Regex(@"^[a-z][a-z0-9_\-]{0,23}$", RegexOptions.Compiled);

        // Answered — CLI ответил разборчивым JSON (Plan может быть null: не вошёл или поля нет). Иначе — ответа нет.
        public sealed class Result
        {
            public bool Answered;
            public string Plan;
        }

        public sealed class Cache
        {
            readonly object gate = new object();
            string key;
            DateTime at;
            Result value;
            int queries;

            public int Queries { get { lock (gate) return queries; } }

            public Result Get(string exe, DateTime utcNow, Func<Result> query)
            {
                lock (gate)
                {
                    if (value != null && key == exe && utcNow >= at && utcNow - at < (value.Answered ? AnsweredTtl : FailedTtl)) return value;
                    queries++;
                    value = query() ?? new Result();
                    key = exe;
                    at = utcNow;
                    return value;
                }
            }
        }

        public static readonly Cache Shared = new Cache();

        public static Result Parse(string stdout)
        {
            var root = Json.Obj(Json.TryParse(stdout == null ? null : stdout.Trim()));
            if (root == null) return new Result();   // не JSON (старый CLI печатает текст) — ответа нет
            var res = new Result { Answered = true };
            if (Json.Bool(Json.Get(root, "loggedIn")) == false) return res;
            res.Plan = PlanName(Json.Get(root, "subscriptionType") as string);
            return res;
        }

        internal static string PlanName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string v = raw.Trim().ToLowerInvariant();
            switch (v)
            {
                case "free": return "Free";
                case "pro": return "Pro";
                case "max": return "Max";
                case "team": return "Team";
                case "enterprise": return "Enterprise";
            }
            // новое значение схемы — читаемо, без догадок о множителе; всё непохожее на идентификатор отбрасывается
            if (!Token.IsMatch(v)) return null;
            return string.Join(" ", v.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        // Внутри сессии Claude CLI ведёт себя иначе — окружение CLAUDE*/ANTHROPIC* не передаём (как и для get_usage).
        public static Result Query(string exe, int timeoutMs)
        {
            var r = Cli.Run(exe, "auth status --json", timeoutMs, 0, psi =>
            {
                var drop = new List<string>();
                foreach (string k in psi.EnvironmentVariables.Keys)
                    if (k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
                        drop.Add(k);
                foreach (string k in drop) psi.EnvironmentVariables.Remove(k);
            });
            if (!r.Ok)
            {
                Log.Write("claude auth status: " + (r.StartError != null ? "не запустился" : r.TimedOut ? "не ответил вовремя" : "код " + r.ExitCode));
                return new Result();
            }
            var res = Parse(r.StdOut);
            if (!res.Answered) Log.Write("claude auth status: ответ не JSON (флаг --json не поддерживается?)");
            return res;
        }
    }
}
