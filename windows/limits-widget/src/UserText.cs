using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LimitsWidget
{
    // Причина сбоя по смыслу технического текста.
    enum FailKind { Other, NeedLogin, NoLink, RateLimited, TimedOut }

    // Тексты ошибок для пользователя. В карточку попадает только короткая фраза по-русски: что случилось и что дальше.
    // Технический текст (stderr CLI, исключение, ответ сервера, URL) остаётся в widget.log — с вычищенными токенами и без
    // параметров URL. LooksTechnical — предохранитель сборщика: он ловит и те ошибки, о которых провайдер не подумал.
    static class UserText
    {
        const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // Порядок проверок важен: «вход» решает раньше «связи» (401 вместе с connection — это вход), «прочее» — последним.
        static readonly Regex NeedLogin = new Regex(@"\b(401|403)\b|unauthori[sz]ed|unauthenticated|forbidden|not logged in|not authenticated|authentication (failed|required|error)|login required|log ?in again|(token|session) (has )?expired|invalid (token|credentials)|нужен вход|просит войти", Ci | RegexOptions.Compiled);
        static readonly Regex NoLink = new Regex(@"error sending request|connection|connect|network|\bdns\b|resolve|name resolution|no such host|dial tcp|unreachable|\btls\b|\bssl\b|certificate|\beof\b|reset by peer|refused|\b(502|503|504)\b|failed to get .*response|(post|get) ""http|offline|нет связи|нет сети|нет интернета", Ci | RegexOptions.Compiled);
        static readonly Regex RateLimited = new Regex(@"\b429\b|rate limit|too many requests", Ci | RegexOptions.Compiled);
        static readonly Regex TimedOut = new Regex(@"timeout|timed out|не ответил", Ci | RegexOptions.Compiled);

        // признаки сырого технического вывода
        static readonly Regex HttpCode = new Regex(@"HTTP\s?\d", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        static readonly Regex CliCode = new Regex(@"код \d", Ci | RegexOptions.Compiled);
        static readonly Regex DrivePath = new Regex(@"\b[A-Za-z]:\\", RegexOptions.Compiled);
        static readonly Regex IsoStamp = new Regex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}", RegexOptions.Compiled);
        static readonly Regex StackFrame = new Regex(@"\bat [\w.<>`]+\(|0x[0-9a-fA-F]{6,}", RegexOptions.Compiled);
        // имена процессов и команды CLI («agy /usage», «codex app-server»): в своих сообщениях виджет пишет «Codex», «Kimi Code»
        static readonly Regex CliToken = new Regex(@"(?<![\w/])/[A-Za-z]{3,}|\b(agy|codex|claude|kimi|npx|node)\b|\b[a-z]+-[a-z]+\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        // три латинских слова подряд — это английская фраза, а не имя («Kimi Code», «Claude Code»)
        static readonly Regex EnglishRun = new Regex(@"\b[A-Za-z][A-Za-z']+\s+[A-Za-z][A-Za-z']+\s+[A-Za-z][A-Za-z']+\b", RegexOptions.Compiled);

        // секреты и параметры адресов — только вычистка для журнала
        static readonly Regex BearerToken = new Regex(@"\b(Bearer|Basic)\s+\S+", Ci | RegexOptions.Compiled);
        static readonly Regex UrlUserInfo = new Regex(@"(://)[^/\s@""']+@", RegexOptions.Compiled);
        static readonly Regex UrlQuery = new Regex(@"(?<u>\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s?#""'<>)]*)[?#][^\s""'<>)]*", RegexOptions.Compiled);
        static readonly Regex SecretPair = new Regex(@"([""']?\b(?:access_token|refresh_token|id_token|token|api[_-]?key|apikey|secret|password|passwd|authorization|signature)[""']?\s*[=:]\s*)([""']?)[^\s""'&,;)}]+", Ci | RegexOptions.Compiled);
        static readonly Regex LongToken = new Regex(@"(?<![A-Za-z0-9_\-.=+/])(?=[A-Za-z0-9_\-.=+/]*\d)(?=[A-Za-z0-9_\-.=+/]*[A-Za-z])[A-Za-z0-9_\-.=+/]{32,}", RegexOptions.Compiled);
        static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        // Вместо технического пояснения у успешного снимка: сам текст — в widget.log.
        public const string NeutralNote = "Сервис прислал служебное сообщение — подробности в widget.log.";
        public const int MaxVisible = 160;
        public const int MaxLog = 600;

        // Человекочитаемое имя сервиса: заголовок карточки, а если его нет — по идентификатору.
        public static string ServiceName(string id, string title)
        {
            if (!string.IsNullOrEmpty(title) && title.Trim().Length > 0) return title.Trim();
            switch ((id ?? "").ToLowerInvariant())
            {
                case "claude": return "Claude Code";
                case "kimi": return "Kimi";
                case "antigravity": return "Antigravity";
                case "codex": return "Codex";
                default: return string.IsNullOrEmpty(id) ? "сервиса" : id;
            }
        }

        public static FailKind Classify(string technical)
        {
            string t = technical ?? "";
            if (NeedLogin.IsMatch(t)) return FailKind.NeedLogin;
            if (NoLink.IsMatch(t)) return FailKind.NoLink;
            if (RateLimited.IsMatch(t)) return FailKind.RateLimited;
            if (TimedOut.IsMatch(t)) return FailKind.TimedOut;
            return FailKind.Other;
        }

        // Короткая фраза по смыслу технического текста. Код, URL и имена процессов в неё не попадают.
        public static string Humanize(string serviceTitle, string technical)
        {
            return Phrase(serviceTitle, Classify(technical), false);
        }

        // offline — у компьютера нет сети (проверено отдельно): тогда причина любого сбоя, кроме входа, — она.
        public static string Phrase(string serviceTitle, FailKind kind, bool offline)
        {
            string s = string.IsNullOrEmpty(serviceTitle) ? "сервиса" : serviceTitle;
            if (kind == FailKind.NeedLogin) return s + " просит войти заново.";
            if (offline) return "Нет связи с интернетом — " + s + " обновится, как только появится сеть.";
            switch (kind)
            {
                case FailKind.NoLink: return "Нет связи с сервером " + s + " — повторю автоматически.";
                case FailKind.RateLimited: return s + " временно ограничил запросы — повторю позже.";
                case FailKind.TimedOut: return s + " не ответил вовремя — повторю при следующем обновлении.";
                default: return "Не удалось получить данные " + s + " — повторю при следующем обновлении.";
            }
        }

        // Фраза для карточки при сбое: учитывает, есть ли сеть вообще (кэшированная проверка, см. NetState).
        // link — сбой связан с сетью (повторять стоит скоро и сразу после появления сети).
        public static string Failure(string serviceTitle, string technical, out bool link)
        {
            var kind = Classify(technical);
            bool offline = kind != FailKind.NeedLogin && NetState.IsOffline();
            link = offline || kind == FailKind.NoLink;
            return Phrase(serviceTitle, kind, offline);
        }

        // Текст похож на сырой технический вывод (исключение, ответ сервера, stderr CLI) или на английскую фразу
        // и в карточке показываться не должен. Свои сообщения виджета — короткие, по-русски, без адресов и кода.
        public static bool LooksTechnical(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Length > MaxVisible) return true;
            if (text.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
            if (text.IndexOf("ERROR", StringComparison.Ordinal) >= 0) return true;
            if (text.IndexOf("error:", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf("stack", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (text.IndexOf('{') >= 0 || text.IndexOf('}') >= 0) return true;
            if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0) return true;
            if (HttpCode.IsMatch(text) || CliCode.IsMatch(text) || DrivePath.IsMatch(text) || IsoStamp.IsMatch(text) || StackFrame.IsMatch(text)) return true;
            if (CliToken.IsMatch(text) || EnglishRun.IsMatch(text)) return true;
            // латиницы больше, чем кириллицы: вывод CLI с русской приставкой («claude /usage не вернул…: You are…»)
            int latin = 0, cyr = 0;
            foreach (char c in text)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
                else if ((c >= 'Ѐ' && c <= 'ӿ')) cyr++;
            }
            return latin >= 4 && latin > cyr;
        }

        // Для журнала: без секретов и параметров адресов. Строка остаётся читаемой — длину и переводы строк не трогает.
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string t = BearerToken.Replace(text, "$1 …");
            t = UrlUserInfo.Replace(t, "$1");
            t = UrlQuery.Replace(t, "${u}");
            t = SecretPair.Replace(t, "$1$2…");
            t = LongToken.Replace(t, "…");
            return t;
        }

        // Для журнала при сбое: Scrub, одна строка, не длиннее 600 символов.
        public static string Redact(string technical)
        {
            return Redact(technical, MaxLog);
        }

        public static string Redact(string technical, int max)
        {
            string t = Scrub(technical ?? "");
            t = Spaces.Replace(t, " ").Trim();
            if (t.Length > max) t = t.Substring(0, max);
            return t;
        }
    }

    // Есть ли у компьютера сеть. Проверка короткая и кэшируется на 10 секунд: её вызывают только при сбое сервиса,
    // чтобы отличить «нет интернета» от неполадки самого сервиса.
    static class NetState
    {
        static readonly object Gate = new object();
        static DateTime at;
        static bool has, offline;
        public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

        // true — сети нет. Подменяется в тестах: настоящая проверка ходит в сеть.
        internal static Func<bool> Probe = DefaultProbe;
        internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;

        public static bool IsOffline()
        {
            lock (Gate)
            {
                DateTime now = UtcNow();
                if (has && now >= at && now - at < Ttl) return offline;
                bool v;
                try { v = Probe(); }
                catch { v = false; }   // проверка сама сломалась — не выдаём это за отсутствие сети
                offline = v;
                at = now;
                has = true;
                return v;
            }
        }

        // Сеть могла появиться или пропасть — старый ответ недействителен.
        public static void Reset()
        {
            lock (Gate) has = false;
        }

        // Нет ни одного работающего сетевого адаптера — сети нет точно. Иначе короткая проверка TCP до двух публичных
        // адресов (данные не отправляются): адаптер поднят, но интернета может не быть.
        static bool DefaultProbe()
        {
            try { if (!NetworkInterface.GetIsNetworkAvailable()) return true; }
            catch { }
            foreach (string ip in new[] { "1.1.1.1", "8.8.8.8" })
            {
                try
                {
                    using (var c = new TcpClient())
                    {
                        var ar = c.BeginConnect(IPAddress.Parse(ip), 443, null, null);
                        if (ar.AsyncWaitHandle.WaitOne(1500) && c.Connected) return false;
                    }
                }
                catch
                {
                }
            }
            return true;
        }
    }

    // Когда повторять опрос: после сбоя связи — быстро, а не через полный интервал обновления.
    static class RetryPolicy
    {
        public const int LinkRetrySeconds = 60;   // пока связи нет: повтор раз в минуту
        public const int AfterNetworkSeconds = 3; // сеть только что появилась: дать адаптеру получить адрес

        public static int DueSeconds(bool linkFailure, int refreshSeconds)
        {
            return linkFailure ? Math.Min(refreshSeconds, LinkRetrySeconds) : refreshSeconds;
        }
    }
}
