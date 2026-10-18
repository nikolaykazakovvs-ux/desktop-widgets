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

    sealed class ServiceSnapshot
    {
        public string Id;
        public string Title;
        public string Plan;
        public string Url;
        public string Source;         // откуда данные (для подсказки)
        public SvcStatus Status;
        public string StatusText;     // коротко, справа в заголовке сервиса
        public string Message;        // строка-пояснение под полосами
        public DateTime? DataTime;    // момент, к которому относятся данные
        public string ActionId;       // действие, которым пользователь чинит проблему прямо из карточки
        public string ActionLabel;    // подпись кнопки этого действия
        public bool Attention;        // выделить пояснение цветом «внимание»
        public List<ExtraItem> Extras = new List<ExtraItem>();   // плашки эпизодических данных (кредиты, сбросы, лимиты трат)
        public List<LimitBar> Bars = new List<LimitBar>();
        public List<string> Notes = new List<string>();

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["id"] = Id;
            d["title"] = Title;
            d["plan"] = Plan;
            d["source"] = Source;
            d["status"] = Status.ToString();
            d["statusText"] = StatusText;
            d["message"] = Message;
            d["dataTime"] = DataTime;
            d["actionId"] = ActionId;
            d["actionLabel"] = ActionLabel;
            d["attention"] = Attention;
            d["extras"] = Extras.Select(x => (object)x.ToDict()).ToList();
            d["bars"] = Bars.Select(b => (object)b.ToDict()).ToList();
            d["notes"] = Notes.Cast<object>().ToList();
            return d;
        }

        public static ServiceSnapshot FromDict(object d)
        {
            var s = new ServiceSnapshot();
            s.Id = Json.Str(Json.Get(d, "id"));
            s.Title = Json.Str(Json.Get(d, "title"));
            s.Plan = Json.Str(Json.Get(d, "plan"));
            s.Source = Json.Str(Json.Get(d, "source"));
            SvcStatus st;
            if (Enum.TryParse(Json.Str(Json.Get(d, "status")) ?? "", out st)) s.Status = st;
            s.StatusText = Json.Str(Json.Get(d, "statusText"));
            s.Message = Json.Str(Json.Get(d, "message"));
            s.DataTime = Json.Time(Json.Get(d, "dataTime"));
            var extras = Json.Arr(Json.Get(d, "extras"));
            if (extras != null) foreach (var x in extras) s.Extras.Add(ExtraItem.FromDict(x));
            var bars = Json.Arr(Json.Get(d, "bars"));
            if (bars != null) foreach (var b in bars) s.Bars.Add(LimitBar.FromDict(b));
            var notes = Json.Arr(Json.Get(d, "notes"));
            if (notes != null) foreach (var n in notes) { var t = Json.Str(n); if (t != null) s.Notes.Add(t); }
            return s;
        }
    }

    sealed class ServiceConfig
    {
        public string Id;
        public bool Enabled = false; // Public distribution: providers are opt-in.
        public string Title;
        public string Plan;
        public string Url;
        public string Command;   // путь к CLI, если его нет в PATH
        public string BaseUrl;
        public int TimeoutSeconds = 45;
        public int MaxProcesses;   // 0 — без лимита; иначе CLI и все его потомки в Job с лимитом процессов
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
            switch (id)
            {
                case "claude": s.Title = "Claude Code"; s.Url = "https://claude.ai/settings/usage"; break;
                case "kimi": s.Title = "Kimi"; s.BaseUrl = "https://api.kimi.com/coding/v1"; s.TimeoutSeconds = 10; break;
                case "antigravity": s.Title = "Antigravity"; s.TimeoutSeconds = 30; s.MaxProcesses = 1; break;
                case "codex": s.Title = "GPT / Codex"; s.Url = "https://chatgpt.com/codex/settings/usage"; s.TimeoutSeconds = 30; break;
            }
            return s;
        }

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

    // Состояние окна и последние удачные снимки (без токенов) — state.json рядом с exe.
    sealed class State
    {
        static readonly object SaveGate = new object();
        readonly object gate = new object();
        readonly Dictionary<string, object> cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
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
            var c = Json.Obj(Json.Get(root, "cache"));
            if (c != null) foreach (var kv in c) if (kv.Value != null) s.cache[kv.Key] = kv.Value;
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

        public void PutCache(ServiceSnapshot snap)
        {
            lock (gate) cache[snap.Id] = snap.ToDict();
        }

        public ServiceSnapshot GetCache(string id)
        {
            lock (gate)
            {
                object d;
                return cache.TryGetValue(id, out d) ? ServiceSnapshot.FromDict(d) : null;
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

