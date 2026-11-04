using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace LimitsWidget
{
    // Лимиты Kimi Code берутся так.
    // 1) Основной: локальный сервер Kimi Code — desktop-приложение или `kimi web` (~/.kimi-code/server/instances/*.json +
    //    server.token), GET /api/v1/oauth/usage — тот же запрос, что делает панель Plan Usage. Сервер сам продлевает вход.
    //    Перебираются все живые loopback-экземпляры (свежий heartbeat первым) в общем бюджете времени.
    // 2) Если готового сервера нет и разрешено (services.kimi.startServer), виджет запускает свой скрытый
    //    `kimi web --no-open` (KimiServerManager) и спрашивает его.
    // 3) Запасной (только с явным allowLocalTokens): прямой GET {base}/usages с ключом из ~/.kimi-code/credentials.
    //    Ключ только читается — виджет его не обновляет и не записывает.
    sealed class KimiProvider : IProvider
    {
        const double FixedPointCents = 1e6;
        // Kimi Code обновляет heartbeat_at примерно раз в 12 минут, а живость сервера сам проверяет по pid;
        // отметка здесь — только страховка от переиспользованного pid давно закрытого сервера.
        const int HeartbeatMaxAgeSec = 3600;
        const int PerCandidateMs = 5000;
        const string ServerSource = "Kimi Code: локальный сервер → /oauth/usage (вход продлевает сам Kimi Code)";
        const string OwnServerSource = "Kimi Code: скрытый сервер виджета (kimi web --no-open) → /oauth/usage";
        const string LoginText = "Kimi Code просит войти — нажмите «Войти в Kimi Code» (вход выполняет сам Kimi Code).";
        public const string LoginAction = "kimi-login";

        readonly KimiServerManager server;

        public KimiProvider() : this(null) { }

        internal KimiProvider(KimiServerManager server)
        {
            this.server = server ?? KimiServerManager.Shared;
        }

        const string TipMonth = "Один месячный лимит подписки Kimi на Kimi Code, Kimi Work, Kimi Claw и другие функции подписки. Kimi отдаёт итог и долю Kimi Code; остальное — Work, Claw и прочее.";

        // Разобранная квота; два разборщика (сырой ответ API и data.quota сервера) дают один и тот же вид.
        sealed class Entry
        {
            public double UsedRatio;
            public DateTime? ResetAt;
        }

        sealed class Booster
        {
            public double LeftCents, TotalCents;
            public bool LimitEnabled;
            public double? LimitCents, UsedCents;
            public string Currency;
        }

        sealed class KimiQuota
        {
            public Entry Limit5h, Limit7d, MonthTotal, MonthCode;
            public Booster Booster;
        }

        public string Id { get { return "kimi"; } }

        public ServiceSnapshot Collect(ProviderContext ctx)
        {
            var snap = ProviderUtil.NewSnap(ctx, Id);
            snap.Source = "Kimi Code API /coding/v1/usages (как /usage в kimi)";
            string fx = ProviderUtil.Fixture(ctx, "kimi-server-usage.json");
            if (fx != null)
            {
                snap.Source = ServerSource;
                // Current server route awaits fetchManagedUsage; success is a new source query.
                // Token refresh/liveness timestamps are never used as quota observations.
                string ferr;
                var fq = ParseServer(Json.TryParse(fx), out ferr);
                if (fq == null)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Ответ сервера Kimi Code: " + ferr, Placeholders());
                Fill(snap, fq);
                ProviderUtil.MarkFresh(snap, ctx, true);
                ApplyLevel(snap, ctx, PreviousLevel(ctx));
                ctx.PublishCore(snap);
                // необязательная фикстура уровня аккаунта; живой запрос в режиме фикстур не делается
                string ufx = ProviderUtil.Fixture(ctx, "kimi-server-userinfo.json");
                ApplyLevel(snap, ctx, ufx != null ? ParseLevel(Json.TryParse(ufx)) : null);
                return snap;
            }
            fx = ProviderUtil.Fixture(ctx, "kimi-usages.json");
            if (fx != null)
            {
                string aerr;
                var aq = ParseApi(Json.TryParse(fx), out aerr);
                if (aq == null)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Ответ Kimi: " + aerr, Placeholders());
                Fill(snap, aq);
                ProviderUtil.MarkFresh(snap, ctx, true);
                return snap;
            }

            if (ctx.FixtureOnly || !string.IsNullOrEmpty(ctx.FixturesDir)) throw new InvalidOperationException("fixture_missing");
            string home = KimiHome();
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(5, ctx.Svc.TimeoutSeconds) * 1000);

            // 1) готовые локальные серверы (desktop, `kimi web`, свой скрытый)
            var probe = TryServers(home, ctx.Now, deadline);
            if (probe.Login)
                return LoginNeeded(snap);
            if (probe.Quota != null)
                return Served(snap, ctx, probe, home);

            // 2) своего сервера нет — запустить скрытый `kimi web --no-open`, если это разрешено
            string startNote = null;
            if (ctx.Svc.StartServer)
            {
                string exe = Cli.Resolve(ctx.Svc.Command, "kimi");
                if (exe == null) startNote = "kimi (Kimi Code CLI) не найден в PATH — установите его или укажите services.kimi.command.";
                else
                {
                    string detail;
                    var launch = server.EnsureStarted(exe, out detail);
                    if (launch == KimiServerManager.Launch.Started || launch == KimiServerManager.Launch.Running)
                    {
                        // первый запуск: ждём регистрации экземпляра дольше обычного бюджета, но не бесконечно
                        var wait = DateTime.UtcNow.AddSeconds(launch == KimiServerManager.Launch.Started ? 30 : 10);
                        if (wait < deadline) wait = deadline;
                        probe = WaitOwnServer(home, ctx.Now, wait);
                        if (probe.Login) return LoginNeeded(snap);
                        if (probe.Quota != null) return Served(snap, ctx, probe, home);
                        startNote = probe.Note ?? "Скрытый сервер Kimi Code запускается — данные появятся при следующем обновлении.";
                    }
                    else if (launch == KimiServerManager.Launch.Backoff)
                        startNote = "Скрытый сервер Kimi Code недавно завершился — " + detail + ".";
                    else
                        startNote = "Скрытый сервер Kimi Code не запущен: " + (detail ?? "ошибка запуска") + ".";
                }
            }

            // 3) запасной путь: ключ из credentials — только с явным разрешением
            if (!ctx.Config.AllowLocalTokens)
            {
                if (probe.Invalid)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Сервер Kimi Code вернул недопустимые значения квот — показан прошлый снимок.", Placeholders());
                if (probe.Tried > 0)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "нет ответа", "Локальный сервер Kimi Code не ответил (" + probe.Tried + " экз.) — повторю при следующем обновлении." + (startNote != null ? " " + startNote : ""), Placeholders());
                if (startNote != null)
                    return ProviderUtil.Fail(snap, SvcStatus.NoData, "нет сервера", startNote, Placeholders());
                return ProviderUtil.Fail(snap, SvcStatus.NeedsPermission, "нужен доступ",
                    "Локальный сервер Kimi Code не найден. Запустите Kimi Code (`kimi web --no-open`), включите services.kimi.startServer или разрешите прямой запрос: allowLocalTokens в config.json",
                    Placeholders());
            }

            string file = FindTokenFile(home);
            if (file == null)
                return LoginNeeded(snap);

            string token;
            DateTime? expires;
            ReadToken(file, out token, out expires);
            if (token == null)
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Формат файла входа Kimi Code не распознан", Placeholders());
            // пустой ключ — отозванный/выполненный выход: входа нет, сетевой запрос не нужен
            if (token.Trim().Length == 0)
                return LoginNeeded(snap);
            if (expires.HasValue && Fmt.NotAfter(expires.Value, ctx.Now.AddSeconds(30)))
                return ProviderUtil.Fail(snap, SvcStatus.Stale, "ждём Kimi Code",
                    "Вход Kimi Code продлевает сам Kimi Code: запустите Kimi Code или включите services.kimi.startServer — виджет подхватит его сессию.", Placeholders());

            string url = (ctx.Svc.BaseUrl ?? "https://api.kimi.com/coding/v1").TrimEnd('/') + "/usages";
            int status;
            string body = Get(url, token, ctx.Svc.TimeoutSeconds * 1000, true, out status);
            token = null;
            if (status == 401)
                return LoginNeeded(snap);
            if (body == null || status < 200 || status >= 300)
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Kimi usages: HTTP " + status, Placeholders());

            string perr;
            var q = ParseApi(Json.TryParse(body), out perr);
            if (q == null)
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Ответ Kimi: " + perr, Placeholders());
            Fill(snap, q);
            if (snap.Bars.Count == 0)
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Ответ Kimi без квот", Placeholders());
            ProviderUtil.MarkFresh(snap, ctx, false);
            return snap;
        }

        ServiceSnapshot Served(ServiceSnapshot snap, ProviderContext ctx, Probe probe, string home)
        {
            snap.Source = probe.Owned ? OwnServerSource : ServerSource;
            if (probe.Owned) server.MarkHealthy();
            // Current server route awaits fetchManagedUsage; success is a new source query.
            // Token refresh/liveness timestamps are never used as quota observations.
            Fill(snap, probe.Quota);
            ProviderUtil.MarkFresh(snap, ctx, false);
            // квоты готовы — в UI и телеметрию сразу (с прошлым уровнем аккаунта, если он был);
            // свежий уровень догружается отдельно и квоты, их время и статус не меняет
            ApplyLevel(snap, ctx, PreviousLevel(ctx));
            ctx.PublishCore(snap);
            ApplyLevel(snap, ctx, probe.From != null ? ReadLevel(probe.From, home) : null);
            return snap;
        }

        // ---------- уровень аккаунта (GET /api/v1/oauth/userinfo) ----------
        // userInfo.userLevelName — уровень профиля Kimi, а не подтверждение оплаченного тарифа. Необязательная догрузка:
        // на квоты, их время и статус не влияет. Из ответа берётся только эта строка; userId, email, телефон и сам ответ
        // нигде не сохраняются и не пишутся в журнал.
        const int LevelBudgetMs = 3000;
        internal const string LevelSource = "Kimi Code: /oauth/userinfo → userLevelName (уровень аккаунта, не подтверждение оплаты)";

        static string ReadLevel(Instance inst, string home)
        {
            try
            {
                string token = ServerToken(home);
                if (token == null) return null;
                int status;
                string body = Get(inst.BaseUrl + "/api/v1/oauth/userinfo", token, LevelBudgetMs, false, out status);
                token = null;
                if (body == null || status != 200)
                {
                    Log.Write("kimi userinfo: HTTP " + status);
                    return null;
                }
                string level = ParseLevel(Json.TryParse(body));
                body = null;
                if (level == null) Log.Write("kimi userinfo: уровня аккаунта нет в ответе");
                return level;
            }
            catch (Exception ex)
            {
                Log.Write("kimi userinfo: " + ex.GetType().Name);
                return null;
            }
        }

        // data = { kind: "ok", userInfo: { userLevelName } }; всё прочее — нет уровня. Строка, похожая на контакт, отбрасывается.
        internal static string ParseLevel(object env)
        {
            var data = Json.Get(env, "data");
            if (Json.Str(Json.Get(data, "kind")) != "ok") return null;
            var raw = Json.Get(data, "userInfo.userLevelName") as string;
            if (raw == null) return null;
            var sb = new StringBuilder();
            foreach (char c in raw.Trim())
            {
                if (char.IsControl(c)) continue;
                if (char.IsWhiteSpace(c)) { if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' '); continue; }
                sb.Append(c);
            }
            string level = sb.ToString().Trim();
            if (level.Length == 0 || level.Length > 40) return null;
            if (level.IndexOf('@') >= 0 || level.Count(char.IsDigit) >= 6) return null;
            return level;
        }

        // Уровень из последнего удачного снимка (до публикации нового) — чтобы сбой userinfo не стирал известный уровень.
        static string PreviousLevel(ProviderContext ctx)
        {
            var cached = ctx.State != null ? ctx.State.GetCache("kimi") : null;
            return cached != null && cached.AutoPlanIsLevel && !string.IsNullOrEmpty(cached.AutoPlan) ? cached.AutoPlan : null;
        }

        // null — оставить как есть; ручной тариф всё равно главнее (PlanResolver).
        static void ApplyLevel(ServiceSnapshot snap, ProviderContext ctx, string level)
        {
            if (level == null) return;
            snap.AutoPlan = level;
            snap.AutoPlanSource = LevelSource;
            snap.AutoPlanIsLevel = true;
            PlanResolver.Apply(snap, ctx.Svc);
        }

        static ServiceSnapshot LoginNeeded(ServiceSnapshot snap)
        {
            ProviderUtil.Fail(snap, SvcStatus.Error, "нужен вход", LoginText, Placeholders());
            snap.Attention = true;
            snap.ActionId = LoginAction;
            snap.ActionLabel = "Войти в Kimi Code";
            return snap;
        }

        internal static string KimiHome()
        {
            string home = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            if (string.IsNullOrEmpty(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
            return home;
        }

        // ---------- локальные серверы ----------

        sealed class Probe
        {
            public KimiQuota Quota;
            public Instance From;   // экземпляр, ответивший квотой (для уровня аккаунта)
            public bool Login, Invalid, Owned;
            public int Tried;
            public string Note;
        }

        internal sealed class Instance
        {
            public string Host;
            public int Port, Pid;
            public double Heartbeat;
            public string BaseUrl { get { return "http://" + (Host.IndexOf(':') >= 0 ? "[" + Host + "]" : Host) + ":" + Port; } }
        }

        static string ServerToken(string home)
        {
            string tokenFile = Path.Combine(home, "server.token");
            if (!File.Exists(tokenFile)) return null;
            string token = ReadAllShared(tokenFile).Trim();
            return token.Length == 0 ? null : token;
        }

        // Все живые loopback-экземпляры по убыванию heartbeat; каждый спрашивается в пределах общего срока.
        Probe TryServers(string home, DateTime now, DateTime deadline)
        {
            var probe = new Probe();
            try
            {
                var list = FindInstances(home, now);
                if (list.Count == 0) return probe;
                string token = ServerToken(home);
                if (token == null) return probe;
                foreach (var inst in list)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 200) break;
                    probe.Tried++;
                    if (Ask(inst, token, Math.Min(PerCandidateMs, left), probe)) { probe.Owned = server.Owns(inst.Pid); return probe; }
                    if (probe.Login) return probe;   // вход общий для всех экземпляров одного каталога
                }
                token = null;
            }
            catch (Exception ex)
            {
                Log.Write("kimi: локальный сервер Kimi Code: " + ex.GetType().Name);
            }
            return probe;
        }

        // Ждём регистрации своего сервера (экземпляр, чей pid в Job виджета), проверяем, что он требует токен, и спрашиваем.
        Probe WaitOwnServer(string home, DateTime now, DateTime until)
        {
            var probe = new Probe();
            while (DateTime.UtcNow < until)
            {
                if (!server.IsRunning) { probe.Note = "Скрытый сервер Kimi Code завершился при запуске."; return probe; }
                Instance own = null;
                foreach (var inst in FindInstances(home, now)) if (server.Owns(inst.Pid)) { own = inst; break; }
                if (own != null)
                {
                    string token = ServerToken(home);
                    if (token != null && Healthy(own, until))
                    {
                        if (!RequiresAuth(own))
                        {
                            server.Reject("сервер отвечает без проверки bearer-токена");
                            probe.Note = "Скрытый сервер Kimi Code отклонён: он отвечает без проверки токена.";
                            return probe;
                        }
                        int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                        probe.Tried++;
                        if (Ask(own, token, Math.Max(1000, Math.Min(10000, left)), probe)) { probe.Owned = true; return probe; }
                        return probe;
                    }
                }
                Thread.Sleep(250);
            }
            server.Fail("не зарегистрировался вовремя");
            probe.Note = "Скрытый сервер Kimi Code не ответил при запуске — повтор позже.";
            return probe;
        }

        static bool Healthy(Instance inst, DateTime until)
        {
            int status;
            int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
            Get(inst.BaseUrl + "/api/v1/healthz", null, Math.Max(500, Math.Min(3000, left)), false, out status);
            return status == 200;
        }

        // Запрос к /api без токена обязан получить 401 — иначе проверка токена отключена.
        static bool RequiresAuth(Instance inst)
        {
            int status;
            Get(inst.BaseUrl + "/api/v1/auth", null, 3000, false, out status);
            return status == 401;
        }

        // true — получена квота. Иначе отмечает в probe вход/недопустимый ответ.
        static bool Ask(Instance inst, string token, int timeoutMs, Probe probe)
        {
            int status;
            string body = Get(inst.BaseUrl + "/api/v1/oauth/usage", token, timeoutMs, false, out status);
            if (body == null || status != 200)
            {
                Log.Write("kimi: локальный сервер Kimi Code :" + inst.Port + ": HTTP " + status + " (нет ответа или не 200)");
                return false;
            }
            var env = Json.TryParse(body);
            if (Json.Str(Json.Get(env, "data.kind")) == "error")
            {
                // status 401 — сервис квот отверг вход. Без status (Kimi Code 2.4.1: нет ключа, ключ отозван, продление
                // не удалось — и сетевой сбой тоже) различает официальный GET /api/v1/auth: managed_provider.status.
                // Текст ошибки не разбирается и в журнал не пишется.
                double? st = Json.Num(Json.Get(env, "data.status"));
                if (st == 401) { probe.Login = true; return false; }
                if (!st.HasValue)
                {
                    string managed = ManagedStatus(inst, token, Math.Min(AuthCheckMs, timeoutMs));
                    if (managed == "unauthenticated" || managed == "revoked" || managed == "expired") { probe.Login = true; return false; }
                    Log.Write("kimi: локальный сервер Kimi Code :" + inst.Port + ": квоты не получены (kind=error), вход: " + (managed ?? "неизвестно"));
                    return false;
                }
            }
            string err;
            var q = ParseServer(env, out err);
            if (q == null)
            {
                if (err != null && err.StartsWith("недопустим", StringComparison.Ordinal)) probe.Invalid = true;
                Log.Write("kimi: локальный сервер Kimi Code :" + inst.Port + ": " + err);
                return false;
            }
            if (q.Limit5h == null && q.Limit7d == null && q.MonthTotal == null && q.MonthCode == null)
            {
                Log.Write("kimi: локальный сервер Kimi Code :" + inst.Port + ": ответ без квот");
                return false;
            }
            probe.Quota = q;
            probe.From = inst;
            return true;
        }

        const int AuthCheckMs = 3000;

        // managed_provider.status из GET /api/v1/auth: authenticated | expired | revoked | unauthenticated; иное/сбой — null.
        internal static string ManagedStatus(Instance inst, string token, int timeoutMs)
        {
            int status;
            string body = Get(inst.BaseUrl + "/api/v1/auth", token, Math.Max(500, timeoutMs), false, out status);
            if (body == null || status != 200) return null;
            string v = Json.Str(Json.Get(Json.TryParse(body), "data.managed_provider.status"));
            switch (v)
            {
                case "authenticated": case "expired": case "revoked": case "unauthenticated": return v;
                default: return null;
            }
        }

        // Живые loopback-экземпляры, свежий heartbeat первым; токен уходит только на свою машину.
        internal static List<Instance> FindInstances(string home, DateTime now)
        {
            var res = new List<Instance>();
            string dir = Path.Combine(home, "server", "instances");
            if (!Directory.Exists(dir)) return res;
            double nowMs = (Fmt.Utc(now) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            var seen = new HashSet<string>();
            foreach (string f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var root = Json.TryParse(ReadAllShared(f));
                    string h = Json.Str(Json.Get(root, "host"));
                    double? p = Json.Num(Json.Get(root, "port"));
                    double? pid = Json.Num(Json.Get(root, "pid"));
                    double? hb = Json.Num(Json.Get(root, "heartbeat_at"));
                    if (h == null || !p.HasValue || !pid.HasValue || !hb.HasValue) continue;
                    if (h != "127.0.0.1" && h != "localhost" && h != "::1") continue;
                    if (p.Value < 1 || p.Value > 65535 || p.Value != Math.Floor(p.Value)) continue;
                    if (pid.Value < 1 || pid.Value > int.MaxValue || pid.Value != Math.Floor(pid.Value)) continue;
                    if (nowMs - hb.Value > HeartbeatMaxAgeSec * 1000.0) continue;
                    if (!PidAlive((int)pid.Value)) continue;
                    if (!seen.Add(h + ":" + (int)p.Value)) continue;
                    res.Add(new Instance { Host = h, Port = (int)p.Value, Pid = (int)pid.Value, Heartbeat = hb.Value });
                }
                catch
                {
                    // битый или занятый файл экземпляра — пропустить
                }
            }
            return res.OrderByDescending(x => x.Heartbeat).ToList();
        }

        // Как у самого Kimi (process.kill(pid, 0)): процесса нет — мёртв; есть, но спросить о нём нельзя — жив.
        // HasExited у приложения Kimi Code падает с отказом в доступе, и это не значит, что оно закрыто.
        static bool PidAlive(int pid)
        {
            Process pr;
            try { pr = Process.GetProcessById(pid); }
            catch { return false; }
            using (pr)
            {
                try { return !pr.HasExited; }
                catch { return true; }
            }
        }

        static string ReadAllShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // ---------- разбор ----------

        // Доля расхода 0..1: отсутствующее/нечисловое — окна нет (не ноль); NaN, бесконечность и выход за диапазон — ответ недостоверен.
        sealed class RatioCheck
        {
            public string Bad;

            public Entry Make(object node, string ratioKey, string resetKey, string name)
            {
                object raw = Json.Get(node, ratioKey);
                if (raw == null) return null;
                double? ratio = Json.Num(raw);
                if (!ratio.HasValue) return null;
                if (double.IsNaN(ratio.Value) || double.IsInfinity(ratio.Value) || ratio.Value < 0 || ratio.Value > 1)
                {
                    if (Bad == null) Bad = name;
                    return null;
                }
                return new Entry { UsedRatio = ratio.Value, ResetAt = Json.Time(Json.Get(node, resetKey)) };
            }
        }

        // Сырой ответ API: snake_case, Booster в fixed-point. null — ответ недостоверен (err).
        static KimiQuota ParseApi(object root, out string err)
        {
            err = null;
            var q = new KimiQuota();
            var usages = Json.Get(root, "usages");
            var check = new RatioCheck();
            q.Limit5h = check.Make(Json.Get(usages, "limit_5h"), "used_ratio", "reset_time", "limit_5h");
            q.Limit7d = check.Make(Json.Get(usages, "limit_7d"), "used_ratio", "reset_time", "limit_7d");
            q.MonthTotal = check.Make(Json.Get(usages, "limit_month_total"), "used_ratio", "reset_time", "limit_month_total");
            q.MonthCode = check.Make(Json.Get(usages, "limit_month_code"), "used_ratio", "reset_time", "limit_month_code");
            if (check.Bad != null) { err = "недопустимая доля расхода (" + check.Bad + ")"; return null; }

            var wallet = Json.Get(root, "boosterWallet");
            var balance = Json.Get(wallet, "balance");
            if (Json.Str(Json.Get(balance, "type")) == "BOOSTER")
            {
                double? amount = Json.Num(Json.Get(balance, "amount"));
                double? amountLeft = Json.Num(Json.Get(balance, "amountLeft"));
                if (amount.HasValue)
                    q.Booster = new Booster
                    {
                        TotalCents = amount.Value / FixedPointCents,
                        LeftCents = (amountLeft ?? 0) / FixedPointCents,
                        LimitEnabled = Json.Bool(Json.Get(wallet, "monthlyChargeLimitEnabled")) == true,
                        LimitCents = Json.Num(Json.Get(wallet, "monthlyChargeLimit.priceInCents")),
                        UsedCents = Json.Num(Json.Get(wallet, "monthlyUsed.priceInCents")),
                        Currency = Json.Str(Json.Get(wallet, "monthlyChargeLimit.currency"))
                            ?? Json.Str(Json.Get(wallet, "monthlyUsed.currency")) ?? "USD"
                    };
            }
            return q;
        }

        // Конверт сервера { code, msg, data }: data.kind == "ok" -> data.quota (camelCase, суммы уже в центах).
        static KimiQuota ParseServer(object env, out string err)
        {
            err = null;
            var data = Json.Get(env, "data");
            if (data == null) { err = "нет data"; return null; }
            string kind = Json.Str(Json.Get(data, "kind"));
            if (kind != "ok")
            {
                double? st = Json.Num(Json.Get(data, "status"));
                err = "kind=" + (kind ?? "?") + (st.HasValue ? ", status " + st.Value : "");
                return null;
            }
            var quota = Json.Get(data, "quota");
            if (quota == null) { err = "нет quota"; return null; }
            var u = Json.Get(quota, "usages");
            var q = new KimiQuota();
            var check = new RatioCheck();
            q.Limit5h = check.Make(Json.Get(u, "limit5h"), "usedRatio", "resetAt", "limit5h");
            q.Limit7d = check.Make(Json.Get(u, "limit7d"), "usedRatio", "resetAt", "limit7d");
            q.MonthTotal = check.Make(Json.Get(u, "monthTotal"), "usedRatio", "resetAt", "monthTotal");
            q.MonthCode = check.Make(Json.Get(u, "monthCode"), "usedRatio", "resetAt", "monthCode");
            if (check.Bad != null) { err = "недопустимая доля расхода (" + check.Bad + ")"; return null; }

            var ex = Json.Get(quota, "extraUsage");
            if (ex != null)
            {
                double? bal = Json.Num(Json.Get(ex, "balanceCents"));
                double? tot = Json.Num(Json.Get(ex, "totalCents"));
                if (bal.HasValue && tot.HasValue)
                    q.Booster = new Booster
                    {
                        LeftCents = bal.Value,
                        TotalCents = tot.Value,
                        LimitEnabled = Json.Bool(Json.Get(ex, "monthlyChargeLimitEnabled")) == true,
                        LimitCents = Json.Num(Json.Get(ex, "monthlyChargeLimitCents")),
                        UsedCents = Json.Num(Json.Get(ex, "monthlyUsedCents")),
                        Currency = Json.Str(Json.Get(ex, "currency")) ?? "USD"
                    };
            }
            return q;
        }

        // ---------- построение карточки (общее для обоих путей) ----------

        static void Fill(ServiceSnapshot snap, KimiQuota q)
        {
            var h5 = Add(snap, q.Limit5h, "5" + Fmt.Nbsp + "ч", "Окно 5 часов · только Kimi Code", "subscription.code", "five_hour", 18000);
            if (h5 != null) h5.Tip = "Лимит 5 часов действует только для Kimi Code.";
            var d7 = Add(snap, q.Limit7d, "7" + Fmt.Nbsp + "д", "Неделя · только Kimi Code", "subscription.code", "seven_day", 604800);
            if (d7 != null) d7.Tip = "Недельный лимит — только для Kimi Code (у старых тарифов).";
            var month = Add(snap, q.MonthTotal, "мес", "Месяц · общий лимит подписки", "subscription.shared", "month", null);
            if (month != null)
            {
                month.Tip = TipMonth;
                if (q.MonthCode != null)
                {
                    // месячный пул общий: Kimi отдаёт итог и долю Kimi Code, остальное — Work, Claw и прочее
                    double codePct = q.MonthCode.UsedRatio * 100;
                    double otherPct = Math.Max(0, month.Used.Value - codePct);
                    month.Parts = new List<ExtraPart>
                    {
                        new ExtraPart { Key = "kimi_code", Name = "Kimi Code", Percent = codePct },
                        new ExtraPart { Key = "kimi_other", Name = "Work, Claw и прочее", Percent = otherPct }
                    };
                }
            }

            var b = q.Booster;
            if (b != null && b.TotalCents > 0 && b.LeftCents / 100 >= 0.01)
                snap.Extras.Add(BoosterItem(b));
        }

        // Booster — платное использование сверх лимитов тарифа: остаток, а при заданном лимите трат — расход за месяц
        static ExtraItem BoosterItem(Booster b)
        {
            double total = b.TotalCents / 100, left = b.LeftCents / 100;
            string cur = b.Currency;
            var e = new ExtraItem();
            e.Icon = "";
            e.Label = "Booster";
            e.Value = CreditStats.Money(left, cur) + " из " + CreditStats.Money(total, cur);
            e.Fill = Math.Max(0, Math.Min(1, (total - left) / total));
            double? used = b.UsedCents;
            double? limit = b.LimitCents;
            if (b.LimitEnabled && limit.HasValue && limit.Value > 0)
                e.Detail = "за месяц " + CreditStats.Money((used ?? 0) / 100, cur) + " из " + CreditStats.Money(limit.Value / 100, cur);
            else if (used.HasValue)
                e.Detail = "за месяц " + CreditStats.Money(used.Value / 100, cur);
            e.Detail = e.Detail != null ? "остаток · " + e.Detail : "остаток";
            e.Accent = left < total * 0.1 ? "warn" : "info";
            e.Tip = "Booster — оплачиваемое дополнительное использование Kimi Code сверх лимитов тарифа.";
            return e;
        }

        static LimitBar Add(ServiceSnapshot snap, Entry entry, string chip, string label, string pool, string window, long? duration)
        {
            if (entry == null) return null;
            var bar = new LimitBar { Chip = chip, Label = label, Used = entry.UsedRatio * 100, ResetsAt = entry.ResetAt };
            bar.Quota = QuotaObservation.Create("kimi", pool, window, duration, bar.Used, bar.ResetsAt);
            snap.Bars.Add(bar);
            return bar;
        }

        static LimitBar[] Placeholders()
        {
            return new[]
            {
                ProviderUtil.Placeholder("5" + Fmt.Nbsp + "ч", "Окно 5 часов · только Kimi Code"),
                ProviderUtil.Placeholder("7" + Fmt.Nbsp + "д", "Неделя · только Kimi Code"),
                ProviderUtil.Placeholder("мес", "Месяц · общий лимит подписки")
            };
        }

        static string FindTokenFile(string home)
        {
            string dir = Path.Combine(home, "credentials");
            if (!Directory.Exists(dir)) return null;
            return new DirectoryInfo(dir).GetFiles("kimi-code*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();
        }

        static void ReadToken(string path, out string token, out DateTime? expires)
        {
            token = null;
            expires = null;
            var root = Json.TryParse(ReadAllShared(path));
            token = Json.Str(Json.Find(root, new[] { "access_token", "accessToken" }, 6));
            // разобранный файл входа без ключа (null после выхода/отзыва) — входа нет, а не «неизвестный формат»
            if (token == null && Json.Obj(root) != null) token = "";
            expires = Json.Time(Json.Find(root, new[] { "expires_at", "expiresAt", "expiry" }, 6));
            if (expires.HasValue && expires.Value.Year < 2020) expires = null;
            if (!expires.HasValue && !string.IsNullOrEmpty(token)) expires = JwtExpiry(token);
        }

        static DateTime? JwtExpiry(string jwt)
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;
            try
            {
                string p = parts[1].Replace('-', '+').Replace('_', '/');
                switch (p.Length % 4) { case 2: p += "=="; break; case 3: p += "="; break; }
                return Json.Time(Json.Get(Json.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(p))), "exp"));
            }
            catch
            {
                return null;
            }
        }

        // useProxy=false — запрос на свою машину (локальный сервер приложения), системный прокси не нужен
        static string Get(string url, string token, int timeoutMs, bool useProxy, out int status)
        {
            status = 0;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                if (!useProxy) req.Proxy = null;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Accept = "application/json";
                req.UserAgent = "LimitsWidget/" + Program.Version;
                if (token != null) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    status = (int)resp.StatusCode;
                    return sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null) { status = (int)r.StatusCode; r.Close(); }
                return null;
            }
        }
    }
}
