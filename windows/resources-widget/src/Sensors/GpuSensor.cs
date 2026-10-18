using System;
using System.Collections.Generic;
using System.Text;

namespace ResourcesWidget
{
    // Видеокарта. Какую показывать — решает статистика WDDM: ту, у которой больше собственной памяти.
    // NVIDIA — через NVML (как nvidia-smi: точная загрузка, мощность, вентилятор в %); AMD, Intel и остальные —
    // через статистику WDDM (как диспетчер задач). Чего не хватает (мощность, вентилятор), берётся у LibreHardwareMonitor,
    // если он запущен. В config.json "gpuSource": "windows" — всегда статистика WDDM.
    sealed class GpuSensor : IDisposable
    {
        readonly NvmlGpu nvml = new NvmlGpu();
        readonly WddmGpu wddm = new WddmGpu();
        readonly LhmClient lhm;
        readonly bool useNvml;

        public GpuSensor(string source, LhmClient lhm)
        {
            this.lhm = lhm;
            useNvml = !string.Equals(source, "windows", StringComparison.OrdinalIgnoreCase);
        }

        public GpuInfo Read()
        {
            var a = wddm.TryOpen() ? wddm.Current : null;
            GpuInfo g = null;
            if (useNvml && (a == null || a.Vendor == 0x10DE))
            {
                g = nvml.Read();
                if (g != null && a != null)
                {
                    g.FullName = a.FullName;
                    g.Integrated = a.Integrated;
                }
            }
            if (g == null && a != null) g = wddm.Read();
            if (g != null) LhmGpu.Supplement(g, lhm != null ? lhm.Root : null, a != null ? a.Vendor : 0x10DE);
            return g;
        }

        public void Dispose()
        {
            nvml.Dispose();
            wddm.Dispose();
        }
    }

    // NVIDIA через NVML (nvml.dll из драйвера). Прав администратора не требует.
    sealed class NvmlGpu : IDisposable
    {
        IntPtr device;
        string name, fullName;
        bool ready, missing, noMemoryV2;
        int failures;
        DateTime nextTry = DateTime.MinValue;

        bool Init()
        {
            if (missing || DateTime.UtcNow < nextTry) return false;
            try
            {
                int r = Native.NvmlInit();
                if (r != Native.NVML_SUCCESS)
                {
                    Retry("nvmlInit: код " + r);
                    return false;
                }
                r = Native.NvmlDeviceGetHandleByIndex(0, out device);
                if (r != Native.NVML_SUCCESS)
                {
                    Native.NvmlShutdown();
                    Retry("видеокарта не найдена: код " + r);
                    return false;
                }
                var sb = new StringBuilder(96);
                fullName = Native.NvmlDeviceGetName(device, sb, (uint)sb.Capacity) == Native.NVML_SUCCESS ? sb.ToString() : "NVIDIA";
                name = WddmGpu.ShortName(fullName, 0x10DE);
                ready = true;
                return true;
            }
            catch (DllNotFoundException)
            {
                missing = true;
            }
            catch (EntryPointNotFoundException ex)
            {
                missing = true;
                Log.Write("gpu: " + ex.Message);
            }
            catch (BadImageFormatException ex)
            {
                missing = true;
                Log.Write("gpu: " + ex.Message);
            }
            return false;
        }

        // После выхода из сна драйвер отвечает ошибкой несколько секунд — первые повторы частые, потом раз в минуту.
        void Retry(string why)
        {
            failures++;
            Log.Write("gpu: " + why);
            nextTry = DateTime.UtcNow.AddSeconds(failures <= 5 ? 3 : 60);
        }

        public GpuInfo Read()
        {
            if (!ready && !Init()) return null;
            Native.NvmlUtilization util;
            int r = Native.NvmlDeviceGetUtilizationRates(device, out util);
            if (r != Native.NVML_SUCCESS)
            {
                // драйвер перезапущен или обновлён — подключиться заново позже
                ready = false;
                try { Native.NvmlShutdown(); }
                catch { }
                Retry("опрос: код " + r);
                return null;
            }
            failures = 0;
            var g = new GpuInfo { Name = name, FullName = fullName, Vendor = "NVIDIA", Source = "NVML (драйвер NVIDIA)", Load = Math.Min(100, util.Gpu) };
            ReadMemory(g);
            uint v;
            if (Native.NvmlDeviceGetTemperature(device, 0, out v) == Native.NVML_SUCCESS) g.Temp = v;
            if (Native.NvmlDeviceGetPowerUsage(device, out v) == Native.NVML_SUCCESS) g.Watts = v / 1000.0;
            if (Native.NvmlDeviceGetFanSpeed(device, out v) == Native.NVML_SUCCESS) g.Fan = v;
            try
            {
                if (Native.NvmlDeviceGetClockInfo(device, 0, out v) == Native.NVML_SUCCESS && v > 0) g.ClockMhz = v;   // NVML_CLOCK_GRAPHICS
            }
            catch (EntryPointNotFoundException)
            {
            }
            return g;
        }

