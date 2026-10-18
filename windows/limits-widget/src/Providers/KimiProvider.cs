using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace LimitsWidget
{
    // Лимиты Kimi Code берутся двумя путями.
    // 1) Основной: локальный сервер приложения Kimi Code (~/.kimi-code/server/instances/*.json + server.token),
    //    GET /api/v1/oauth/usage — тот же запрос, что делает панель Plan Usage. Сервер сам продлевает вход
    //    (ключ живёт 15 минут), поэтому ни браузер, ни `kimi login` не нужны.
    // 2) Запасной (приложение не запущено): прямой GET {base}/usages с ключом из ~/.kimi-code/credentials.
    //    Ключ только читается — виджет его не обновляет и не записывает; нужен флаг allowLocalTokens.
    sealed class KimiProvider : IProvider
    {
        const double FixedPointCents = 1e6;
        // Kimi Code обновляет heartbeat_at примерно раз в 12 минут, а живость сервера сам проверяет по pid;
        // отметка здесь — только страховка от переиспользованного pid давно закрытого сервера.
        const int HeartbeatMaxAgeSec = 3600;
        const string ServerSource = "Kimi Code: локальный сервер приложения → /oauth/usage (вход продлевает сам Kimi Code)";
        const string LoginText = "Kimi Code просит войти заново — войдите в приложении Kimi Code.";

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
                string ferr;
                var fq = ParseServer(Json.TryParse(fx), out ferr);
                if (fq == null)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Ответ сервера Kimi Code: " + ferr, Placeholders());
                Fill(snap, fq);
                ProviderUtil.MarkFresh(snap, ctx, true);
                return snap;
            }
            fx = ProviderUtil.Fixture(ctx, "kimi-usages.json");
            if (fx != null)
            {
                Fill(snap, ParseApi(Json.TryParse(fx)));
                ProviderUtil.MarkFresh(snap, ctx, true);
                return snap;
            }

            string home = KimiHome();

            // 1) локальный сервер приложения
            bool login401;
            bool serverFailed;
            var q = TryServer(ctx, home, out login401, out serverFailed);
            if (login401)
            {
                ProviderUtil.Fail(snap, SvcStatus.Error, "нужен вход", LoginText, Placeholders());
                snap.Attention = true;
                return snap;
            }
            if (q != null)
            {
                snap.Source = ServerSource;
                Fill(snap, q);
                ProviderUtil.MarkFresh(snap, ctx, false);
                return snap;
            }

            // 2) запасной путь: ключ из credentials
            if (!ctx.Config.AllowLocalTokens)
            {
                if (serverFailed)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "нет ответа", "Сервер приложения Kimi Code не ответил — данные обновятся, когда он ответит.", Placeholders());
                return ProviderUtil.Fail(snap, SvcStatus.NeedsPermission, "нужен доступ",
                    "Сервер приложения Kimi Code не найден. Для прямого запроса нужно разрешение читать токен kimi: allowLocalTokens в config.json",
                    Placeholders());
            }

            string file = FindTokenFile(home);
            if (file == null)
                return ProviderUtil.Fail(snap, SvcStatus.NoData, "нет входа", "Нет входа в Kimi Code — войдите в приложении Kimi Code.", Placeholders());

            string token;
            DateTime? expires;
            ReadToken(file, out token, out expires);
            if (token == null)
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Формат файла входа Kimi Code не распознан", Placeholders());
            if (expires.HasValue && expires.Value <= ctx.Now.AddSeconds(30))
                return ProviderUtil.Fail(snap, SvcStatus.Stale, "ждём Kimi Code",
                    "Вход Kimi Code продлевается самим Kimi Code: откройте приложение Kimi Code — виджет подхватит его сессию сам.", Placeholders());

            string url = (ctx.Svc.BaseUrl ?? "https://api.kimi.com/coding/v1").TrimEnd('/') + "/usages";
            int status;
            string body = Get(url, token, ctx.Svc.TimeoutSeconds * 1000, true, out status);
            token = null;
            if (status == 401)
            {
                ProviderUtil.Fail(snap, SvcStatus.Error, "нужен вход", LoginText, Placeholders());
                snap.Attention = true;
                return snap;
            }
            if (body == null || status < 200 || status >= 300)
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Kimi usages: HTTP " + status, Placeholders());

            Fill(snap, ParseApi(Json.TryParse(body)));
            if (snap.Bars.Count == 0)
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "Ответ Kimi без квот", Placeholders());
            ProviderUtil.MarkFresh(snap, ctx, false);
            return snap;
        }

        static string KimiHome()
        {
            string home = Environment.GetEnvironmentVariable("KIMI_CODE_HOME");
            if (string.IsNullOrEmpty(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code");
            return home;
        }

        // ---------- локальный сервер приложения ----------

        // Квота с сервера приложения или null (тогда запасной путь). login401 — сервер сказал, что нужен вход.
        static KimiQuota TryServer(ProviderContext ctx, string home, out bool login401, out bool serverFailed)
        {
            login401 = false;
            serverFailed = false;
            try
            {
                string host;
                int port;
                if (!FindInstance(home, ctx.Now, out host, out port)) return null;
                string tokenFile = Path.Combine(home, "server.token");
                if (!File.Exists(tokenFile)) return null;
                string token = ReadAllShared(tokenFile).Trim();
                if (token.Length == 0) return null;

                string h = host.IndexOf(':') >= 0 ? "[" + host + "]" : host;
                string url = "http://" + h + ":" + port + "/api/v1/oauth/usage";
                int timeoutSec = Math.Min(10, ctx.Svc.TimeoutSeconds);
                serverFailed = true;   // экземпляр найден: дальше любая неудача — «не ответил», а не «не найден»
                int status;
                string body = Get(url, token, timeoutSec * 1000, false, out status);
                token = null;
                if (body == null || status != 200)
                {
                    Log.Write("kimi: локальный сервер Kimi Code: HTTP " + status + " (нет ответа или не 200)");
                    return null;
                }
                var env = Json.TryParse(body);
                if (Json.Str(Json.Get(env, "data.kind")) == "error" && Json.Num(Json.Get(env, "data.status")) == 401)
                {
                    login401 = true;
                    return null;
                }
                string err;
                var q = ParseServer(env, out err);
                if (q == null) Log.Write("kimi: локальный сервер Kimi Code: " + err);
                else if (q.Limit5h == null && q.Limit7d == null && q.MonthTotal == null && q.MonthCode == null)
                {
                    Log.Write("kimi: локальный сервер Kimi Code: ответ без квот");
                    return null;
                }
                return q;
            }
            catch (Exception ex)
            {
                Log.Write("kimi: локальный сервер Kimi Code: " + ex.GetType().Name);
                return null;
            }
        }

        // Живой экземпляр с самым свежим heartbeat_at; токен уходит только на свою машину.
        static bool FindInstance(string home, DateTime now, out string host, out int port)
        {
            host = null;
            port = 0;
            string dir = Path.Combine(home, "server", "instances");
            if (!Directory.Exists(dir)) return false;
            double nowMs = (now.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            double best = double.MinValue;
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
                    if (nowMs - hb.Value > HeartbeatMaxAgeSec * 1000.0) continue;
                    if (!PidAlive((int)pid.Value)) continue;
                    if (hb.Value > best)
                    {
                        best = hb.Value;
                        host = h;
                        port = (int)p.Value;
                    }
                }
                catch
                {
                    // битый или занятый файл экземпляра — пропустить
                }
            }
            return host != null;
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

        // Сырой ответ API: snake_case, Booster в fixed-point.
        static KimiQuota ParseApi(object root)
        {
            var q = new KimiQuota();
            var usages = Json.Get(root, "usages");
            q.Limit5h = ApiEntry(Json.Get(usages, "limit_5h"));
            q.Limit7d = ApiEntry(Json.Get(usages, "limit_7d"));
            q.MonthTotal = ApiEntry(Json.Get(usages, "limit_month_total"));
            q.MonthCode = ApiEntry(Json.Get(usages, "limit_month_code"));

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

        static Entry ApiEntry(object node)
        {
            double? ratio = Json.Num(Json.Get(node, "used_ratio"));
            if (!ratio.HasValue) return null;
            return new Entry { UsedRatio = ratio.Value, ResetAt = Json.Time(Json.Get(node, "reset_time")) };
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
            q.Limit5h = ServerEntry(Json.Get(u, "limit5h"));
            q.Limit7d = ServerEntry(Json.Get(u, "limit7d"));
            q.MonthTotal = ServerEntry(Json.Get(u, "monthTotal"));
            q.MonthCode = ServerEntry(Json.Get(u, "monthCode"));

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

        static Entry ServerEntry(object node)
        {
            double? ratio = Json.Num(Json.Get(node, "usedRatio"));
            if (!ratio.HasValue) return null;
            return new Entry { UsedRatio = ratio.Value, ResetAt = Json.Time(Json.Get(node, "resetAt")) };
        }

        // ---------- построение карточки (общее для обоих путей) ----------

        static void Fill(ServiceSnapshot snap, KimiQuota q)
        {
            var h5 = Add(snap, q.Limit5h, "5" + Fmt.Nbsp + "ч", "Окно 5 часов · только Kimi Code");
            if (h5 != null) h5.Tip = "Лимит 5 часов действует только для Kimi Code.";
            var d7 = Add(snap, q.Limit7d, "7" + Fmt.Nbsp + "д", "Неделя · только Kimi Code");
            if (d7 != null) d7.Tip = "Недельный лимит — только для Kimi Code (у старых тарифов).";
            var month = Add(snap, q.MonthTotal, "мес", "Месяц · общий лимит подписки");
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

        static LimitBar Add(ServiceSnapshot snap, Entry entry, string chip, string label)
        {
            if (entry == null) return null;
            var bar = new LimitBar { Chip = chip, Label = label, Used = entry.UsedRatio * 100, ResetsAt = entry.ResetAt };
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
            expires = Json.Time(Json.Find(root, new[] { "expires_at", "expiresAt", "expiry" }, 6));
            if (expires.HasValue && expires.Value.Year < 2020) expires = null;
            if (!expires.HasValue && token != null) expires = JwtExpiry(token);
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
                req.UserAgent = "LimitsWidget/1.0";
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
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
