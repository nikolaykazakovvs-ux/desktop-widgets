using System;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ResourcesWidget
{
    // Температуру процессора Windows обычным программам не отдаёт: датчик Ryzen читают только программы
    // мониторинга со своим драйвером. Они же открыто делятся показаниями с другими программами —
    // виджет берёт температуру у той, что запущена:
    //   HWiNFO — общая память «HWiNFO_SENS_SM2» (настройка «Shared Memory Support»);
    //   AIDA64 — общая память «AIDA64_SensorValues» (Настройки → Мониторинг → Внешние приложения);
    //   Core Temp — общая память «CoreTempMappingObjectEx» / «CoreTempMappingObject» (включена всегда);
    //   LibreHardwareMonitor 0.9.x — встроенный веб-сервер /data.json (Options → Remote Web Server → Run);
    //   OpenHardwareMonitor и старые LibreHardwareMonitor — WMI.
    // Только чтение. Если ни одна программа не запущена, температуры нет — ни прежнего значения, ни нуля.
    sealed class CpuTempSensor
    {
        static readonly Regex AidaTemp = new Regex(@"<temp><id>([^<]*)</id><label>([^<]*)</label><value>([^<]*)</value></temp>", RegexOptions.Compiled);
        static readonly string[] WmiNamespaces = { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" };

        DateTime nextWmi = DateTime.MinValue;
        double? wmiValue;
        string wmiSource;

        readonly LhmClient lhm;

        public string Source;   // откуда последнее значение: "HWiNFO · CPU (Tctl/Tdie)"; null — источника нет

        // lhm — опрос веб-сервера LibreHardwareMonitor (общий с видеокартой).
        public CpuTempSensor(LhmClient lhm)
        {
            this.lhm = lhm;
        }

        public double? Read()
        {
            string label;
            double? t = LocalSensorSnapshot.Read(out label);
            if (t.HasValue) { Source = "Локальный датчик · " + label; return t; }
            t = FromHwinfo(out label);
            if (t.HasValue) { Source = "HWiNFO · " + label; return t; }
            t = FromAida(out label);
            if (t.HasValue) { Source = "AIDA64 · " + label; return t; }
            t = FromCoreTemp(out label);
            if (t.HasValue) { Source = "Core Temp · " + label; return t; }
            t = ParseLhmTree(lhm != null ? lhm.Root : null, out label);
            if (t.HasValue) { Source = "LibreHardwareMonitor · " + label; return t; }
            t = FromWmi();
            Source = t.HasValue ? wmiSource : null;
            return t;
        }

        // ---------- HWiNFO ----------

        const uint HwinfoSignature = 0x53695748;   // "HWiS"; при закрытии HWiNFO пишет "DEAD"

        static double? FromHwinfo(out string label)
        {
            label = null;
            IntPtr map = Native.OpenFileMapping(Native.FILE_MAP_READ, false, "Global\\HWiNFO_SENS_SM2");
            if (map == IntPtr.Zero) return null;
            try
            {
                IntPtr view = Native.MapViewOfFile(map, Native.FILE_MAP_READ, 0, 0, UIntPtr.Zero);
                if (view == IntPtr.Zero) return null;
                try { return ParseHwinfo(view, out label); }
                finally { Native.UnmapViewOfFile(view); }
            }
            finally
            {
                Native.CloseHandle(map);
            }
        }

        // Заголовок: подпись, версия, ревизия, время опроса, смещение/размер/число датчиков, смещение/размер/число показаний.
        // Показание: тип (1 — температура), номер датчика, id, исходная подпись [128], подпись пользователя [128], единица [16], значение (double).
        public static double? ParseHwinfo(IntPtr view, out string label)
        {
            label = null;
            if ((uint)Marshal.ReadInt32(view, 0) != HwinfoSignature) return null;
            int offset = Marshal.ReadInt32(view, 32), size = Marshal.ReadInt32(view, 36), count = Marshal.ReadInt32(view, 40);
            if (offset <= 0 || size < 292 || count <= 0 || count > 100000) return null;
            int best = int.MaxValue;
            double? value = null;
            for (int i = 0; i < count; i++)
            {
                IntPtr r = view + offset + i * size;
                if (Marshal.ReadInt32(r, 0) != 1) continue;
                string name = Marshal.PtrToStringAnsi(r + 12);
                int rank = Rank(name);
                if (rank >= best) continue;
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(r, 284));
                if (!Plausible(v)) continue;
                best = rank;
                value = v;
                label = name;
            }
            return value;
        }

        // ---------- AIDA64 ----------

        static double? FromAida(out string label)
        {
            label = null;
            IntPtr map = Native.OpenFileMapping(Native.FILE_MAP_READ, false, "AIDA64_SensorValues");
            if (map == IntPtr.Zero) return null;
            try
            {
                IntPtr view = Native.MapViewOfFile(map, Native.FILE_MAP_READ, 0, 0, UIntPtr.Zero);
                if (view == IntPtr.Zero) return null;
                try { return ParseAida(Marshal.PtrToStringAnsi(view), out label); }
                finally { Native.UnmapViewOfFile(view); }
            }
            finally
            {
                Native.CloseHandle(map);
            }
        }

        // Текст вида <temp><id>TCPUDIO</id><label>CPU Diode</label><value>52</value></temp>...
        public static double? ParseAida(string text, out string label)
        {
            label = null;
            if (string.IsNullOrEmpty(text)) return null;
            int best = int.MaxValue;
            double? value = null;
            foreach (Match m in AidaTemp.Matches(text))
            {
                string id = m.Groups[1].Value, name = m.Groups[2].Value;
                int rank = Rank(name);
                if (rank == int.MaxValue && id.StartsWith("TCPU", StringComparison.OrdinalIgnoreCase)) rank = 50;
                if (rank >= best) continue;
                double v;
                if (!double.TryParse(m.Groups[3].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || !Plausible(v)) continue;
                best = rank;
                value = v;
                label = name;
            }
            return value;
        }

        // ---------- Core Temp ----------

        static double? FromCoreTemp(out string label)
        {
            label = null;
            foreach (string name in new[] { "CoreTempMappingObjectEx", "CoreTempMappingObject" })
            {
                IntPtr map = Native.OpenFileMapping(Native.FILE_MAP_READ, false, name);
                if (map == IntPtr.Zero) continue;
                try
                {
                    IntPtr view = Native.MapViewOfFile(map, Native.FILE_MAP_READ, 0, 0, UIntPtr.Zero);
                    if (view == IntPtr.Zero) continue;
                    try { return ParseCoreTemp(view, out label); }
                    finally { Native.UnmapViewOfFile(view); }
                }
                finally
                {
                    Native.CloseHandle(map);
                }
            }
            return null;
        }

        // CoreTempSharedData(Ex) с выравниванием 4 байта (alcpu.com/CoreTemp/developers.html): uiLoad[256], uiTjMax[128],
        // uiCoreCnt, uiCPUCnt, fTemp[256], fVID, fCPUSpeed, fFSBSpeed, fMultiplier, sCPUName[100], ucFahrenheit, ucDeltaToTjMax, …
        // Ядро j процессора i — fTemp[i·uiCoreCnt + j]; при ucDeltaToTjMax это расстояние до TjMax, при ucFahrenheit — градусы Фаренгейта.
        public static double? ParseCoreTemp(IntPtr view, out string label)
        {
            label = null;
            int cores = Marshal.ReadInt32(view, 1536), cpus = Marshal.ReadInt32(view, 1540);
            if (cores <= 0 || cpus <= 0 || cpus > 128 || cores * cpus > 256) return null;
            bool fahrenheit = Marshal.ReadByte(view, 2684) != 0, delta = Marshal.ReadByte(view, 2685) != 0;
            var tjMax = new int[128];
            var temps = new float[256];
            Marshal.Copy(view + 1024, tjMax, 0, tjMax.Length);
            Marshal.Copy(view + 1544, temps, 0, temps.Length);
            double? best = null;
            for (int i = 0; i < cpus; i++)
                for (int j = 0; j < cores; j++)
                {
                    double v = temps[i * cores + j];
                    if (delta) v = tjMax[i] - v;
                    if (fahrenheit) v = (v - 32) * 5 / 9;
                    if (Plausible(v) && (!best.HasValue || v > best.Value)) best = v;
                }
            if (best.HasValue) label = "самое горячее ядро";
            return best;
        }

        // ---------- LibreHardwareMonitor: веб-сервер ----------
        // В LibreHardwareMonitor 0.9.6 WMI нет; другим программам он отдаёт показания только через GET /data.json
        // (опрашивает LhmClient). Если LHM не отвечает, дерева нет и температуры нет.

        // Дерево узлов {Text, Value, RawValue, SensorId, Type, Children}. Значение — строка с единицей
        // в локали LHM («52,4 °C»); пустое число означает, что датчик сейчас не читается.
        public static double? ParseLhm(string json, out string label)
        {
            return ParseLhmTree(Json.TryParse(json), out label);
        }

        public static double? ParseLhmTree(object root, out string label)
        {
            int best = int.MaxValue;
            double? value = null;
            label = null;
            if (root != null) WalkLhm(root, ref best, ref value, ref label);
            return value;
        }

        static void WalkLhm(object node, ref int best, ref double? value, ref string label)
        {
            var d = Json.Obj(node);
            if (d == null) return;
            if (Json.Str(Json.Get(d, "Type")) == "Temperature")
            {
                string id = Json.Str(Json.Get(d, "SensorId")) ?? "";
                if (id.StartsWith("/amdcpu", StringComparison.OrdinalIgnoreCase) || id.StartsWith("/intelcpu", StringComparison.OrdinalIgnoreCase))
                {
                    string name = Json.Str(Json.Get(d, "Text"));
                    int rank = Rank(name);
                    if (rank == int.MaxValue) rank = 60;   // любая другая температура процессора
                    double v;
                    if (rank < best && Lhm.TryValue(d, out v) && Plausible(v))
                    {
                        best = rank;
                        value = v;
                        label = name;
                    }
                }
            }
            var kids = Json.Arr(Json.Get(d, "Children"));
            if (kids != null)
                foreach (var k in kids) WalkLhm(k, ref best, ref value, ref label);
        }

        // ---------- OpenHardwareMonitor / старые LibreHardwareMonitor: WMI ----------

        // WMI дороже общей памяти: опрос раз в 2 с, а если программы нет — проверка раз в 30 с.
        double? FromWmi()
        {
            if (DateTime.UtcNow < nextWmi) return wmiValue;
            wmiValue = null;
            wmiSource = null;
            foreach (string ns in WmiNamespaces)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher(ns, "SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'"))
                    using (var found = searcher.Get())
                    {
                        int best = int.MaxValue;
                        foreach (ManagementBaseObject mo in found)
                        {
                            using (mo)
                            {
                                string id = mo["Identifier"] as string ?? "";
                                if (id.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                string name = mo["Name"] as string ?? "";
                                int rank = Rank(name);
                                if (rank == int.MaxValue) rank = 60;   // любая другая температура процессора
                                object raw = mo["Value"];
                                if (rank >= best || raw == null) continue;
                                double v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                                if (!Plausible(v)) continue;
                                best = rank;
                                wmiValue = v;
                                wmiSource = (ns.EndsWith("LibreHardwareMonitor") ? "LibreHardwareMonitor" : "OpenHardwareMonitor") + " · " + name;
                            }
                        }
                    }
                    if (wmiValue.HasValue) break;
                }
                catch (ManagementException)
                {
                    // пространства нет — программа не запущена
                }
                catch (COMException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            nextWmi = DateTime.UtcNow.AddSeconds(wmiValue.HasValue ? 2 : 30);
            return wmiValue;
        }

        // ---------- выбор датчика ----------

        // Чем меньше, тем лучше: собственный датчик процессора (Tctl/Tdie), затем корпус, затем датчик платы у сокета.
        static int Rank(string name)
        {
            if (string.IsNullOrEmpty(name)) return int.MaxValue;
            string n = name.Trim();
            if (n.IndexOf("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (n.IndexOf("Tdie", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (n.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) || n.Equals("Package", StringComparison.OrdinalIgnoreCase)) return 2;
            if (n.Equals("CPU Diode", StringComparison.OrdinalIgnoreCase)) return 3;
            if (n.StartsWith("CPU Die", StringComparison.OrdinalIgnoreCase)) return 4;
            if (n.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0) return 5;
            if (n.Equals("CPU", StringComparison.OrdinalIgnoreCase)) return 10;   // датчик платы у сокета
            return int.MaxValue;
        }

        public static bool Plausible(double v)
        {
            return !double.IsNaN(v) && v > 0 && v < 150;
        }
    }
}