        void ReadMemory(GpuInfo g)
        {
            if (!noMemoryV2)
            {
                try
                {
                    var v2 = new Native.NvmlMemoryV2 { Version = Native.NvmlMemoryV2Version };
                    if (Native.NvmlDeviceGetMemoryInfoV2(device, ref v2) == Native.NVML_SUCCESS)
                    {
                        g.MemTotal = (long)v2.Total;
                        g.MemUsed = (long)v2.Used;
                        return;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                }
                noMemoryV2 = true;   // старый драйвер — берём первую версию
            }
            Native.NvmlMemory mem;
            if (Native.NvmlDeviceGetMemoryInfo(device, out mem) == Native.NVML_SUCCESS)
            {
                g.MemTotal = (long)mem.Total;
                g.MemUsed = (long)mem.Used;
            }
        }

        public void Dispose()
        {
            if (!ready) return;
            ready = false;
            try { Native.NvmlShutdown(); }
            catch { }
        }
    }

    // Недостающие показатели видеокарты у LibreHardwareMonitor: его датчики «/gpu-amd/0/…», «/gpu-nvidia/0/…»,
    // «/gpu-intel-…». Подставляется только то, чего нет у основного источника.
    static class LhmGpu
    {
        sealed class Group
        {
            public string Hardware;   // название видеокарты в LHM
            public double? Temp, Watts, Fan, FanRpm, Clock;
            public int PowerRank = int.MaxValue;
        }

        public static void Supplement(GpuInfo g, object root, int vendor)
        {
            if (root == null) return;
            string family = vendor == 0x1002 || vendor == 0x1022 ? "/gpu-amd" : vendor == 0x8086 ? "/gpu-intel" : vendor == 0x10DE ? "/gpu-nvidia" : null;
            if (family == null) return;
            var groups = new Dictionary<string, Group>();
            Walk(root, null, null, family, groups);
            Group best = null;
            foreach (var x in groups.Values)
            {
                if (best == null) best = x;
                if (g.FullName != null && string.Equals(x.Hardware, g.FullName, StringComparison.OrdinalIgnoreCase)) { best = x; break; }
            }
            if (best == null) return;
            var used = new List<string>();
            if (!g.Temp.HasValue && best.Temp.HasValue) { g.Temp = best.Temp; used.Add("температура"); }
            if (!g.Watts.HasValue && best.Watts.HasValue) { g.Watts = best.Watts; used.Add("мощность"); }
            if (!g.Fan.HasValue && best.Fan.HasValue) { g.Fan = best.Fan; used.Add("вентилятор"); }
            if (!g.Fan.HasValue && !g.FanRpm.HasValue && best.FanRpm.HasValue) { g.FanRpm = best.FanRpm; used.Add("вентилятор"); }
            if (!g.ClockMhz.HasValue && best.Clock.HasValue) { g.ClockMhz = best.Clock; used.Add("частота"); }
            if (used.Count > 0) g.Source += " + LibreHardwareMonitor: " + string.Join(", ", used.ToArray());
        }

        // Датчик лежит в группе («Temperatures»), группа — в узле видеокарты: название видеокарты — у «деда» датчика.
        static void Walk(object node, string parent, string grand, string family, Dictionary<string, Group> groups)
        {
            var d = Json.Obj(node);
            if (d == null) return;
            string text = Json.Str(Json.Get(d, "Text"));
            string id = Json.Str(Json.Get(d, "SensorId"));
            if (id != null && id.StartsWith(family, StringComparison.OrdinalIgnoreCase))
            {
                // "/gpu-amd/0/temperature/0" -> ключ группы "/gpu-amd/0"
                int cut = id.IndexOf('/', id.IndexOf('/', 1) + 1);
                string key = cut > 0 ? id.Substring(0, cut) : id;
                Group grp;
                if (!groups.TryGetValue(key, out grp)) groups[key] = grp = new Group { Hardware = grand };
                Take(grp, Json.Str(Json.Get(d, "Type")), text ?? "", d);
            }
            var kids = Json.Arr(Json.Get(d, "Children"));
            if (kids != null)
                foreach (var k in kids) Walk(k, text, parent, family, groups);
        }

        static void Take(Group grp, string type, string text, IDictionary<string, object> sensor)
        {
            double v;
            if (!Lhm.TryValue(sensor, out v)) return;
            bool fan = text.StartsWith("GPU Fan", StringComparison.OrdinalIgnoreCase);
            switch (type)
            {
                case "Temperature":
                    if (text.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) && CpuTempSensor.Plausible(v)) grp.Temp = v;
                    break;
                case "Power":
                    int rank = text.Equals("GPU Package", StringComparison.OrdinalIgnoreCase) ? 0 : text.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                    if (rank < grp.PowerRank && v >= 0 && v < 2000) { grp.PowerRank = rank; grp.Watts = v; }
                    break;
                case "Control":
                    if (fan && !grp.Fan.HasValue && v >= 0 && v <= 100) grp.Fan = v;
                    break;
                case "Fan":
                    if (fan && !grp.FanRpm.HasValue && v >= 0 && v < 20000) grp.FanRpm = v;
                    break;
                case "Clock":
                    if (text.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) && v > 0) grp.Clock = v;
                    break;
            }
        }
    }
}
