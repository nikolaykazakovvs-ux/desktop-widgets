using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LimitsWidget
{
    enum SvcStatus { Ok, Stale, NoData, NeedsPermission, Error, Fixture }

    sealed class LimitBar
    {
        public QuotaObservation Quota; // typed subscription facts; independent of UI labels/visual reset
        public string Chip;          // тип окна: "5 ч", "7 д", "мес"
        public string Label;         // подпись: "Неделя · все модели"
        public double? Used;         // израсходовано, 0..100; null — нет данных
        public DateTime? ResetsAt;   // локальное время сброса
        public string Note;          // доп. подпись под полосой
        public string Tip;           // всплывающая подсказка
        public bool Placeholder;
        public List<ExtraPart> Parts;   // разбивка заливки на цветные доли (в процентах лимита); может быть null

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["quota"] = Quota == null ? null : Quota.ToDict();
            d["chip"] = Chip;
            d["label"] = Label;
            d["used"] = Used;
            d["resetsAt"] = ResetsAt;
            d["note"] = Note;
            d["tip"] = Tip;
            d["placeholder"] = Placeholder;
            d["parts"] = Parts != null ? Parts.Select(p => (object)p.ToDict()).ToList() : null;
            return d;
        }

        public static LimitBar FromDict(object d)
        {
            var b = new LimitBar();
            b.Quota = QuotaObservation.FromDict(Json.Get(d, "quota"));
            b.Chip = Json.Str(Json.Get(d, "chip"));
            b.Label = Json.Str(Json.Get(d, "label"));
            b.Used = Json.Num(Json.Get(d, "used"));
            b.ResetsAt = Json.Time(Json.Get(d, "resetsAt"));
            b.Note = Json.Str(Json.Get(d, "note"));
            b.Tip = Json.Str(Json.Get(d, "tip"));
            b.Placeholder = Json.Bool(Json.Get(d, "placeholder")) == true;
            var parts = Json.Arr(Json.Get(d, "parts"));
            if (parts != null)
            {
                b.Parts = new List<ExtraPart>();
                foreach (var p in parts) b.Parts.Add(ExtraPart.FromDict(p));
            }
            return b;
        }
    }

    // Часть разбивки (например, доля продукта в недельном расходе).
    sealed class ExtraPart
    {
        public string Key;
        public string Name;
        public double Percent;

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["key"] = Key;
            d["name"] = Name;
            d["percent"] = Percent;
            return d;
        }

        public static ExtraPart FromDict(object d)
        {
            var p = new ExtraPart();
            p.Key = Json.Str(Json.Get(d, "key"));
            p.Name = Json.Str(Json.Get(d, "name"));
            p.Percent = Json.Num(Json.Get(d, "percent")) ?? 0;
            return p;
        }
    }

    // Плашка эпизодических данных под полосами: кредиты, сбросы, лимит трат, расход по продуктам.
    sealed class ExtraItem
    {
        public string Icon;      // глиф Segoe Fluent Icons; может быть null
        public string Label;     // "Кредиты"
        public string Value;     // крупно справа; может быть null
        public string Detail;    // строка под ним; может быть null
        public string Tip;       // всплывающая подсказка
        public string Accent;    // "info" | "warn" | "ok" | "danger" | "neutral"; null = "info"
        public double? Fill;     // 0..1 — доля израсходованного, когда есть знаменатель
        public List<ExtraPart> Parts;   // разбивка; может быть null
        public DateTime? ExpiresAt;     // срок годности плашки: после него она не рисуется (null — бессрочная)

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["icon"] = Icon;
            d["label"] = Label;
            d["value"] = Value;
            d["detail"] = Detail;
            d["tip"] = Tip;
            d["accent"] = Accent;
            d["fill"] = Fill;
            d["expiresAt"] = ExpiresAt;
            d["parts"] = Parts != null ? Parts.Select(p => (object)p.ToDict()).ToList() : null;
            return d;
        }

        public static ExtraItem FromDict(object d)
        {
            var e = new ExtraItem();
            e.Icon = Json.Str(Json.Get(d, "icon"));
            e.Label = Json.Str(Json.Get(d, "label"));
            e.Value = Json.Str(Json.Get(d, "value"));
            e.Detail = Json.Str(Json.Get(d, "detail"));
            e.Tip = Json.Str(Json.Get(d, "tip"));
            e.Accent = Json.Str(Json.Get(d, "accent"));
            e.Fill = Json.Num(Json.Get(d, "fill"));
            e.ExpiresAt = Json.Time(Json.Get(d, "expiresAt"));
            var parts = Json.Arr(Json.Get(d, "parts"));
            if (parts != null)
            {
                e.Parts = new List<ExtraPart>();
                foreach (var p in parts) e.Parts.Add(ExtraPart.FromDict(p));
            }
            return e;
        }
    }

    // Дневной расход токенов Codex (account/usage/read): дата бакета и число токенов; null — значения нет (не ноль).
    sealed class DailyTokens
    {
        public DateTime Date;     // календарная дата бакета (startDate), без времени
        public long? Tokens;

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> { { "date", Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }, { "tokens", Tokens } };
        }

        public static DailyTokens FromDict(object d)
        {
            DateTime date;
            if (!TokenActivity.TryDate(Json.Str(Json.Get(d, "date")), out date)) return null;
            return new DailyTokens { Date = date, Tokens = TokenActivity.Long(Json.Get(d, "tokens")) };
        }
    }

    // Статистика использования Codex — отдельный факт со своим временем; в квоты, кредиты и телеметрию не попадает.
    sealed class TokenActivity
    {
        public DateTime? MeasuredAt;   // UTC: завершение account/usage/read
        public bool Stale;             // последний запрос статистики не удался — показан прошлый ответ
        public List<DailyTokens> Days; // null — сервер не дал дневных значений
        public long? LifetimeTokens, PeakDailyTokens, CurrentStreakDays, LongestStreakDays, LongestRunningTurnSec;

        public static long? Long(object node)
        {
            double? v = Json.Num(node);
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value) || v.Value < 0 || v.Value > 9e15) return null;
            return (long)Math.Round(v.Value);
        }

        public static bool TryDate(string s, out DateTime date)
        {
            date = default(DateTime);
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length > 10) s = s.Substring(0, 10);
            return DateTime.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);
        }

        // Ответ account/usage/read (GetAccountTokenUsageResponse); null — ответ не распознан.
        public static TokenActivity Parse(object result, DateTime measuredUtc)
        {
            if (Json.Obj(result) == null) return null;
            var summary = Json.Get(result, "summary");
            var buckets = Json.Get(result, "dailyUsageBuckets");
            if (Json.Obj(summary) == null && buckets == null) return null;
            var a = new TokenActivity { MeasuredAt = measuredUtc };
            a.LifetimeTokens = Long(Json.Get(summary, "lifetimeTokens"));
            a.PeakDailyTokens = Long(Json.Get(summary, "peakDailyTokens"));
            a.CurrentStreakDays = Long(Json.Get(summary, "currentStreakDays"));
            a.LongestStreakDays = Long(Json.Get(summary, "longestStreakDays"));
            a.LongestRunningTurnSec = Long(Json.Get(summary, "longestRunningTurnSec"));
            var arr = Json.Arr(buckets);
            if (arr != null)
            {
                var byDate = new SortedDictionary<DateTime, long?>();
                foreach (var b in arr)
                {
                    DateTime date;
                    if (!TryDate(Json.Str(Json.Get(b, "startDate")), out date)) continue;
                    long? t = Long(Json.Get(b, "tokens"));
                    long? prev;
                    // повтор даты — сумма известных значений; неизвестное к известному не добавляет ноль
                    if (byDate.TryGetValue(date, out prev)) byDate[date] = prev.HasValue && t.HasValue ? prev + t : prev ?? t;
                    else byDate[date] = t;
                }
                a.Days = byDate.Select(kv => new DailyTokens { Date = kv.Key, Tokens = kv.Value }).ToList();
            }
            return a;
        }

        // Семь календарных дней, оканчивающихся сегодняшним (или последним днём ответа, если он позже): пропуск — null.
        public List<DailyTokens> LastWeek(DateTime today)
        {
            if (Days == null) return null;
            DateTime end = today.Date;
            foreach (var d in Days) if (d.Date > end) end = d.Date;
            var res = new List<DailyTokens>();
            for (int i = 6; i >= 0; i--)
            {
                DateTime day = end.AddDays(-i);
                var hit = Days.FirstOrDefault(x => x.Date == day);
                res.Add(new DailyTokens { Date = day, Tokens = hit != null ? hit.Tokens : null });
            }
            return res;
        }

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["measuredAt"] = MeasuredAt;
            d["stale"] = Stale;
            d["days"] = Days != null ? Days.Select(x => (object)x.ToDict()).ToList() : null;
            d["lifetimeTokens"] = LifetimeTokens;
            d["peakDailyTokens"] = PeakDailyTokens;
            d["currentStreakDays"] = CurrentStreakDays;
            d["longestStreakDays"] = LongestStreakDays;
            d["longestRunningTurnSec"] = LongestRunningTurnSec;
            return d;
        }

        public static TokenActivity FromDict(object d)
        {
            if (Json.Obj(d) == null) return null;
            var a = new TokenActivity();
            a.MeasuredAt = Fmt.Utc(Json.Time(Json.Get(d, "measuredAt")));
            a.Stale = Json.Bool(Json.Get(d, "stale")) == true;
            var days = Json.Arr(Json.Get(d, "days"));
            if (days != null)
            {
                a.Days = new List<DailyTokens>();
                foreach (var x in days) { var day = DailyTokens.FromDict(x); if (day != null) a.Days.Add(day); }
            }
            a.LifetimeTokens = Long(Json.Get(d, "lifetimeTokens"));
            a.PeakDailyTokens = Long(Json.Get(d, "peakDailyTokens"));
            a.CurrentStreakDays = Long(Json.Get(d, "currentStreakDays"));
            a.LongestStreakDays = Long(Json.Get(d, "longestStreakDays"));
            a.LongestRunningTurnSec = Long(Json.Get(d, "longestRunningTurnSec"));
            return a;
        }
    }

    sealed class ServiceSnapshot
    {
        public DateTime? LastAttemptAt;   // UTC: завершение последней попытки опроса
        public bool ObservationTimeUnknown;
        public string Id;
        public string Title;
        public string Plan;           // показанное имя тарифа: вручную > подтверждённое поле провайдера > нет
        public string PlanSource;     // "manual" | "provider" | null (неизвестно)
        public string AutoPlan;       // тариф из подтверждённого поля провайдера (для диалога «Подписки»)
        public string AutoPlanSource; // какое поле провайдера его подтвердило
        public bool AutoPlanIsLevel;  // AutoPlan — уровень аккаунта (Kimi userLevelName), а не подтверждённый оплаченный тариф
        public string Url;            // страница лимитов
        public string ProfileUrl;     // страница профиля/подписки (аккаунт, оплата) — отдельно от лимитов
        public string Source;         // откуда данные (для подсказки)
        public SvcStatus Status;
        public string StatusText;     // коротко, справа в заголовке сервиса
        public string Message;        // строка-пояснение под полосами
        public DateTime? DataTime;    // UTC: момент, к которому относятся данные
        public string ActionId;       // действие, которым пользователь чинит проблему прямо из карточки
        public string ActionLabel;    // подпись кнопки этого действия
        public bool Attention;        // выделить пояснение цветом «внимание»
        public TokenActivity Activity;   // статистика использования (Codex); null — нет
        public long Generation;       // номер опроса (только в памяти): поздний результат старого опроса не затирает новый
        public bool Partial;          // ранний результат: основные квоты готовы, дополнительные сведения ещё догружаются
        public List<ExtraItem> Extras = new List<ExtraItem>();   // плашки эпизодических данных (кредиты, сбросы, лимиты трат)
        public List<LimitBar> Bars = new List<LimitBar>();
        public List<string> Notes = new List<string>();

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["lastAttemptAt"] = LastAttemptAt;
            d["observationTimeUnknown"] = ObservationTimeUnknown;
            d["id"] = Id;
            d["title"] = Title;
            d["plan"] = Plan;
            d["planSource"] = PlanSource;
            d["autoPlan"] = AutoPlan;
            d["autoPlanSource"] = AutoPlanSource;
            d["autoPlanIsLevel"] = AutoPlanIsLevel;
            d["url"] = Url;
            d["profileUrl"] = ProfileUrl;
            d["source"] = Source;
            d["status"] = Status.ToString();
            d["statusText"] = StatusText;
            d["message"] = Message;
            d["dataTime"] = DataTime;
            d["actionId"] = ActionId;
            d["actionLabel"] = ActionLabel;
            d["attention"] = Attention;
            d["activity"] = Activity != null ? Activity.ToDict() : null;
            d["extras"] = Extras.Select(x => (object)x.ToDict()).ToList();
            d["bars"] = Bars.Select(b => (object)b.ToDict()).ToList();
            d["notes"] = Notes.Cast<object>().ToList();
            return d;
        }

        public static ServiceSnapshot FromDict(object d)
        {
            var s = new ServiceSnapshot();
            s.LastAttemptAt = Fmt.Utc(Json.Time(Json.Get(d, "lastAttemptAt")));
            s.ObservationTimeUnknown = Json.Bool(Json.Get(d, "observationTimeUnknown")) == true;
            s.Id = Json.Str(Json.Get(d, "id"));
            s.Title = Json.Str(Json.Get(d, "title"));
            s.Plan = Json.Str(Json.Get(d, "plan"));
            s.PlanSource = Json.Str(Json.Get(d, "planSource"));
            s.AutoPlan = Json.Str(Json.Get(d, "autoPlan"));
            s.AutoPlanSource = Json.Str(Json.Get(d, "autoPlanSource"));
            s.AutoPlanIsLevel = Json.Bool(Json.Get(d, "autoPlanIsLevel")) == true;
            s.Url = Json.Str(Json.Get(d, "url"));
            s.ProfileUrl = Json.Str(Json.Get(d, "profileUrl"));
            s.Source = Json.Str(Json.Get(d, "source"));
            SvcStatus st;
            if (Enum.TryParse(Json.Str(Json.Get(d, "status")) ?? "", out st)) s.Status = st;
            s.StatusText = Json.Str(Json.Get(d, "statusText"));
            s.Message = Json.Str(Json.Get(d, "message"));
            s.DataTime = Fmt.Utc(Json.Time(Json.Get(d, "dataTime")));
            s.ActionId = Json.Str(Json.Get(d, "actionId"));
            s.ActionLabel = Json.Str(Json.Get(d, "actionLabel"));
            s.Attention = Json.Bool(Json.Get(d, "attention")) == true;
            s.Activity = TokenActivity.FromDict(Json.Get(d, "activity"));
            var extras = Json.Arr(Json.Get(d, "extras"));
            if (extras != null) foreach (var x in extras) s.Extras.Add(ExtraItem.FromDict(x));
            var bars = Json.Arr(Json.Get(d, "bars"));
            if (bars != null) foreach (var b in bars) s.Bars.Add(LimitBar.FromDict(b));
            var notes = Json.Arr(Json.Get(d, "notes"));
            if (notes != null) foreach (var n in notes) { var t = Json.Str(n); if (t != null) s.Notes.Add(t); }
            return s;
        }

        // Независимая копия для передачи между потоками (UI, кэш, телеметрия) — поздние правки сборщика её не трогают.
        public ServiceSnapshot Clone()
        {
            var c = FromDict(Json.Parse(Json.Serialize(ToDict(), false)));
            c.Generation = Generation;
            c.Partial = Partial;
            return c;
        }
    }

    // Последняя попытка опроса — отдельно от последнего удачного снимка (cache), чтобы состояние не выдавало старую удачу за новую.
    sealed class AttemptRecord
    {
        public DateTime? At;          // UTC
        public string Status, StatusText, Message, ActionId, ActionLabel;
        public bool Attention;

        public static AttemptRecord Of(ServiceSnapshot s)
        {
            return new AttemptRecord { At = s.LastAttemptAt, Status = s.Status.ToString(), StatusText = s.StatusText, Message = s.Message,
                ActionId = s.ActionId, ActionLabel = s.ActionLabel, Attention = s.Attention };
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> { { "at", At }, { "status", Status }, { "statusText", StatusText }, { "message", Message },
                { "actionId", ActionId }, { "actionLabel", ActionLabel }, { "attention", Attention } };
        }

        public static AttemptRecord FromDict(object d)
        {
            if (Json.Obj(d) == null) return null;
            return new AttemptRecord { At = Fmt.Utc(Json.Time(Json.Get(d, "at"))), Status = Json.Str(Json.Get(d, "status")),
                StatusText = Json.Str(Json.Get(d, "statusText")), Message = Json.Str(Json.Get(d, "message")),
                ActionId = Json.Str(Json.Get(d, "actionId")), ActionLabel = Json.Str(Json.Get(d, "actionLabel")),
                Attention = Json.Bool(Json.Get(d, "attention")) == true };
        }
    }

    // Имя тарифа: ручное (config.json services.<id>.plan, прежний ключ) > подтверждённое поле провайдера > неизвестно.
    static class PlanResolver
    {
        public static void Apply(ServiceSnapshot snap, ServiceConfig svc)
        {
            string manual = svc != null && !string.IsNullOrWhiteSpace(svc.Plan) ? svc.Plan.Trim() : null;
            if (manual != null) { snap.Plan = manual; snap.PlanSource = "manual"; }
            else if (!string.IsNullOrWhiteSpace(snap.AutoPlan)) { snap.Plan = snap.AutoPlan; snap.PlanSource = "provider"; }
            else { snap.Plan = null; snap.PlanSource = null; }
            if (svc != null)
            {
                snap.Url = SafeUrl(svc.Url);
                snap.ProfileUrl = SafeUrl(svc.ProfileUrl);
            }
        }

        public static string Describe(ServiceSnapshot s)
        {
            if (s.PlanSource == "manual") return "Тариф указан вручную (меню → «Подписки…»)"
                + (string.IsNullOrEmpty(s.AutoPlan) ? "" : (s.AutoPlanIsLevel ? ". Уровень аккаунта у провайдера: " : ". Провайдер сообщает: ") + s.AutoPlan);
            if (s.PlanSource == "provider" && s.AutoPlanIsLevel)
                return "Уровень аккаунта: " + s.Plan + " (" + (s.AutoPlanSource ?? "профиль провайдера") + "). Это уровень профиля, а не подтверждение оплаченного тарифа; тариф можно указать вручную в меню → «Подписки…»";
            if (s.PlanSource == "provider") return "Тариф определён автоматически: " + (s.AutoPlanSource ?? "поле провайдера");
            return "Тариф неизвестен: провайдер не сообщает его подтверждённым полем. Укажите вручную в меню → «Подписки…»";
        }

        // Ссылки открываются только по https.
        public static string SafeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            Uri u;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host)) return null;
            return u.AbsoluteUri;
        }
    }

    sealed class ServiceConfig
    {
        public string Id;
        public bool Enabled = false; // Public distribution: providers are opt-in.
        public string Title;
        public string Plan;        // ручное имя тарифа (прежний ключ plan сохраняет смысл)
        public string Url;         // страница лимитов
        public string ProfileUrl;  // страница профиля/подписки
        public string Command;   // путь к CLI, если его нет в PATH
        public string BaseUrl;
        public int TimeoutSeconds = 45;
        public int MaxProcesses;   // 0 — без лимита; иначе CLI и все его потомки в Job с лимитом процессов
        public bool StartServer;   // Kimi: запускать свой скрытый `kimi web --no-open`, если готового сервера нет
    }

    sealed class Config
    {
        public int RefreshSeconds = 300;
        public bool AllowLocalTokens;
        public string Hotkey = "Ctrl+Alt+L";
        public List<string> Order = new List<string> { "claude", "kimi", "antigravity", "codex" };
        public string FilePath;
        DateTime loadedStamp;
        readonly Dictionary<string, ServiceConfig> services = new Dictionary<string, ServiceConfig>(StringComparer.OrdinalIgnoreCase);

        public ServiceConfig Svc(string id)
        {
            ServiceConfig s;
            if (!services.TryGetValue(id, out s))
            {
                s = Defaults(id);
                services[id] = s;
            }
            return s;
        }

        static ServiceConfig Defaults(string id)
        {
            var s = new ServiceConfig { Id = id, Title = id };
            // ProfileUrl — страницы аккаунта/оплаты, а не продаж; если такая страница не подтверждена, ссылки нет (задаётся вручную).
            switch (id)
            {
                case "claude": s.Title = "Claude Code"; s.Url = "https://claude.ai/settings/usage"; s.ProfileUrl = "https://claude.ai/settings/billing"; break;
                // страница подписки из официальной справки Kimi (help/membership/membership-account-query → «membership subscription page»);
                // другой регион без подтверждённого адреса — через services.kimi.profileUrl, домены не подставляются
                case "kimi": s.Title = "Kimi"; s.BaseUrl = "https://api.kimi.com/coding/v1"; s.TimeoutSeconds = 10; s.StartServer = true; s.ProfileUrl = "https://www.kimi.com/membership/subscription"; break;
                case "antigravity": s.Title = "Antigravity"; s.TimeoutSeconds = 30; s.MaxProcesses = 1; s.ProfileUrl = "https://myaccount.google.com/payments-and-subscriptions"; break;
                case "codex": s.Title = "GPT / Codex"; s.Url = "https://chatgpt.com/codex/settings/usage"; s.ProfileUrl = "https://chatgpt.com/#settings/Account"; s.TimeoutSeconds = 30; break;
            }
            return s;
        }

        public IEnumerable<string> KnownIds
        {
            get
            {
                var ids = new List<string>(Order);
                foreach (var id in new[] { "claude", "kimi", "antigravity", "codex" }) if (!ids.Contains(id)) ids.Add(id);
                return ids;
            }
        }

        // Стамп файла, по которому кэши (например, найденные CLI) понимают, что настройки поменялись.
        public DateTime LoadedStamp { get { return loadedStamp; } }

        public static Config Load(string path)
        {
            var c = new Config();
            c.FilePath = path;
            c.Reload();
            return c;
        }

        public void ReloadIfChanged()
        {
            if (FilePath == null) return;
            DateTime stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            if (stamp != loadedStamp) Reload();
        }

        void Reload()
        {
            RefreshSeconds = 300;
            AllowLocalTokens = false;
            Hotkey = "Ctrl+Alt+L";
            Order = new List<string> { "claude", "kimi", "antigravity", "codex" };
            services.Clear();
            foreach (var id in Order) Svc(id);
            loadedStamp = FilePath != null && File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            if (FilePath == null || !File.Exists(FilePath)) return;

            var root = Json.Obj(Json.TryParse(File.ReadAllText(FilePath, Encoding.UTF8)));
            if (root == null)
            {
                Log.Write("config.json не разобран — взяты значения по умолчанию");
                return;
            }
            var n = Json.Num(Json.Get(root, "refreshSeconds"));
            if (n.HasValue) RefreshSeconds = Math.Max(60, (int)n.Value);
            AllowLocalTokens = Json.Bool(Json.Get(root, "allowLocalTokens")) == true;
            var hk = Json.Get(root, "hotkey");
            if (hk != null) Hotkey = Json.Str(hk) ?? "";
            var order = Json.Arr(Json.Get(root, "order"));
            if (order != null)
            {
                var list = order.Select(x => Json.Str(x)).Where(x => !string.IsNullOrEmpty(x)).ToList();
                if (list.Count > 0) Order = list;
            }
            var svcs = Json.Obj(Json.Get(root, "services"));
            if (svcs == null) return;
            foreach (var kv in svcs)
            {
                var o = Json.Obj(kv.Value);
                if (o == null) continue;
                var s = Svc(kv.Key);
                var b = Json.Bool(Json.Get(o, "enabled"));
                if (b.HasValue) s.Enabled = b.Value;
                var str = Json.Str(Json.Get(o, "title"));
                if (!string.IsNullOrEmpty(str)) s.Title = str;
                str = Json.Str(Json.Get(o, "plan"));
                if (str != null) s.Plan = str;
                str = Json.Str(Json.Get(o, "url"));
                if (str != null) s.Url = str;
                str = Json.Str(Json.Get(o, "profileUrl"));
                if (str != null) s.ProfileUrl = str;
                b = Json.Bool(Json.Get(o, "startServer"));
                if (b.HasValue) s.StartServer = b.Value;
                str = Json.Str(Json.Get(o, "command"));
                if (str != null) s.Command = str;
                str = Json.Str(Json.Get(o, "baseUrl"));
                if (!string.IsNullOrEmpty(str)) s.BaseUrl = str;
                n = Json.Num(Json.Get(o, "timeoutSeconds"));
                if (n.HasValue) s.TimeoutSeconds = Math.Max(5, (int)n.Value);
                n = Json.Num(Json.Get(o, "maxProcesses"));
                if (n.HasValue) s.MaxProcesses = Math.Max(0, (int)n.Value);
            }
        }
    }

    // Точечная правка config.json: меняются только названные ключи, остальные поля (в том числе неизвестные этой версии) сохраняются.
    static class ConfigFile
    {
        static readonly object Gate = new object();

        // false — файл есть, но не разобран: такой файл не перезаписываем, чтобы не потерять настройки пользователя.
        public static bool Update(string path, Action<IDictionary<string, object>> mutate)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (Gate)
            {
                IDictionary<string, object> root;
                if (File.Exists(path))
                {
                    root = Json.Obj(Json.TryParse(File.ReadAllText(path, Encoding.UTF8)));
                    if (root == null) return false;
                }
                else root = new Dictionary<string, object>();
                mutate(root);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(root, true) + "\n", new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
        }

        public static IDictionary<string, object> Service(IDictionary<string, object> root, string id)
        {
            var svcs = Json.Obj(root.ContainsKey("services") ? root["services"] : null);
            if (svcs == null) { svcs = new Dictionary<string, object>(); root["services"] = svcs; }
            // ключ сервиса без учёта регистра — как читает Config
            string key = svcs.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase)) ?? id;
            var o = Json.Obj(svcs.ContainsKey(key) ? svcs[key] : null);
            if (o == null) { o = new Dictionary<string, object>(); svcs[key] = o; }
            return o;
        }

        // null или пустая строка — убрать ключ (вернуться к автоматическому значению).
        public static void Set(IDictionary<string, object> o, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) o.Remove(key);
            else o[key] = value.Trim();
        }
    }

    // Состояние окна и последние удачные снимки (без токенов) — state.json рядом с exe.
    sealed class State
    {
        static readonly object SaveGate = new object();
        static readonly string[] KnownKeys = { "left", "top", "topmost", "hidden", "locked", "compact", "autostartInitialized", "trayPromoted",
            "theme", "bgOpacity", "scale", "cardOrder", "cache", "creditLog", "attempts", "activityExpanded", "stateVersion" };
        readonly object gate = new object();
        readonly Dictionary<string, object> cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> cacheGeneration = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        // последняя попытка опроса по сервисам — отдельно от последнего удачного снимка
        readonly Dictionary<string, AttemptRecord> attempts = new Dictionary<string, AttemptRecord>(StringComparer.OrdinalIgnoreCase);
        // поля state.json, неизвестные этой версии (например, от более новой): сохраняются как есть
        readonly Dictionary<string, object> unknown = new Dictionary<string, object>(StringComparer.Ordinal);
        public bool ActivityExpanded;   // развёрнута ли подробная статистика Codex
        // история остатка кредитов по сервисам: { unixSeconds, balance }; только под gate
        readonly Dictionary<string, List<double[]>> creditLog = new Dictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);

        public string FilePath;
        public double? Left, Top;
        public bool Topmost, Locked, Compact;
        public bool Hidden;   // карточка свёрнута в трей (окно скрыто)
        public bool AutostartInitialized, TrayPromoted;   // однократные действия первого запуска
        public string Theme = "auto";
        public double Opacity = 1.0;   // непрозрачность фона карточки (текст и полосы всегда непрозрачны)
        public double Scale = 1.0;
        public List<string> CardOrder = new List<string>();   // ручной порядок карточек нейросетей (id); пусто — порядок из config.json
        public const double OpacityMin = 0.1, ScaleMin = 0.6, ScaleMax = 2.0;

        public static State Load(string path)
        {
            var s = new State();
            s.FilePath = path;
            if (path == null || !File.Exists(path)) return s;
            var root = Json.Obj(Json.TryParse(File.ReadAllText(path, Encoding.UTF8)));
            if (root == null) return s;
            s.Left = Json.Num(Json.Get(root, "left"));
            s.Top = Json.Num(Json.Get(root, "top"));
            s.Topmost = Json.Bool(Json.Get(root, "topmost")) == true;
            s.Hidden = Json.Bool(Json.Get(root, "hidden")) == true;
            s.Locked = Json.Bool(Json.Get(root, "locked")) == true;
            s.Compact = Json.Bool(Json.Get(root, "compact")) == true;
            s.AutostartInitialized = Json.Bool(Json.Get(root, "autostartInitialized")) == true;
            s.TrayPromoted = Json.Bool(Json.Get(root, "trayPromoted")) == true;
            var th = Json.Str(Json.Get(root, "theme"));
            if (th == "dark" || th == "light" || th == "auto") s.Theme = th;
            // прежний ключ "opacity" гасил всю карточку и не учитывается: новая настройка стартует со 100 %
            var op = Json.Num(Json.Get(root, "bgOpacity"));
            if (op.HasValue) s.Opacity = Math.Min(1, Math.Max(OpacityMin, op.Value));
            var sc = Json.Num(Json.Get(root, "scale"));
            if (sc.HasValue) s.Scale = Math.Min(ScaleMax, Math.Max(ScaleMin, sc.Value));
            var ord = Json.Arr(Json.Get(root, "cardOrder"));
            if (ord != null)
                foreach (var o in ord)
                {
                    string id = Json.Str(o);
                    if (!string.IsNullOrEmpty(id) && !s.CardOrder.Contains(id)) s.CardOrder.Add(id);
                }
            s.ActivityExpanded = Json.Bool(Json.Get(root, "activityExpanded")) == true;
            foreach (var kv in root)
                if (Array.IndexOf(KnownKeys, kv.Key) < 0) s.unknown[kv.Key] = kv.Value;
            var c = Json.Obj(Json.Get(root, "cache"));
            if (c != null) foreach (var kv in c) if (kv.Value != null) s.cache[kv.Key] = kv.Value;
            var at = Json.Obj(Json.Get(root, "attempts"));
            if (at != null)
                foreach (var kv in at)
                {
                    var rec = AttemptRecord.FromDict(kv.Value);
                    if (rec != null) s.attempts[kv.Key] = rec;
                }
            var log = Json.Obj(Json.Get(root, "creditLog"));
            if (log != null)
                foreach (var kv in log)
                {
                    var arr = Json.Arr(kv.Value);
                    if (arr == null) continue;
                    var list = new List<double[]>();
                    foreach (var pair in arr)
                    {
                        var p = Json.Arr(pair);
                        if (p == null || p.Count < 2) continue;
                        double? tt = Json.Num(p[0]), bb = Json.Num(p[1]);
                        if (tt.HasValue && bb.HasValue && tt.Value > 0 && tt.Value < CreditStats.Unix(DateTime.UtcNow) + 86400
                            && !double.IsNaN(bb.Value) && !double.IsInfinity(bb.Value)) list.Add(new[] { tt.Value, bb.Value });
                    }
                    list.Sort((x, y) => x[0].CompareTo(y[0]));
                    s.creditLog[kv.Key] = list;
                }
            return s;
        }

        // Последний удачный снимок. Результат более старого опроса (поздняя догрузка) не затирает более новый.
        public bool PutCache(ServiceSnapshot snap)
        {
            var d = snap.ToDict();
            lock (gate)
            {
                long g;
                if (snap.Generation > 0 && cacheGeneration.TryGetValue(snap.Id, out g) && g > snap.Generation) return false;
                cache[snap.Id] = d;
                if (snap.Generation > 0) cacheGeneration[snap.Id] = snap.Generation;
                return true;
            }
        }

        public ServiceSnapshot GetCache(string id)
        {
            lock (gate)
            {
                object d;
                return cache.TryGetValue(id, out d) ? ServiceSnapshot.FromDict(d) : null;
            }
        }

        public void PutAttempt(ServiceSnapshot snap)
        {
            var rec = AttemptRecord.Of(snap);
            lock (gate) attempts[snap.Id] = rec;
        }

        public AttemptRecord GetAttempt(string id)
        {
            lock (gate)
            {
                AttemptRecord r;
                return attempts.TryGetValue(id, out r) ? r : null;
            }
        }

        // Точка истории остатка: пишем при изменении (>= 0.5) или не реже раза в 10 минут; хранится 8 суток, не больше 3000 точек.
        public void AddCreditSample(string id, DateTime now, double balance)
        {
            double t = CreditStats.Unix(now);
            lock (gate)
            {
                List<double[]> list;
                if (!creditLog.TryGetValue(id, out list)) { list = new List<double[]>(); creditLog[id] = list; }
                list.RemoveAll(x => x[0] > t); // точки из будущего (часы переводили) не должны блокировать запись
                if (list.Count == 0 || Math.Abs(balance - list[list.Count - 1][1]) >= 0.5 || t - list[list.Count - 1][0] >= 600)
                    list.Add(new[] { t, balance });
                list.RemoveAll(x => x[0] < t - 8 * 86400);
                if (list.Count > 3000) list.RemoveRange(0, list.Count - 3000);
            }
        }

        public List<double[]> CreditSamples(string id)
        {
            lock (gate)
            {
                List<double[]> list;
                return creditLog.TryGetValue(id, out list) ? new List<double[]>(list) : new List<double[]>();
            }
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            string json;
            lock (gate)
            {
                var d = new Dictionary<string, object>();
                foreach (var kv in unknown) d[kv.Key] = kv.Value;
                d["stateVersion"] = 2;   // 2: attempts (последняя попытка) отдельно от cache (последний удачный снимок)
                d["left"] = Left;
                d["top"] = Top;
                d["topmost"] = Topmost;
                d["hidden"] = Hidden;
                d["locked"] = Locked;
                d["compact"] = Compact;
                d["autostartInitialized"] = AutostartInitialized;
                d["trayPromoted"] = TrayPromoted;
                d["theme"] = Theme;
                d["bgOpacity"] = Opacity;
                d["scale"] = Scale;
                d["cardOrder"] = CardOrder.Select(x => (object)x).ToList();
                d["cache"] = new Dictionary<string, object>(cache);
                d["attempts"] = attempts.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToDict());
                d["activityExpanded"] = ActivityExpanded;
                var log = new Dictionary<string, object>();
                foreach (var kv in creditLog)
                    log[kv.Key] = kv.Value.Select(x => (object)new object[] { x[0], x[1] }).ToList();
                d["creditLog"] = log;
                json = Json.Serialize(d, true);
            }
            lock (SaveGate)
            {
                try
                {
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);
                }
                catch (Exception ex)
                {
                    Log.Write("state.json: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }
}

