using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DesktopWidgets.Limits.Core.Claude
{
    // Чистый разбор ответов Claude Code — без процессов и файлов. Перенесено из ClaudeProvider.cs / ClaudeAuth.cs WPF-версии;
    // правила пулов, «нет процента ≠ 0 %» и неизвестное время наблюдения сохранены.
    public static class ClaudeParser
    {
        static readonly Regex UsageLine = new Regex(
            @"^\s*Current\s+(?<what>session|week)(?:\s*\((?<scope>[^)]+)\))?\s*:\s*(?<pct>\d+(?:[.,]\d+)?)\s*%\s*used(?:\s*\S\s*resets\s+(?<reset>.+?))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex TrailingZone = new Regex(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);
        static readonly Regex AmPm = new Regex(@"(\d)\s*(am|pm)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex PlanToken = new Regex(@"^[a-z][a-z0-9_\-]{0,23}$", RegexOptions.Compiled);
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        static readonly string[] DateFormats = { "MMM d, h:mmtt", "MMM d, htt", "MMM d h:mmtt", "MMM d htt", "MMM d, yyyy, h:mmtt", "MMM d, yyyy h:mmtt", "MMM d" };
        static readonly string[] TimeFormats = { "h:mmtt", "htt", "H:mm" };

        public const string AuthSource = "Claude Code: claude auth status --json → subscriptionType";
        static string Chip5 => "5" + Fmt.Nbsp + "ч";
        static string Chip7 => "7" + Fmt.Nbsp + "д";

        public static LimitBar[] Placeholders() => new[]
        {
            new LimitBar { Chip = Chip5, Label = "Сессия · окно 5 часов", Placeholder = true },
            new LimitBar { Chip = Chip7, Label = "Неделя · все модели", Placeholder = true }
        };

        static double? ValidPercent(double? v) =>
            !v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value) || v.Value < 0 ? null : v;

        static string PoolOf(string key)
        {
            switch (key)
            {
                case "five_hour": case "seven_day": return "subscription.general";
                case "seven_day_opus": return "subscription.opus";
                case "seven_day_sonnet": return "subscription.sonnet";
                case "seven_day_cowork": return "subscription.cowork";
                case "seven_day_oauth_apps": return "subscription.oauth_apps";
                default: return null;
            }
        }

        static LimitBar WindowBar(object rl, string key, string chip, string label)
        {
            var w = Json.Get(rl, key);
            if (Json.Obj(w) == null) return null;
            double? u = ValidPercent(Json.Num(Json.Get(rl, key + ".utilization")));
            var bar = new LimitBar { Chip = chip, Label = label, Used = u, ResetsAt = Json.Time(Json.Get(w, "resets_at")) };
            bar.Quota = QuotaObservation.Create("claude", PoolOf(key), key == "five_hour" ? "five_hour" : "seven_day", key == "five_hour" ? 18000 : 604800, u, bar.ResetsAt);
            if (!u.HasValue) bar.Note = bar.ResetsAt.HasValue ? "нет данных о расходе" : "окно не открыто";
            return bar;
        }

        // Ответ get_usage (response.response). false — нет rate_limits, snap не тронут.
        public static bool FillFromUsage(ServiceSnapshot snap, object data, DateTime now)
        {
            snap.ObservationTimeUnknown = true;   // get_usage не сообщает, свежий ли это ответ или кэш CLI
            if (data == null || Json.Bool(Json.Get(data, "rate_limits_available")) == false) return false;
            object rl = Json.Get(data, "rate_limits");
            var rlObj = Json.Obj(rl);
            if (rlObj == null) return false;

            var bars = new List<LimitBar>();
            void Add(LimitBar b) { if (b != null) bars.Add(b); }
            Add(WindowBar(rl, "five_hour", Chip5, "Сессия · окно 5 часов"));
            Add(WindowBar(rl, "seven_day", Chip7, "Неделя · все модели"));
            Add(WindowBar(rl, "seven_day_opus", Chip7, "Неделя · Opus"));
            Add(WindowBar(rl, "seven_day_sonnet", Chip7, "Неделя · Sonnet"));
            Add(WindowBar(rl, "seven_day_cowork", Chip7, "Неделя · Cowork"));
            Add(WindowBar(rl, "seven_day_oauth_apps", Chip7, "Неделя · сторонние приложения"));
            var ms = Json.Arr(Json.Get(rl, "model_scoped"));
            if (ms != null)
                foreach (var m in ms)
                {
                    string name = Json.Str(Json.Get(m, "display_name"));
                    if (string.IsNullOrEmpty(name)) continue;
                    string label = "Неделя · " + name;
                    if (bars.Any(x => x.Label == label)) continue;
                    double? u = ValidPercent(Json.Num(Json.Get(m, "utilization")));
                    var bar = new LimitBar { Chip = Chip7, Label = label, Used = u, ResetsAt = Json.Time(Json.Get(m, "resets_at")) };
                    string key = Json.Str(Json.Get(m, "key")) ?? Json.Str(Json.Get(m, "id"));
                    string pool = key != null && key.StartsWith("seven_day_", StringComparison.Ordinal) ? PoolOf(key) : null;
                    bar.Quota = QuotaObservation.Create("claude", pool, "seven_day", 604800, u, bar.ResetsAt);
                    bar.Tip = "Недельный лимит модели " + name + (pool == null ? ". Ответ не указывает подтверждённый идентификатор пула — окно показано только здесь." : ".");
                    if (!u.HasValue) bar.Note = "нет данных о расходе";
                    bars.Add(bar);
                }
            if (bars.Count == 0) return false;
            snap.Bars.AddRange(bars);

            foreach (var kv in rlObj)
            {
                string reason = Json.Str(Json.Get(kv.Value, "locked_reason"));
                if (reason != null) { snap.Attention = true; snap.Message = "Лимит заблокирован (" + reason + ")"; break; }
            }

            var eu = Json.Get(rl, "extra_usage");
            var spend = Json.Get(rl, "spend");
            if (Json.Bool(Json.Get(eu, "is_enabled")) == true || Json.Bool(Json.Get(spend, "enabled")) == true)
            {
                double dp = Math.Pow(10, (int)(Json.Num(Json.Get(eu, "decimal_places")) ?? 2));
                double used; string cur;
                double? sm = Json.Num(Json.Get(spend, "used.amount_minor"));
                if (sm.HasValue) { used = sm.Value / Math.Pow(10, (int)(Json.Num(Json.Get(spend, "used.exponent")) ?? 2)); cur = Json.Str(Json.Get(spend, "used.currency")); }
                else { used = (Json.Num(Json.Get(eu, "used_credits")) ?? 0) / dp; cur = Json.Str(Json.Get(eu, "currency")); }
                double limit = (Json.Num(Json.Get(eu, "monthly_limit")) ?? 0) / dp;
                double? lm = Json.Num(Json.Get(spend, "limit.amount_minor"));
                if (lm.HasValue) limit = lm.Value / Math.Pow(10, (int)(Json.Num(Json.Get(spend, "limit.exponent")) ?? 2));
                bool reached = Json.Bool(Json.Get(eu, "spend_limit_reached")) == true;
                double u5 = Json.Num(Json.Get(rl, "five_hour.utilization")) ?? 0, u7 = Json.Num(Json.Get(rl, "seven_day.utilization")) ?? 0;
                var e = new ExtraItem
                {
                    Label = "Usage credits",
                    Value = Money(used, cur) + (limit > 0 ? " из " + Money(limit, cur) : ""),
                    Fill = limit > 0 ? used / limit : (double?)null,
                    Detail = reached ? "лимит трат на месяц исчерпан" : limit > 0 ? "лимит трат на месяц, сброс 1-го числа" : "без месячного лимита",
                    Accent = reached ? "danger" : u5 >= 100 || u7 >= 100 ? "warn" : "info",
                    Tip = "Usage credits оплачивают работу Claude Code, когда исчерпаны лимиты тарифа."
                };
                snap.Extras.Add(e);
                if (e.Accent == "warn") { snap.Attention = true; snap.Message = "Лимит исчерпан — Claude Code работает за счёт Usage credits."; }
            }

            var rows = Json.Arr(Json.Get(rl, "seven_day_breakdown.rows"));
            if (rows != null)
            {
                var parts = new List<ExtraPart>();
                foreach (var row in rows)
                {
                    string key = Json.Str(Json.Get(row, "key"));
                    string name = key == "claude_code" ? "Claude Code" : key == "chat" ? "Чаты" : key == "cowork" ? "Cowork" : key == "other" ? "Прочее"
                        : Json.Str(Json.Get(row, "display_name")) ?? key;
                    parts.Add(new ExtraPart { Key = key, Name = name, Percent = Json.Num(Json.Get(row, "percent")) ?? 0 });
                }
                if (parts.Any(x => x.Percent > 0))
                    snap.Extras.Add(new ExtraItem { Label = "Неделя по продуктам", Accent = "neutral", Parts = parts, ExpiresAt = Json.Time(Json.Get(rl, "seven_day.resets_at")) });
            }
            snap.Source = "claude get_usage (stream-json) — лимиты тарифа, кредиты, расход по продуктам";
            return true;
        }

        // Текстовый запасной путь: вывод `claude -p "/usage"`.
        public static List<LimitBar> ParseUsageText(string text, DateTime now)
        {
            var list = new List<LimitBar>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (Match m in UsageLine.Matches(text))
            {
                double.TryParse(m.Groups["pct"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct);
                var bar = new LimitBar { Used = pct, ResetsAt = ParseReset(m.Groups["reset"].Value, now) };
                bool session = m.Groups["what"].Value.Equals("session", StringComparison.OrdinalIgnoreCase);
                string scope = m.Groups["scope"].Success ? m.Groups["scope"].Value.Trim() : "all models";
                if (session)
                {
                    bar.Chip = Chip5; bar.Label = "Сессия · окно 5 часов";
                    if (pct <= 0 && !bar.ResetsAt.HasValue) bar.Note = "окно не открыто";
                }
                else
                {
                    bar.Chip = Chip7;
                    bar.Label = scope.Equals("all models", StringComparison.OrdinalIgnoreCase) ? "Неделя · все модели" : "Неделя · " + scope;
                }
                string ts = m.Groups["scope"].Value.Trim().ToLowerInvariant();
                string pool = session || ts == "all models" || ts == "" ? "subscription.general" : ts == "opus" ? "subscription.opus" : ts == "sonnet" ? "subscription.sonnet" : null;
                bar.Quota = QuotaObservation.Create("claude", pool, session ? "five_hour" : "seven_day", session ? 18000 : 604800, bar.Used, bar.ResetsAt);
                bar.Tip = m.Value.Trim();
                list.Add(bar);
            }
            return list;
        }

        // "Sep 24, 4:19pm (Europe/Kyiv)" | "4:19pm" -> местное время
        public static DateTime? ParseReset(string raw, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = TrailingZone.Replace(raw, "").Replace(" at ", " ").Trim();
            s = AmPm.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
            now = Fmt.Local(now);
            if (DateTime.TryParseExact(s, DateFormats, En, DateTimeStyles.AllowWhiteSpaces, out DateTime dt))
            {
                if (dt < now.AddDays(-180)) dt = dt.AddYears(1);
                return DateTime.SpecifyKind(dt, DateTimeKind.Local);
            }
            if (DateTime.TryParseExact(s, TimeFormats, En, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out dt))
            {
                DateTime t = now.Date + dt.TimeOfDay;
                if (t < now.AddMinutes(-1)) t = t.AddDays(1);
                return DateTime.SpecifyKind(t, DateTimeKind.Local);
            }
            return null;
        }

        // Запасной источник без входа CLI: plan-usage-history.json приложения Claude ({"samples":[{"t":мс,"u":{"fh":%, "sd":%}}]}).
        public static bool FillFromDesktopHistory(ServiceSnapshot snap, string text, DateTime now)
        {
            var samples = Json.Arr(Json.Get(Json.TryParse(text), "samples"));
            if (samples == null || samples.Count == 0) return false;
            object last = samples[samples.Count - 1];
            DateTime? t = Json.Time(Json.Get(last, "t"));
            if (!t.HasValue || (now - t.Value).TotalMinutes > 60) return false;
            double? fh = Json.Num(Json.Get(last, "u.fh")), sd = Json.Num(Json.Get(last, "u.sd"));
            if (!fh.HasValue && !sd.HasValue) return false;
            if (fh.HasValue) snap.Bars.Add(new LimitBar { Chip = Chip5, Label = "Сессия · окно 5 часов", Used = fh.Value });
            if (sd.HasValue) snap.Bars.Add(new LimitBar { Chip = Chip7, Label = "Неделя · все модели", Used = sd.Value });
            snap.DataTime = Fmt.Utc(t.Value);
            snap.ObservationTimeUnknown = false;
            for (int i = 0; i < snap.Bars.Count; i++)
            {
                var bar = snap.Bars[i];
                bool five = fh.HasValue && i == 0;
                bar.Quota = QuotaObservation.Create("claude", "subscription.general", five ? "five_hour" : "seven_day", five ? 18000 : 604800, bar.Used, null);
                if (bar.Quota != null) bar.Quota.MeasuredAt = t.Value.ToUniversalTime();
            }
            snap.Source = "приложение Claude (plan-usage-history.json) — запасной источник, пока у CLI нет входа";
            return true;
        }

        // `claude auth status --json`: берётся только subscriptionType, остальное (email, организация) отбрасывается сразу.
        public static (bool Answered, string Plan) ParseAuth(string stdout)
        {
            var root = Json.Obj(Json.TryParse(stdout?.Trim()));
            if (root == null) return (false, null);
            if (Json.Bool(Json.Get(root, "loggedIn")) == false) return (true, null);
            return (true, PlanName(Json.Get(root, "subscriptionType") as string));
        }

        public static bool? LoggedIn(string stdout) => Json.Bool(Json.Get(Json.TryParse(stdout), "loggedIn"));

        public static string PlanName(string raw)
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
            if (!PlanToken.IsMatch(v)) return null;
            return string.Join(" ", v.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        public static string Money(double amount, string currency)
        {
            string cur = string.IsNullOrEmpty(currency) ? "USD" : currency.ToUpperInvariant();
            bool whole = Math.Abs(amount - Math.Round(amount)) < 0.005;
            var nf = new NumberFormatInfo { NumberGroupSeparator = Fmt.Nbsp, NumberGroupSizes = new[] { 3 }, NumberDecimalSeparator = "." };
            string num = amount.ToString(whole ? "N0" : "N2", nf);
            switch (cur)
            {
                case "USD": return "$" + num;
                case "EUR": return "€" + num;
                case "GBP": return "£" + num;
                default: return num + " " + cur;
            }
        }
    }
}
