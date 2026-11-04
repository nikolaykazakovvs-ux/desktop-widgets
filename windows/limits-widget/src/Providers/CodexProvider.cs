using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace LimitsWidget
{
    // codex app-server (JSON-RPC по stdio): initialize -> initialized -> account/rateLimits/read, затем в том же процессе
    // необязательный account/usage/read (статистика токенов по всему аккаунту, без threadId) — не дольше 5 секунд оставшегося
    // бюджета. Квоты публикуются до статистики; её сбой квоты не ломает. Лимиты приходят с сервера OpenAI через сам Codex CLI,
    // авторизацию делает он.
    sealed class CodexProvider : IProvider
    {
        const string NpmVendorExe = @"node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe";
        const int ActivityBudgetMs = 5000;

        public string Id { get { return "codex"; } }

        public ServiceSnapshot Collect(ProviderContext ctx)
        {
            var snap = ProviderUtil.NewSnap(ctx, Id);
            snap.Source = "codex app-server → account/rateLimits/read";
            string text = ProviderUtil.Fixture(ctx, "codex-ratelimits.json");
            bool fixture = text != null;
            object result;
            AppServer session = null;
            try
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(ctx.Svc.TimeoutSeconds * 1000);
                if (fixture)
                {
                    result = Json.Get(Json.TryParse(text), "result");
                }
                else
                {
                    if (ctx.FixtureOnly || !string.IsNullOrEmpty(ctx.FixturesDir)) throw new InvalidOperationException("fixture_missing");
                    string exe = ResolveCodex(ctx.Svc);
                    if (exe == null)
                        return ProviderUtil.Fail(snap, SvcStatus.NoData, "нет CLI", "codex не найден в PATH", Placeholder());
                    string error;
                    session = AppServer.Start(exe, ctx.Svc.MaxProcesses, out error);
                    result = session == null ? null : session.Initialize(deadline, out error) ? session.Request("account/rateLimits/read", null, deadline, out error) : null;
                    if (result == null)
                        return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "codex app-server: " + (session != null ? session.Explain(error) : error), Placeholder());
                }
                if (!FillLimits(snap, result, ctx, fixture))
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "codex не вернул окон лимитов", Placeholder());

                // квоты готовы — в UI и телеметрию сразу; статистика догружается в том же процессе
                ProviderUtil.MarkFresh(snap, ctx, fixture);
                ctx.PublishCore(snap);
                snap.Activity = ReadActivity(ctx, session, fixture, deadline);
                return snap;
            }
            finally
            {
                if (session != null) session.Dispose();
            }
        }

        // Статистика использования: независимое время, null/отсутствие не превращаются в ноль. Сбой — прошлая статистика с пометкой.
        static TokenActivity ReadActivity(ProviderContext ctx, AppServer session, bool fixture, DateTime deadline)
        {
            TokenActivity fresh = null;
            if (fixture)
            {
                string text = ProviderUtil.Fixture(ctx, "codex-account-usage.json");
                if (text != null) fresh = TokenActivity.Parse(Json.Get(Json.TryParse(text), "result"), ctx.UtcNow());
                return fresh;   // фикстура статистики необязательна; живой запрос в режиме фикстур не делается
            }
            if (session != null)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                int budget = Math.Min(ActivityBudgetMs, left);
                if (budget > 200)
                {
                    string error;
                    // без threadId — статистика всего аккаунта
                    var r = session.Request("account/usage/read", "{}", DateTime.UtcNow.AddMilliseconds(budget), out error);
                    if (r != null) fresh = TokenActivity.Parse(r, ctx.UtcNow());
                    if (fresh == null) Log.Write("codex account/usage/read: " + (error ?? "ответ не распознан"));
                }
            }
            if (fresh != null) return fresh;
            var cached = ctx.State != null ? ctx.State.GetCache("codex") : null;
            if (cached != null && cached.Activity != null)
            {
                cached.Activity.Stale = true;
                return cached.Activity;
            }
            return null;
        }

        static bool FillLimits(ServiceSnapshot snap, object result, ProviderContext ctx, bool fixture)
        {
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
                AddWindow(snap, Json.Get(l, "primary"), name, model, ctx.Now, id);
                AddWindow(snap, Json.Get(l, "secondary"), name, model, ctx.Now, id);
            }

            var head = limits.FirstOrDefault();
            if (head != null)
            {
                // planType — подтверждённое поле протокола app-server; ручное имя из config.json главнее (PlanResolver)
                string plan = Json.Str(Json.Get(head, "planType"));
                if (!string.IsNullOrEmpty(plan) && !plan.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    snap.AutoPlan = PlanName(plan);
                    snap.AutoPlanSource = "Codex app-server: account/rateLimits/read → planType";
                    PlanResolver.Apply(snap, ctx.Svc);
                }
            }
            DescribeCreditsAndResets(snap, result, head, ctx, fixture);
            return snap.Bars.Count > 0;
        }

        static void AddWindow(ServiceSnapshot snap, object w, string name, string model, DateTime now, string limitId)
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
            bar.Quota = QuotaObservation.Create("codex", limitId == "codex" ? "subscription.general" : limitId == "gpt-reserve" ? "subscription.reserve" : null, QuotaObservation.Window(mins), mins.HasValue ? (long?)(mins.Value * 60) : null, bar.Used, Json.Time(Json.Get(w, "resetsAt")));
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

        internal static string PlanName(string plan)
        {
            switch (plan.ToLowerInvariant())
            {
                case "pro": return "Pro";
                case "prolite": return "Pro Lite";
                case "promax": return "Pro Max";
                case "plus": return "Plus";
                case "go": return "Go";
                case "team": return "Team";
                case "business": return "Business";
                case "enterprise": return "Enterprise";
                case "edu": return "Edu";
                case "edu_plus": return "Edu Plus";
                case "edu_pro": return "Edu Pro";
                case "free": return "Free";
                default:
                    // прочие значения схемы (self_serve_business_*, enterprise_cbp_*, sci*) — читаемо, без выдумки
                    var words = plan.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1));
                    return string.Join(" ", words);
            }
        }

        // npm-шим codex.cmd запускает node; берём нативный codex.exe рядом, иначе — из приложения Codex.
        // Обход каталога приложения Codex кэшируется на 5 минут (как и поиск в PATH в Cli.Resolve).
        static readonly object AppBinGate = new object();
        static string appBinHit;
        static DateTime appBinAt;

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
            lock (AppBinGate)
            {
                if (appBinHit != null && File.Exists(appBinHit)) return appBinHit;
                if (DateTime.UtcNow - appBinAt < TimeSpan.FromMinutes(5)) return appBinHit;
                appBinAt = DateTime.UtcNow;
                appBinHit = null;
                try
                {
                    string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
                    if (Directory.Exists(bin))
                        appBinHit = new DirectoryInfo(bin).GetFiles("codex.exe", SearchOption.AllDirectories)
                            .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();
                }
                catch
                {
                }
                return appBinHit;
            }
        }
    }

    // Один процесс codex app-server на опрос: JSON-RPC построчно по stdio, процесс и его потомки — в Job.
    sealed class AppServer : IDisposable
    {
        readonly Process p;
        readonly IntPtr job;
        readonly BlockingCollection<string> lines = new BlockingCollection<string>();
        readonly System.Threading.Tasks.Task<string> stderr;
        int nextId = 1;

        AppServer(Process p, IntPtr job)
        {
            this.p = p;
            this.job = job;
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
            stderr = p.StandardError.ReadToEndAsync();
        }

        public static AppServer Start(string exe, int maxProcesses, out string error)
        {
            error = null;
            IntPtr job = Cli.Job.Create(maxProcesses);
            try
            {
                var p = Process.Start(Cli.StartInfo(exe, "app-server"));
                Cli.Job.Assign(job, p);
                return new AppServer(p, job);
            }
            catch (Exception ex)
            {
                Cli.Job.Close(job);
                error = "не запустился: " + ex.Message;
                return null;
            }
        }

        public bool Initialize(DateTime deadline, out string error)
        {
            var init = Call("initialize", "{\"clientInfo\":{\"name\":\"limits_widget\",\"title\":\"Limits widget\",\"version\":\"" + Program.Version + "\"}}", deadline, out error);
            if (init == null) return false;
            Send("{\"method\":\"initialized\"}");
            return true;
        }

        // result ответа или null (error — причина без сырых данных аккаунта).
        public object Request(string method, string paramsJson, DateTime deadline, out string error)
        {
            return Call(method, paramsJson, deadline, out error);
        }

        object Call(string method, string paramsJson, DateTime deadline, out string error)
        {
            error = null;
            try
            {
                int id = nextId++;
                Send("{\"method\":\"" + method + "\",\"id\":" + id + (paramsJson != null ? ",\"params\":" + paramsJson : "") + "}");
                var resp = WaitFor(id, deadline);
                if (resp == null) { error = lines.IsCompleted ? "процесс завершился без ответа на " + method : "нет ответа на " + method; return null; }
                if (Json.Get(resp, "error") != null) { error = Json.Str(Json.Get(resp, "error.message")) ?? "ошибка " + method; return null; }
                var result = Json.Get(resp, "result");
                if (result == null) error = "пустой ответ на " + method;
                return result;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        void Send(string json)
        {
            p.StandardInput.WriteLine(json);
            p.StandardInput.Flush();
        }

        object WaitFor(int id, DateTime deadline)
        {
            while (true)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return null;
                string line;
                if (!lines.TryTake(out line, left)) return null;
                var o = Json.TryParse(line);
                var got = Json.Num(Json.Get(o, "id"));
                if (got.HasValue && (int)got.Value == id && (Json.Get(o, "result") != null || Json.Get(o, "error") != null)) return o;
            }
        }

        // К причине сбоя добавляется первая строка stderr CLI (без токенов: CLI их туда не пишет).
        public string Explain(string error)
        {
            string hint = null;
            try
            {
                try { p.StandardInput.Close(); }
                catch { }
                if (p.WaitForExit(1000) && stderr.Wait(500)) hint = Cli.FirstLine(stderr.Result);
            }
            catch
            {
            }
            return hint != null ? error + " (" + hint + ")" : error;
        }

        public void Dispose()
        {
            try { p.StandardInput.Close(); }
            catch { }
            try { if (!p.WaitForExit(3000)) Cli.KillTree(p); }
            catch { }
            p.Dispose();
            Cli.Job.Close(job);
        }
    }
}
