using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ResourcesWidget
{
    sealed class CpuInfo
    {
        public string Name;
        public int Logical;         // логических процессоров
        public int BaseMhz;         // паспортная частота
        public double Total;        // загрузка, 0..100
        public double[] Cores;      // загрузка по логическим процессорам
        public double MaxCore;      // самый загруженный поток
        public double? Mhz;         // текущая частота (как «Скорость» в диспетчере задач)
        public double? MaxMhz;      // наибольшая частота среди потоков
        public double? Temp;        // температура, °C; null — ни одна программа мониторинга не делится ею
        public string TempSource;   // откуда температура: "HWiNFO · CPU (Tctl/Tdie)"
    }

    sealed class MemInfo
    {
        public long Total, Available, Cache;
        public long CommitTotal, CommitLimit;
        public int Processes, Threads, Handles;

        public long Used { get { return Total - Available; } }
        public double Percent { get { return Total > 0 ? 100.0 * Used / Total : 0; } }
    }

    sealed class GpuInfo
    {
        public string Name;             // "RTX 3070", "Radeon RX 6800 XT"
        public string FullName;         // как называет драйвер: "NVIDIA GeForce RTX 3070"
        public string Vendor;           // NVIDIA, AMD, Intel…
        public string Source;           // откуда показания: "NVML (драйвер NVIDIA)", "Windows (статистика WDDM…)"
        public bool Integrated;         // встроенная видеокарта
        public double Load;             // 0..100
        public long MemUsed, MemTotal;  // видеопамять; у встроенной — общая память, которую она занимает
        public bool MemShared;          // MemUsed/MemTotal — общая память, а не своя
        public long SharedUsed, SharedTotal;
        public double? Temp, Watts, Fan, FanRpm, ClockMhz;   // Fan — %, FanRpm — об/мин, ClockMhz — частота ядра

        public double MemPercent { get { return MemTotal > 0 ? 100.0 * MemUsed / MemTotal : 0; } }
    }

    sealed class DiskInfo
    {
        public string Letter;           // "C:"
        public string Label;
        public long Total, Free;
        public double ReadBps, WriteBps;
        public double Active;           // доля времени под нагрузкой, 0..100

        public long Used { get { return Total - Free; } }
        public double Percent { get { return Total > 0 ? 100.0 * Used / Total : 0; } }
    }

    sealed class NetInfo
    {
        public string Name;             // имя активного адаптера
        public long LinkBps;
        public double DownBps, UpBps;   // байт в секунду
        public long TotalDown, TotalUp; // байт с момента включения адаптера
        public double PeakDown, PeakUp; // наибольшая скорость на графике (за historySeconds) — верх шкалы
        public DayPeaks Today;          // пики, трафик и перебои за сегодня (журнал network-log)
        public NetHealth Health;        // пинг и идущий сейчас перебой
    }

    sealed class ProcInfo
    {
        public string Name;
        public int Count;               // сколько процессов с этим именем
        public double Cpu;              // 0..100 от всей мощности процессора
        public long Memory;             // частный рабочий набор
    }

    // Снимок всех показателей; создаётся в потоке опроса, читается в потоке интерфейса.
    sealed class Sample
    {
        public DateTime Time;
        public TimeSpan Uptime;
        public CpuInfo Cpu;
        public MemInfo Mem;
        public GpuInfo Gpu;                                     // null — видеокарта недоступна
        public List<DiskInfo> Disks = new List<DiskInfo>();
        public NetInfo Net;                                     // null — нет активного адаптера
        public List<ProcInfo> Procs;                            // null — процессы не опрашивались
        public double[] CpuHistory, GpuHistory, NetDownHistory, NetUpHistory;

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["time"] = Time;
            d["uptimeSeconds"] = Math.Round(Uptime.TotalSeconds);
            if (Cpu != null)
            {
                var c = new Dictionary<string, object>();
                c["name"] = Cpu.Name;
                c["logical"] = Cpu.Logical;
                c["baseMhz"] = Cpu.BaseMhz;
                c["total"] = Math.Round(Cpu.Total, 1);
                c["maxCore"] = Math.Round(Cpu.MaxCore, 1);
                c["cores"] = Cpu.Cores.Select(x => (object)Math.Round(x, 1)).ToList();
                c["mhz"] = Cpu.Mhz.HasValue ? (object)Math.Round(Cpu.Mhz.Value) : null;
                c["maxMhz"] = Cpu.MaxMhz.HasValue ? (object)Math.Round(Cpu.MaxMhz.Value) : null;
                c["temp"] = Cpu.Temp.HasValue ? (object)Math.Round(Cpu.Temp.Value, 1) : null;
                c["tempSource"] = Cpu.TempSource;
                d["cpu"] = c;
            }
            if (Mem != null)
            {
                var m = new Dictionary<string, object>();
                m["total"] = Mem.Total;
                m["available"] = Mem.Available;
                m["cache"] = Mem.Cache;
                m["commitTotal"] = Mem.CommitTotal;
                m["commitLimit"] = Mem.CommitLimit;
                m["percent"] = Math.Round(Mem.Percent, 1);
                m["processes"] = Mem.Processes;
                m["threads"] = Mem.Threads;
                m["handles"] = Mem.Handles;
                d["memory"] = m;
            }
            if (Gpu != null)
            {
                var g = new Dictionary<string, object>();
                g["name"] = Gpu.Name;
                g["fullName"] = Gpu.FullName;
                g["vendor"] = Gpu.Vendor;
                g["source"] = Gpu.Source;
                g["integrated"] = Gpu.Integrated;
                g["load"] = Math.Round(Gpu.Load, 1);
                g["memUsed"] = Gpu.MemUsed;
                g["memTotal"] = Gpu.MemTotal;
                g["memShared"] = Gpu.MemShared;
                g["sharedUsed"] = Gpu.SharedUsed;
                g["temp"] = Gpu.Temp.HasValue ? (object)Math.Round(Gpu.Temp.Value, 1) : null;
                g["watts"] = Gpu.Watts.HasValue ? (object)Math.Round(Gpu.Watts.Value, 1) : null;
                g["fan"] = Gpu.Fan;
                g["fanRpm"] = Gpu.FanRpm;
                g["clockMhz"] = Gpu.ClockMhz.HasValue ? (object)Math.Round(Gpu.ClockMhz.Value) : null;
                d["gpu"] = g;
            }
            else d["gpu"] = null;
            d["disks"] = Disks.Select(x =>
            {
                var k = new Dictionary<string, object>();
                k["letter"] = x.Letter;
                k["label"] = x.Label;
                k["total"] = x.Total;
                k["free"] = x.Free;
                k["readBps"] = Math.Round(x.ReadBps);
                k["writeBps"] = Math.Round(x.WriteBps);
                k["active"] = Math.Round(x.Active, 1);
                return (object)k;
            }).ToList();
            if (Net != null)
            {
                var n = new Dictionary<string, object>();
                n["name"] = Net.Name;
                n["linkBps"] = Net.LinkBps;
                n["downBps"] = Math.Round(Net.DownBps);
                n["upBps"] = Math.Round(Net.UpBps);
                n["totalDown"] = Net.TotalDown;
                n["totalUp"] = Net.TotalUp;
                n["peakDownBps"] = Math.Round(Net.PeakDown);
                n["peakUpBps"] = Math.Round(Net.PeakUp);
                if (Net.Today != null)
                {
                    var t = Net.Today;
                    var td = new Dictionary<string, object>();
                    td["day"] = t.Day.ToString("yyyy-MM-dd");
                    td["downBps"] = Math.Round(t.Down);
                    td["downAt"] = t.Down > 0 ? (object)t.DownAt : null;
                    td["upBps"] = Math.Round(t.Up);
                    td["upAt"] = t.Up > 0 ? (object)t.UpAt : null;
                    td["minuteDownBps"] = Math.Round(t.MinuteDown);
                    td["minuteUpBps"] = Math.Round(t.MinuteUp);
                    td["bytesDown"] = Math.Round(t.BytesDown);
                    td["bytesUp"] = Math.Round(t.BytesUp);
                    td["seconds"] = Math.Round(t.Seconds);
                    n["today"] = td;
                }
                if (Net.Health != null)
                {
                    n["pingMs"] = Net.Health.PingMs;
                    n["gatewayMs"] = Net.Health.GatewayMs;
                    n["gateway"] = Net.Health.Gateway;
                }
                d["network"] = n;
            }
            else d["network"] = null;
            if (Procs != null)
                d["processes"] = Procs.Select(x =>
                {
                    var k = new Dictionary<string, object>();
                    k["name"] = x.Name;
                    k["count"] = x.Count;
                    k["cpu"] = Math.Round(x.Cpu, 1);
                    k["memory"] = x.Memory;
                    return (object)k;
                }).ToList();
            return d;
        }
    }

    // Настройки, которые правятся руками: config.json рядом с exe.
    sealed class Config
    {
        public string Hotkey = "Ctrl+Alt+R";
        public int IntervalMs = 1000;
        public int HistorySeconds = 60;
        public List<string> Disks = new List<string>();     // пусто — все локальные диски
        public bool NetworkBits = true;
        public int TopProcesses = 5;
        public string LhmUrl = "http://127.0.0.1:8085/data.json";   // локальный веб-сервер LibreHardwareMonitor; пусто — не опрашивать
        public string GpuSource = "auto";                           // auto — NVML для NVIDIA; windows — всегда статистика WDDM
        public int InternetMbps;                                    // скорость тарифа для «% канала» в журнале сети; 0 — по скорости адаптера
        public string PingHost = "1.1.1.1";                  // узел в интернете для проверки связи; пусто — не пинговать
        public int PingIntervalSec = 2;
        public int PingWarnMs = 150;                                // выше — «высокий пинг»
        public string FilePath;

        // Сколько точек истории помещается в график.
        public int HistoryPoints
        {
            get { return Math.Max(10, HistorySeconds * 1000 / IntervalMs); }
        }

        public static Config Load(string path)
        {
            var c = new Config();
            c.FilePath = path;
            if (path == null || !File.Exists(path)) return c;
            var root = Json.Obj(Json.TryParse(File.ReadAllText(path, Encoding.UTF8)));
            if (root == null)
            {
                Log.Write("config.json не разобран — взяты значения по умолчанию");
                return c;
            }
            var s = Json.Str(Json.Get(root, "hotkey"));
            if (s != null) c.Hotkey = s.Trim();
            var n = Json.Num(Json.Get(root, "intervalMs"));
            if (n.HasValue) c.IntervalMs = Math.Min(5000, Math.Max(500, (int)n.Value));
            n = Json.Num(Json.Get(root, "historySeconds"));
            if (n.HasValue) c.HistorySeconds = Math.Min(600, Math.Max(20, (int)n.Value));
            n = Json.Num(Json.Get(root, "topProcesses"));
            if (n.HasValue) c.TopProcesses = Math.Min(15, Math.Max(1, (int)n.Value));
            s = Json.Str(Json.Get(root, "networkUnits"));
            if (s != null) c.NetworkBits = !string.Equals(s.Trim(), "bytes", StringComparison.OrdinalIgnoreCase);
            s = Json.Str(Json.Get(root, "lhmUrl"));
            if (s != null) c.LhmUrl = s.Trim();
            s = Json.Str(Json.Get(root, "pingHost"));
            if (s != null) c.PingHost = s.Trim();
            n = Json.Num(Json.Get(root, "pingIntervalSec"));
            if (n.HasValue) c.PingIntervalSec = Math.Min(60, Math.Max(1, (int)n.Value));
            n = Json.Num(Json.Get(root, "pingWarnMs"));
            if (n.HasValue) c.PingWarnMs = Math.Min(5000, Math.Max(20, (int)n.Value));
            n = Json.Num(Json.Get(root, "internetMbps"));
            if (n.HasValue) c.InternetMbps = Math.Max(0, (int)n.Value);
            s = Json.Str(Json.Get(root, "gpuSource"));
            if (s != null) c.GpuSource = s.Trim().ToLowerInvariant();
            var disks = Json.Arr(Json.Get(root, "disks"));
            if (disks != null)
                foreach (var x in disks)
                {
                    string letter = NormalizeDisk(Json.Str(x));
                    if (letter != null && !c.Disks.Contains(letter)) c.Disks.Add(letter);
                }
            return c;
        }

        // "c", "C:", "c:\" -> "C:"
        public static string NormalizeDisk(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Length == 0 || !char.IsLetter(s[0])) return null;
            return char.ToUpperInvariant(s[0]) + ":";
        }
    }

    // Состояние окна: state.json рядом с exe, пишется самим виджетом.
    sealed class State
    {
        public static readonly string[] SectionIds = { "cpu", "memory", "gpu", "disks", "network", "processes" };
        readonly Dictionary<string, bool> sections = new Dictionary<string, bool>();

        public string FilePath;
        public double? Left, Top;
        public bool Docked = true;          // карточка стоит в углу у трея
        public bool Topmost;                // скрепка: поверх всех окон
        public bool Hidden;                 // свёрнута в трей (так и стартует)
        public bool Locked;                 // положение зафиксировано: перетаскивание выключено
        public bool Compact;
        public int Columns = 1;
        public string Theme = "auto";
        public double Opacity = 1.0;   // непрозрачность фона карточки (текст, графики и полосы всегда непрозрачны)
        public double Scale = 1.0;
        public const double OpacityMin = 0.1, ScaleMin = 0.6, ScaleMax = 2.0;
        public string ProcessSort = "cpu";  // cpu | memory
        public bool TrayPromoted;           // значок уже выводился из скрытой области трея
        public double? JournalLeft, JournalTop;   // окно «Сеть по дням», если его передвигали

        public bool Section(string id)
        {
            bool on;
            return !sections.TryGetValue(id, out on) || on;
        }

        public void SetSection(string id, bool on)
        {
            sections[id] = on;
        }

        public static State Load(string path)
        {
            var s = new State();
            s.FilePath = path;
            if (path == null || !File.Exists(path)) return s;
            var root = Json.Obj(Json.TryParse(File.ReadAllText(path, Encoding.UTF8)));
            if (root == null) return s;
            s.Left = Json.Num(Json.Get(root, "left"));
            s.Top = Json.Num(Json.Get(root, "top"));
            s.Docked = Json.Bool(Json.Get(root, "docked")) != false;
            // до 2026-10-06: "pinned" — не скрывать при щелчке мимо (карточка и так была поверх окон),
            // "visible" — была на экране при выходе
            s.Topmost = (Json.Bool(Json.Get(root, "topmost")) ?? Json.Bool(Json.Get(root, "pinned"))) == true;
            s.Hidden = Json.Bool(Json.Get(root, "hidden")) ?? Json.Bool(Json.Get(root, "visible")) == false;
            s.Locked = Json.Bool(Json.Get(root, "locked")) == true;
            s.Compact = Json.Bool(Json.Get(root, "compact")) == true;
            var cols = Json.Num(Json.Get(root, "columns"));
            if (cols.HasValue) s.Columns = cols.Value >= 2 ? 2 : 1;
            var th = Json.Str(Json.Get(root, "theme"));
            if (th == "dark" || th == "light" || th == "auto") s.Theme = th;
            // прежний ключ "opacity" гасил всю карточку и не учитывается: новая настройка стартует со 100 %
            var op = Json.Num(Json.Get(root, "bgOpacity"));
            if (op.HasValue) s.Opacity = Math.Min(1, Math.Max(OpacityMin, op.Value));
            var sc = Json.Num(Json.Get(root, "scale"));
            if (sc.HasValue) s.Scale = Math.Min(ScaleMax, Math.Max(ScaleMin, sc.Value));
            var sort = Json.Str(Json.Get(root, "processSort"));
            if (sort == "cpu" || sort == "memory") s.ProcessSort = sort;
            s.TrayPromoted = Json.Bool(Json.Get(root, "trayPromoted")) == true;
            s.JournalLeft = Json.Num(Json.Get(root, "journalLeft"));
            s.JournalTop = Json.Num(Json.Get(root, "journalTop"));
            var sec = Json.Obj(Json.Get(root, "sections"));
            if (sec != null)
                foreach (var id in SectionIds)
                {
                    var on = Json.Bool(Json.Get(sec, id));
                    if (on.HasValue) s.sections[id] = on.Value;
                }
            return s;
        }

        public void Save()
        {
            if (string.IsNullOrEmpty(FilePath)) return;
            var d = new Dictionary<string, object>();
            d["left"] = Left;
            d["top"] = Top;
            d["docked"] = Docked;
            d["topmost"] = Topmost;
            d["hidden"] = Hidden;
            d["locked"] = Locked;
            d["compact"] = Compact;
            d["columns"] = Columns;
            d["theme"] = Theme;
            d["bgOpacity"] = Opacity;
            d["scale"] = Scale;
            d["processSort"] = ProcessSort;
            d["trayPromoted"] = TrayPromoted;
            d["journalLeft"] = JournalLeft;
            d["journalTop"] = JournalTop;
            var sec = new Dictionary<string, object>();
            foreach (var id in SectionIds) sec[id] = Section(id);
            d["sections"] = sec;
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(d, true), new UTF8Encoding(false));
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

