using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LimitsWidget
{
    // claude -p "/usage": команда поддерживает неинтерактивный режим и печатает лимиты тарифа, например
    //   Current session: 2% used · resets Sep 24, 4:19pm (Europe/Kyiv)
    //   Current week (all models): 29% used · resets Sep 28, 7:59pm (Europe/Kyiv)
    //   Current week (Fable): 42% used · resets Sep 28, 7:59pm (Europe/Kyiv)
    sealed class ClaudeProvider : IProvider
    {
        static readonly Regex UsageLine = new Regex(
            @"^\s*Current\s+(?<what>session|week)(?:\s*\((?<scope>[^)]+)\))?\s*:\s*(?<pct>\d+(?:[.,]\d+)?)\s*%\s*used(?:\s*\S\s*resets\s+(?<reset>.+?))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        static readonly Regex TrailingZone = new Regex(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);
        static readonly Regex AmPm = new Regex(@"(\d)\s*(am|pm)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        static readonly string[] DateFormats = { "MMM d, h:mmtt", "MMM d, htt", "MMM d h:mmtt", "MMM d htt", "MMM d, yyyy, h:mmtt", "MMM d, yyyy h:mmtt", "MMM d" };
        static readonly string[] TimeFormats = { "h:mmtt", "htt", "H:mm" };

        public string Id { get { return "claude"; } }

        public ServiceSnapshot Collect(ProviderContext ctx)
        {
            var snap = ProviderUtil.NewSnap(ctx, Id);
            snap.Source = "claude -p \"/usage\" — лимиты тарифа Claude (общие для Claude Code и claude.ai)";

            // Основной путь — служебный запрос get_usage (лимиты, кредиты, расход по продуктам); фикстура — объект response.response.
            string guFix = ProviderUtil.Fixture(ctx, "claude-get-usage.json");
            if (guFix != null)
            {
                if (FillFromUsage(snap, Json.TryParse(guFix), ctx))
                {
                    ProviderUtil.MarkFresh(snap, ctx, true);
                    return snap;
                }
                Log.Write("claude get_usage (фикстура): нет rate_limits");
            }

            string text = ProviderUtil.Fixture(ctx, "claude-usage.txt");
            bool fixture = text != null;
            string cliExe = null;
            if (!fixture)
            {
                string exe = cliExe = Cli.Resolve(ctx.Svc.Command, "claude");
                if (exe == null)
                    return ProviderUtil.Fail(snap, SvcStatus.NoData, "нет CLI", "claude не найден в PATH — укажите services.claude.command в config.json", Placeholders());
                if (guFix == null)
                {
                    string guError;
                    object data = ReadUsage(exe, ctx.Svc.TimeoutSeconds * 1000, ctx.Svc.MaxProcesses, out guError);
                    if (data != null && FillFromUsage(snap, data, ctx))
                    {
                        ProviderUtil.MarkFresh(snap, ctx, false);
                        return snap;
                    }
                    Log.Write("claude get_usage: " + (guError ?? "rate_limits недоступны"));
                    snap.Bars.Clear();
                    snap.Extras.Clear();
                    snap.Attention = false;
                    snap.Message = null;
                }
                var r = Cli.Run(exe, "-p \"/usage\" --no-session-persistence --strict-mcp-config", ctx.Svc.TimeoutSeconds * 1000, ctx.Svc.MaxProcesses);
                if (!r.Ok)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "claude /usage: " + r.Describe(), Placeholders());
                text = r.StdOut;
            }

            foreach (Match m in UsageLine.Matches(text))
            {
                var bar = new LimitBar();
                double pct;
                double.TryParse(m.Groups["pct"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out pct);
                bar.Used = pct;
                bar.ResetsAt = ParseReset(m.Groups["reset"].Value, ctx.Now);
                if (m.Groups["what"].Value.Equals("session", StringComparison.OrdinalIgnoreCase))
                {
                    bar.Chip = "5" + Fmt.Nbsp + "ч";
                    bar.Label = "Сессия · окно 5 часов";
                    if (pct <= 0 && !bar.ResetsAt.HasValue) bar.Note = "окно не открыто";
                }
                else
                {
                    string scope = m.Groups["scope"].Success ? m.Groups["scope"].Value.Trim() : "all models";
                    bar.Chip = "7" + Fmt.Nbsp + "д";
                    bar.Label = scope.Equals("all models", StringComparison.OrdinalIgnoreCase) ? "Неделя · все модели" : "Неделя · " + scope;
                }
                bar.Tip = m.Value.Trim();
                snap.Bars.Add(bar);
            }

            if (snap.Bars.Count == 0)
            {
                // Без входа /usage печатает только «Total cost…». Тогда проценты берутся из данных приложения Claude,
                // а в карточке появляется кнопка входа — пользователю не нужен терминал.
                if (!fixture && cliExe != null && !LoggedIn(cliExe, ctx.Svc.TimeoutSeconds * 1000))
                {
                    snap.ActionId = LoginAction;
                    snap.ActionLabel = "Войти в Claude Code";
                    if (FillFromDesktopApp(snap, ctx.Now))
                    {
                        snap.Status = SvcStatus.Ok;
                        snap.StatusText = Fmt.Clock(snap.DataTime.Value);
                        snap.Attention = true;
                        snap.Message = "Вход Claude Code истёк. Пока показаны данные приложения Claude — без лимитов по моделям и времени сброса.";
                        return snap;
                    }
                    var failed = ProviderUtil.Fail(snap, SvcStatus.Error, "нужен вход", "Вход Claude Code истёк — нажмите «Войти».", Placeholders());
                    failed.Attention = true;
                    return failed;
                }
                string hint = Cli.FirstLine(text);
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка",
                    "claude /usage не вернул лимитов" + (hint != null ? ": " + hint : ""), Placeholders());
            }
            ProviderUtil.MarkFresh(snap, ctx, fixture);
            return snap;
        }

        public const string LoginAction = "claude-login";

        // Служебный запрос CLI get_usage через stream-json: только чтение, в модель не уходит (так его делает Claude Agent SDK).
        // Возвращает response.response или null (error — причина).
        static object ReadUsage(string exe, int timeoutMs, int maxProcesses, out string error)
        {
            error = null;
            Process p;
            IntPtr job = Cli.Job.Create(maxProcesses);
            try
            {
                var psi = Cli.StartInfo(exe, "-p --input-format stream-json --output-format stream-json --verbose --no-session-persistence --strict-mcp-config");
                // Внутри сессии Claude CLI ведёт себя иначе — окружение CLAUDE*/ANTHROPIC* не передаём.
                var drop = new List<string>();
                foreach (string k in psi.EnvironmentVariables.Keys)
                    if (k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase))
                        drop.Add(k);
                foreach (string k in drop) psi.EnvironmentVariables.Remove(k);
                p = Process.Start(psi);
                Cli.Job.Assign(job, p);
            }
            catch (Exception ex)
            {
                Cli.Job.Close(job);
                error = "не запустился: " + ex.Message;
                return null;
            }
            var lines = new BlockingCollection<string>();
            var reader = new Thread(() =>
            {
                try
                {
                    string l;
                    while ((l = p.StandardOutput.ReadLine()) != null) lines.Add(l);
                }
                catch
                {
                }
                finally
                {
                    lines.CompleteAdding();
                }
            });
            reader.IsBackground = true;
            reader.Start();
            var stderr = p.StandardError.ReadToEndAsync();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            try
            {
                p.StandardInput.WriteLine("{\"type\":\"control_request\",\"request_id\":\"lw-usage\",\"request\":{\"subtype\":\"get_usage\",\"skip_behaviors\":true}}");
                p.StandardInput.Flush();
                while (true)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) { error = "нет ответа на get_usage"; return null; }
                    string line;
                    if (!lines.TryTake(out line, left))
                    {
                        error = lines.IsCompleted ? "CLI завершился без ответа" : "нет ответа на get_usage";
                        return null;
                    }
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
                    return null;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                try { p.StandardInput.Close(); }
                catch { }
                try { if (!p.WaitForExit(3000)) Cli.KillTree(p); }
                catch { }
                if (error != null)
                {
                    string hint = null;
                    try { if (stderr.Wait(500)) hint = Cli.FirstLine(stderr.Result); }
                    catch { }
                    if (hint != null) error += " (" + hint + ")";
                }
                p.Dispose();
                Cli.Job.Close(job);
            }
        }

        static bool IsLimitKey(string k)
        {
            return k == "five_hour" || k.StartsWith("seven_day", StringComparison.Ordinal);
        }

        static double? Util(object rl, string key)
        {
            return Json.Num(Json.Get(rl, key + ".utilization"));
        }

        static LimitBar WindowBar(object rl, string key, string chip, string label, DateTime now)
        {
            var w = Json.Get(rl, key);
            if (Json.Obj(w) == null) return null;
            var bar = new LimitBar { Chip = chip, Label = label };
            double? u = Util(rl, key);
            bar.Used = u ?? 0;
            bar.ResetsAt = Json.Time(Json.Get(w, "resets_at"));
            if (!u.HasValue) bar.Note = "окно не открыто";
            return bar;
        }

        // Разбор ответа get_usage; false — нет rate_limits (тогда snap не тронут).
        static bool FillFromUsage(ServiceSnapshot snap, object data, ProviderContext ctx)
        {
            if (data == null || Json.Bool(Json.Get(data, "rate_limits_available")) == false) return false;
            object rl = Json.Get(data, "rate_limits");
            var rlObj = Json.Obj(rl);
            if (rlObj == null) return false;

            string chip5 = "5" + Fmt.Nbsp + "ч", chip7 = "7" + Fmt.Nbsp + "д";
            var bars = new List<LimitBar>();
            var b5 = WindowBar(rl, "five_hour", chip5, "Сессия · окно 5 часов", ctx.Now);
            if (b5 != null) bars.Add(b5);
            var b7 = WindowBar(rl, "seven_day", chip7, "Неделя · все модели", ctx.Now);
            if (b7 != null) bars.Add(b7);
            string[][] scoped =
            {
                new[] { "seven_day_opus", "Неделя · Opus" }, new[] { "seven_day_sonnet", "Неделя · Sonnet" },
                new[] { "seven_day_cowork", "Неделя · Cowork" }, new[] { "seven_day_oauth_apps", "Неделя · сторонние приложения" }
            };
            foreach (var s in scoped)
            {
                var b = WindowBar(rl, s[0], chip7, s[1], ctx.Now);
                if (b != null) bars.Add(b);
            }
            var ms = Json.Arr(Json.Get(rl, "model_scoped"));
            if (ms != null)
                foreach (var m in ms)
                {
                    string name = Json.Str(Json.Get(m, "display_name"));
                    if (string.IsNullOrEmpty(name)) continue;
                    string label = "Неделя · " + name;
                    if (bars.Any(x => x.Label == label)) continue;
                    bars.Add(new LimitBar
                    {
                        Chip = chip7, Label = label,
                        Used = Json.Num(Json.Get(m, "utilization")) ?? 0,
                        ResetsAt = Json.Time(Json.Get(m, "resets_at"))
                    });
                }
            if (bars.Count == 0) return false;
            snap.Bars.AddRange(bars);

            // Заблокированное окно
            foreach (var kv in rlObj)
            {
                string reason = Json.Str(Json.Get(kv.Value, "locked_reason"));
                if (reason != null)
                {
                    snap.Attention = true;
                    snap.Message = "Лимит заблокирован (" + reason + ")";
                    break;
                }
            }

            // a) кредиты в долларах
            foreach (var kv in rlObj)
            {
                if (IsLimitKey(kv.Key) || Json.Obj(kv.Value) == null) continue;
                double? limit = Json.Num(Json.Get(kv.Value, "limit_dollars"));
                if (!limit.HasValue || limit.Value <= 0) continue;
                double used = Json.Num(Json.Get(kv.Value, "used_dollars")) ?? 0;
                double remaining = Json.Num(Json.Get(kv.Value, "remaining_dollars")) ?? (limit.Value - used);
                // показываем только действующий кредит: есть остаток, окно не закрыто, срок не вышел, блокировки нет
                double? util = Json.Num(Json.Get(kv.Value, "utilization"));
                DateTime? creditEnds = Json.Time(Json.Get(kv.Value, "resets_at"));
                if (remaining <= 0.005 || (util.HasValue && util.Value >= 100)
                    || (creditEnds.HasValue && creditEnds.Value <= ctx.Now)
                    || !string.IsNullOrEmpty(Json.Str(Json.Get(kv.Value, "locked_reason")))) continue;
                var e = new ExtraItem();
                e.ExpiresAt = creditEnds;
                switch (kv.Key)
                {
                    case "iguana_necktie":
                        e.Label = "Кредит облачных сессий";
                        e.Tip = "Включённый кредит для облачных сессий Claude Code. Пока он есть, облачные сессии не расходуют лимиты тарифа; после него действуют обычные лимиты.";
                        break;
                    case "cinder_cove":
                        e.Label = "Разовый кредит Claude Code и Cowork";
                        e.Tip = "Разовый кредит на Claude Code и Cowork.";
                        break;
                    case "omelette_promotional":
                        e.Label = "Промо-кредит";
                        e.Tip = "Дополнительный кредит тарифа (ключ ответа: " + kv.Key + ").";
                        break;
                    default:
                        e.Label = "Дополнительный кредит";
                        e.Tip = "Дополнительный кредит тарифа (ключ ответа: " + kv.Key + ").";
                        break;
                }
                e.Icon = kv.Key == "iguana_necktie" ? "\uE753" : "\uE8C7";
                e.Value = CreditStats.Money(remaining) + " из " + CreditStats.Money(limit.Value);
                e.Fill = used / limit.Value;
                DateTime? ends = Json.Time(Json.Get(kv.Value, "resets_at"));
                e.Detail = "остаток" + (ends.HasValue ? " · действует до " + Fmt.When(ends.Value, ctx.Now) : "");
                e.Accent = remaining < limit.Value * 0.1 ? "warn" : "info";
                snap.Extras.Add(e);
            }

            // b) Usage credits
            var eu = Json.Get(rl, "extra_usage");
            var spend = Json.Get(rl, "spend");
            if (Json.Bool(Json.Get(eu, "is_enabled")) == true || Json.Bool(Json.Get(spend, "enabled")) == true)
            {
                double dp = Math.Pow(10, (int)(Json.Num(Json.Get(eu, "decimal_places")) ?? 2));
                double used;
                string cur;
                double? sm = Json.Num(Json.Get(spend, "used.amount_minor"));
                if (sm.HasValue)
                {
                    used = sm.Value / Math.Pow(10, (int)(Json.Num(Json.Get(spend, "used.exponent")) ?? 2));
                    cur = Json.Str(Json.Get(spend, "used.currency"));
                }
                else
                {
                    used = (Json.Num(Json.Get(eu, "used_credits")) ?? 0) / dp;
                    cur = Json.Str(Json.Get(eu, "currency"));
                }
                double limit = (Json.Num(Json.Get(eu, "monthly_limit")) ?? 0) / dp;
                double? lm = Json.Num(Json.Get(spend, "limit.amount_minor"));
                if (lm.HasValue)
                    limit = lm.Value / Math.Pow(10, (int)(Json.Num(Json.Get(spend, "limit.exponent")) ?? 2));
                bool reached = Json.Bool(Json.Get(eu, "spend_limit_reached")) == true;
                double u5 = Util(rl, "five_hour") ?? 0, u7 = Util(rl, "seven_day") ?? 0;
                var e = new ExtraItem();
                e.Icon = "\uE8C7";
                e.Label = "Usage credits";
                e.Value = CreditStats.Money(used, cur) + (limit > 0 ? " из " + CreditStats.Money(limit, cur) : "");
                e.Fill = limit > 0 ? used / limit : (double?)null;
                e.Detail = reached ? "лимит трат на месяц исчерпан" : (limit > 0 ? "лимит трат на месяц, сброс 1-го числа" : "без месячного лимита");
                e.Accent = reached ? "danger" : (u5 >= 100 || u7 >= 100 ? "warn" : "info");
                e.Tip = "Usage credits оплачивают работу Claude Code, когда исчерпаны лимиты тарифа.";
                snap.Extras.Add(e);
                if (e.Accent == "warn")
                {
                    snap.Attention = true;
                    snap.Message = "Лимит исчерпан — Claude Code работает за счёт Usage credits.";
                }
            }

            // c) расход по продуктам
            var rows = Json.Arr(Json.Get(rl, "seven_day_breakdown.rows"));
            if (rows != null)
            {
                var parts = new List<ExtraPart>();
                foreach (var row in rows)
                {
                    string key = Json.Str(Json.Get(row, "key"));
                    string name;
                    switch (key)
                    {
                        case "claude_code": name = "Claude Code"; break;
                        case "chat": name = "Чаты"; break;
                        case "cowork": name = "Cowork"; break;
                        case "other": name = "Прочее"; break;
                        default: name = Json.Str(Json.Get(row, "display_name")) ?? key; break;
                    }
                    parts.Add(new ExtraPart { Key = key, Name = name, Percent = Json.Num(Json.Get(row, "percent")) ?? 0 });
                }
                if (parts.Any(x => x.Percent > 0))
                    snap.Extras.Add(new ExtraItem
                    {
                        Label = "Неделя по продуктам",
                        Accent = "neutral",
                        Parts = parts,
                        ExpiresAt = Json.Time(Json.Get(rl, "seven_day.resets_at")),
                        Tip = "Доли расхода недельного лимита по продуктам Claude."
                    });
            }

            // d) бесплатные сбросы (структура известна лишь из схемы CLI — разбор защитный)
            double resets = 0;
            DateTime? until = null;
            var grants = Json.Arr(Json.Get(rl, "cedar_ember.grants"));
            if (grants != null)
                foreach (var g in grants)
                {
                    double left = Json.Num(Json.Get(g, "resets_left")) ?? 0;
                    if (left <= 0) continue;
                    resets += left;
                    DateTime? ge = Json.Time(Json.Get(g, "ends_at"));
                    if (ge.HasValue && (!until.HasValue || ge.Value < until.Value)) until = ge;
                }
            double avail = Json.Num(Json.Get(rl, "juniper_tide.available")) ?? 0;
            if (avail > 0) resets += avail;
            if (resets > 0)
                snap.Extras.Add(new ExtraItem
                {
                    Icon = "\uE72C",
                    Label = "Бесплатный сброс лимита",
                    Value = Math.Round(resets).ToString("0", CultureInfo.InvariantCulture),
                    Detail = until.HasValue ? "до " + Fmt.When(until.Value, ctx.Now) : null,
                    Accent = "ok",
                    ExpiresAt = until,
                    Tip = "Бесплатный сброс лимита Claude: применяется в Claude Code (/usage)."
                });

            snap.Source = "claude get_usage (stream-json) — лимиты тарифа, кредиты, расход по продуктам";
            return true;
        }

        // Запасной источник без входа в CLI: приложение Claude раз в ~15 минут пишет plan-usage-history.json
        // ({"samples":[{"t":<мс>,"org":…,"u":{"fh":<окно 5 ч, %>,"sd":<неделя, %>}}]}). Токенов там нет.
        static bool FillFromDesktopApp(ServiceSnapshot snap, DateTime now)
        {
            try
            {
                string path = FindDesktopHistory();
                if (path == null) return false;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();
                var samples = Json.Arr(Json.Get(Json.TryParse(text), "samples"));
                if (samples == null || samples.Count == 0) return false;
                object last = samples[samples.Count - 1];
                DateTime? t = Json.Time(Json.Get(last, "t"));
                if (!t.HasValue || (now - t.Value).TotalMinutes > 60) return false;   // приложение закрыто — данные старые

                double? fh = Json.Num(Json.Get(last, "u.fh")), sd = Json.Num(Json.Get(last, "u.sd"));
                if (!fh.HasValue && !sd.HasValue) return false;
                if (fh.HasValue)
                    snap.Bars.Add(new LimitBar { Chip = "5" + Fmt.Nbsp + "ч", Label = "Сессия · окно 5 часов", Used = fh.Value });
                if (sd.HasValue)
                    snap.Bars.Add(new LimitBar { Chip = "7" + Fmt.Nbsp + "д", Label = "Неделя · все модели", Used = sd.Value });
                snap.DataTime = t;
                snap.Source = "приложение Claude (plan-usage-history.json) — запасной источник, пока у CLI нет входа";
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("claude desktop history: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        static string FindDesktopHistory()
        {
            var candidates = new List<string>();
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (Directory.Exists(packages))
                foreach (var d in Directory.GetDirectories(packages, "Claude_*"))
                    candidates.Add(Path.Combine(d, @"LocalCache\Roaming\Claude\plan-usage-history.json"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Claude\plan-usage-history.json"));
            return candidates.Where(File.Exists).OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
        }

        // `claude auth status` печатает JSON с полем loggedIn (без токенов). Неясный ответ считаем «вошёл».
        static bool LoggedIn(string exe, int timeoutMs)
        {
            var r = Cli.Run(exe, "auth status", timeoutMs, 0);
            return Json.Bool(Json.Get(Json.TryParse(r.StdOut), "loggedIn")) != false;
        }

        static LimitBar[] Placeholders()
        {
            return new[]
            {
                ProviderUtil.Placeholder("5" + Fmt.Nbsp + "ч", "Сессия · окно 5 часов"),
                ProviderUtil.Placeholder("7" + Fmt.Nbsp + "д", "Неделя · все модели")
            };
        }

        // "Sep 24, 4:19pm (Europe/Kyiv)" | "4:19pm" -> локальное время
        public static DateTime? ParseReset(string raw, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = TrailingZone.Replace(raw, "").Replace(" at ", " ").Trim();
            s = AmPm.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
            DateTime dt;
            if (DateTime.TryParseExact(s, DateFormats, En, DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                if (dt < now.AddDays(-180)) dt = dt.AddYears(1);
                return dt;
            }
            if (DateTime.TryParseExact(s, TimeFormats, En, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out dt))
            {
                DateTime t = now.Date + dt.TimeOfDay;
                if (t < now.AddMinutes(-1)) t = t.AddDays(1);
                return t;
            }
            return null;
        }
    }
}
