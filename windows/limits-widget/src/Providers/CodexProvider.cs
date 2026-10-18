using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace LimitsWidget
{
    // codex app-server (JSON-RPC по stdio): initialize -> initialized -> account/rateLimits/read.
    // Лимиты приходят с сервера OpenAI через сам Codex CLI, авторизацию делает он.
    sealed class CodexProvider : IProvider
    {
        const string NpmVendorExe = @"node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";

        public string Id { get { return "codex"; } }

        public ServiceSnapshot Collect(ProviderContext ctx)
        {
            var snap = ProviderUtil.NewSnap(ctx, Id);
            snap.Source = "codex app-server → account/rateLimits/read";
            string text = ProviderUtil.Fixture(ctx, "codex-ratelimits.json");
            bool fixture = text != null;
            object result;
            if (fixture)
            {
                result = Json.Get(Json.TryParse(text), "result");
            }
            else
            {
                string exe = ResolveCodex(ctx.Svc);
                if (exe == null)
                    return ProviderUtil.Fail(snap, SvcStatus.NoData, "нет CLI", "codex не найден в PATH", Placeholder());
                string error;
                result = ReadRateLimits(exe, ctx.Svc.TimeoutSeconds * 1000, ctx.Svc.MaxProcesses, out error);
                if (result == null)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "codex app-server: " + error, Placeholder());
            }

            var limits = new List<IDictionary<string, object>>();
            var byId = Json.Obj(Json.Get(result, "rateLimitsByLimitId"));
            if (byId != null)
            {
                object main;
                if (byId.TryGetValue("codex", out main) && Json.Obj(main) != null) limits.Add(Json.Obj(main));
                foreach (var kv in byId.OrderBy(k => k.Key, StringComparer.Ordinal))
                    if (kv.Key != "codex" && Json.Obj(kv.Value) != null) limits.Add(Json.Obj(kv.Value));
            }
            else if (Json.Obj(Json.Get(result, "rateLimits")) != null)
            {
                limits.Add(Json.Obj(Json.Get(result, "rateLimits")));
            }

            foreach (var l in limits)
            {
                string id = Json.Str(Json.Get(l, "limitId")) ?? "codex";
                string name = id == "codex" ? null : (Json.Str(Json.Get(l, "limitName")) ?? id);
                string model = Json.Str(Json.Get(l, "normalModelSlug"));
                // Дополнительные пулы (например, gpt-reserve) кабинет OpenAI не показывает — выводим их, только когда они расходуются.
                if (name != null && !InUse(l)) continue;
                AddWindow(snap, Json.Get(l, "primary"), name, model, ctx.Now);
                AddWindow(snap, Json.Get(l, "secondary"), name, model, ctx.Now);
            }

            var head = limits.FirstOrDefault();
            if (head != null)
            {
                string plan = Json.Str(Json.Get(head, "planType"));
                if (snap.Plan == null && !string.IsNullOrEmpty(plan)) snap.Plan = PlanName(plan);
            }
            DescribeCreditsAndResets(snap, result, head, ctx, fixture);

            if (snap.Bars.Count == 0)
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "codex не вернул окон лимитов", Placeholder());
            ProviderUtil.MarkFresh(snap, ctx, fixture);
            return snap;
        }

        static void AddWindow(ServiceSnapshot snap, object w, string name, string model, DateTime now)
        {
            if (Json.Obj(w) == null) return;
            double? mins = Json.Num(Json.Get(w, "windowDurationMins"));
            var bar = new LimitBar();
            bar.Chip = ProviderUtil.WindowChip(mins);
            if (name == null)
            {
                bar.Label = mins.HasValue && Math.Abs(mins.Value - 10080) < 1 ? "Неделя · Codex и «Работа»"
                    : mins.HasValue && Math.Abs(mins.Value - 300) < 1 ? "Окно 5 часов" : "Лимит · " + ProviderUtil.WindowWord(mins);
                // так этот лимит описывает кабинет OpenAI (Настройки → Использование и оплата)
                bar.Tip = "Общий лимит тарифа для Codex, режима «Работа», агентов рабочей области и ChatGPT для Excel. Диалоги в режиме «Чат» не учитываются.";
            }
            else
                bar.Label = name + " · " + ProviderUtil.WindowWord(mins);
            bar.Used = Json.Num(Json.Get(w, "usedPercent"));
            bar.ResetsAt = Json.Time(Json.Get(w, "resetsAt"));
            if (!string.IsNullOrEmpty(model)) bar.Tip = (bar.Tip != null ? bar.Tip + "\n" : "") + "Модель: " + model;
            ProviderUtil.MarkIdle(bar, now, mins);
            snap.Bars.Add(bar);
        }

        static LimitBar Placeholder()
        {
            return ProviderUtil.Placeholder("7" + Fmt.Nbsp + "д", "Неделя · Codex и «Работа»");
        }

        static bool InUse(IDictionary<string, object> limit)
        {
            if (!string.IsNullOrEmpty(Json.Str(Json.Get(limit, "rateLimitReachedType")))) return true;
            foreach (string key in new[] { "primary", "secondary" })
            {
                double? used = Json.Num(Json.Get(limit, key + ".usedPercent"));
                if (used.HasValue && used.Value > 0) return true;
            }
            return false;
        }

        // Как в кабинете: после исчерпания недельного лимита Codex продолжает работать за счёт кредитов;
        // плюс бесплатные сбросы лимита и индивидуальный лимит трат. Всё это — плашки под полосами (snap.Extras).
        static void DescribeCreditsAndResets(ServiceSnapshot snap, object result, IDictionary<string, object> head, ProviderContext ctx, bool fixture)
        {
            var credits = head != null ? Json.Get(head, "credits") : null;
            bool unlimited = Json.Bool(Json.Get(credits, "unlimited")) == true;
            double? balance = Json.Bool(Json.Get(credits, "hasCredits")) == true ? Json.Num(Json.Get(credits, "balance")) : null;
            bool hasCreditsRow = unlimited || balance.HasValue;

            int resets = 0;
            DateTime? resetExpires = null;
            string resetTitle = null;
            var list = Json.Arr(Json.Get(result, "rateLimitResetCredits.credits"));
            if (list != null)
                foreach (var c in list)
                {
                    string status = Json.Str(Json.Get(c, "status"));
                    if (status != null && status != "available") continue;
                    resets++;
                    DateTime? exp = Json.Time(Json.Get(c, "expiresAt"));
                    if (exp.HasValue && (!resetExpires.HasValue || exp.Value < resetExpires.Value)) resetExpires = exp;
                    string title = Json.Str(Json.Get(c, "title"));
                    if (resetTitle == null && !string.IsNullOrEmpty(title)) resetTitle = title;
                }
            double? availCount = Json.Num(Json.Get(result, "rateLimitResetCredits.availableCount"));
            if (availCount.HasValue && availCount.Value > resets) resets = (int)availCount.Value;

            string reached = head != null ? Json.Str(Json.Get(head, "rateLimitReachedType")) : null;
            bool exhausted = Json.Bool(Json.Get(result, "ordinaryUsageAllowed")) == false || !string.IsNullOrEmpty(reached);
            if (hasCreditsRow)
            {
                // в режиме фикстур история не пишется, но читается — так тест показывает расход
                if (!fixture && !unlimited && ctx.State != null) ctx.State.AddCreditSample("codex", ctx.Now, balance.Value);
                var samples = ctx.State != null ? ctx.State.CreditSamples("codex") : new List<double[]>();
                var cr = CreditStats.Describe(balance ?? 0, unlimited, exhausted, samples, ctx.Now);
                // плашка — только пока кредиты есть; обнулённый остаток (кончилась акция) не держим
                if (unlimited || (balance.HasValue && balance.Value >= 1)) snap.Extras.Add(cr);
            }

            if (resets > 0)
            {
                var r = new ExtraItem();
                r.Icon = "\uE72C";
                r.Label = "Бесплатный сброс лимита";
                r.Value = resets.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string detail = resetExpires.HasValue ? "до " + resetExpires.Value.ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture) : null;
                if (!string.IsNullOrEmpty(resetTitle)) detail = detail != null ? detail + " · " + resetTitle : resetTitle;
                r.Detail = detail;
                r.Accent = "ok";
                r.ExpiresAt = resetExpires;
                r.Tip = "Бесплатный сброс недельного лимита Codex: применяется в приложении Codex.";
                snap.Extras.Add(r);
            }

            // индивидуальный лимит трат, заданный администратором рабочей области
            var il = head != null ? Json.Get(head, "individualLimit") : null;
            if (Json.Obj(il) != null)
            {
                double? used = Json.Num(Json.Get(il, "used"));
                double? limit = Json.Num(Json.Get(il, "limit"));
                if (used.HasValue && limit.HasValue)
                {
                    var x = new ExtraItem();
                    x.Icon = "\uE8C7";
                    x.Label = "Лимит трат";
                    x.Value = CreditStats.Number(used.Value) + " из " + CreditStats.Number(limit.Value);
                    x.Fill = limit.Value > 0 ? (double?)(used.Value / limit.Value) : null;
                    DateTime? at = Json.Time(Json.Get(il, "resetsAt"));
                    x.Detail = at.HasValue ? "сброс " + Fmt.When(at.Value, ctx.Now) : null;
                    x.Accent = Json.Bool(Json.Get(head, "spendControlReached")) == true ? "danger" : "info";
                    x.Tip = "Индивидуальный лимит трат, заданный администратором рабочей области.";
                    snap.Extras.Add(x);
                }
            }

            if (exhausted)
            {
                snap.Attention = true;
                switch (reached)
                {
                    case "workspace_owner_credits_depleted": snap.Message = "Кредиты рабочей области закончились."; break;
                    case "workspace_member_credits_depleted": snap.Message = "Ваши кредиты в рабочей области закончились."; break;
                    case "workspace_owner_usage_limit_reached": snap.Message = "Достигнут лимит использования рабочей области."; break;
                    case "workspace_member_usage_limit_reached": snap.Message = "Достигнут ваш лимит в рабочей области."; break;
                    default:
                        snap.Message = unlimited || (balance.HasValue && balance.Value >= 1)
                            ? "Лимит исчерпан — Codex работает за счёт кредитов."
                            : "Лимит исчерпан — Codex недоступен до сброса.";
                        break;
                }
                if (resets > 0) snap.Message += " Можно применить бесплатный сброс в Codex.";
            }
        }

        static string PlanName(string plan)
        {
            switch (plan.ToLowerInvariant())
            {
                case "pro": return "Pro";
                case "plus": return "Plus";
                case "team": return "Team";
                case "business": return "Business";
                case "enterprise": return "Enterprise";
                case "edu": return "Edu";
                case "free": return "Free";
                default: return char.ToUpperInvariant(plan[0]) + plan.Substring(1);
            }
        }

        // npm-шим codex.cmd запускает node; берём нативный codex.exe рядом, иначе — из приложения Codex.
        static string ResolveCodex(ServiceConfig svc)
        {
            if (!string.IsNullOrEmpty(svc.Command)) return Cli.Resolve(svc.Command, "codex");
            string found = Cli.Resolve(null, "codex");
            if (found != null && !found.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                string vendor = Path.Combine(Path.GetDirectoryName(found), NpmVendorExe);
                if (File.Exists(vendor)) return vendor;
            }
            if (found != null) return found;
            try
            {
                string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"OpenAI\Codex\bin");
                if (Directory.Exists(bin))
                    return new DirectoryInfo(bin).GetFiles("codex.exe", SearchOption.AllDirectories)
                        .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();
            }
            catch
            {
            }
            return null;
        }

        static object ReadRateLimits(string exe, int timeoutMs, int maxProcesses, out string error)
        {
            error = null;
            Process p;
            IntPtr job = Cli.Job.Create(maxProcesses);
            try
            {
                p = Process.Start(Cli.StartInfo(exe, "app-server"));
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
                Send(p, "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"limits_widget\",\"title\":\"Limits widget\",\"version\":\"1.0.0\"}}}");
                var init = WaitFor(lines, 1, deadline);
                if (init == null) { error = "нет ответа на initialize"; return null; }
                if (Json.Get(init, "error") != null) { error = Json.Str(Json.Get(init, "error.message")) ?? "ошибка initialize"; return null; }
                Send(p, "{\"method\":\"initialized\"}");
                Send(p, "{\"method\":\"account/rateLimits/read\",\"id\":2}");
                var resp = WaitFor(lines, 2, deadline);
                if (resp == null) { error = "нет ответа на account/rateLimits/read"; return null; }
                if (Json.Get(resp, "error") != null)
                {
                    error = Json.Str(Json.Get(resp, "error.message")) ?? "ошибка rateLimits/read";
                    return null;
                }
                return Json.Get(resp, "result");
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
                if (!p.WaitForExit(3000)) Cli.KillTree(p);
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

        static void Send(Process p, string json)
        {
            p.StandardInput.WriteLine(json);
            p.StandardInput.Flush();
        }

        static object WaitFor(BlockingCollection<string> lines, int id, DateTime deadline)
        {
            while (true)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return null;
                string line;
                if (!lines.TryTake(out line, left)) return null;
                var o = Json.TryParse(line);
                var got = Json.Num(Json.Get(o, "id"));
                if (got.HasValue && (int)got.Value == id) return o;
            }
        }
    }
}
