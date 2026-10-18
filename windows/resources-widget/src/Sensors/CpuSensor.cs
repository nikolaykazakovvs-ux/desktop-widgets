using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ResourcesWidget
{
    // Загрузка — по времени простоя каждого логического процессора (NtQuerySystemInformation).
    // Частота — счётчик «% Processor Performance» × паспортная частота, как «Скорость» в диспетчере задач.
    sealed class CpuSensor : IDisposable
    {
        const int EntrySize = 48;   // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
        readonly int count = Environment.ProcessorCount;
        readonly IntPtr buf;
        readonly long[] prevIdle, prevTotal;
        readonly string name;
        readonly int baseMhz;
        bool primed;
        IntPtr query, perfTotal, perfEach;
        IntPtr arr;
        uint arrSize;

        public CpuSensor()
        {
            buf = Marshal.AllocHGlobal(EntrySize * count);
            prevIdle = new long[count];
            prevTotal = new long[count];
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null)
                    {
                        name = ShortName(k.GetValue("ProcessorNameString") as string);
                        var mhz = k.GetValue("~MHz");
                        if (mhz is int) baseMhz = (int)mhz;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("cpu: реестр: " + ex.Message);
            }
            InitFrequency();
        }

        // "AMD Ryzen 7 2700 Eight-Core Processor" -> "Ryzen 7 2700"
        static string ShortName(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "").Trim();
            int cut = s.IndexOf(" @", StringComparison.Ordinal);
            if (cut > 0) s = s.Substring(0, cut);
            foreach (string word in new[] { " Processor", " CPU" })
            {
                int i = s.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (i > 0) s = s.Substring(0, i);
            }
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new System.Collections.Generic.List<string>();
            foreach (string p in parts)
            {
                if (p.EndsWith("-Core", StringComparison.OrdinalIgnoreCase)) continue;
                if (kept.Count == 0 && (p == "AMD" || p == "Intel")) continue;
                kept.Add(p);
            }
            return kept.Count > 0 ? string.Join(" ", kept) : s;
        }

        void InitFrequency()
        {
            if (baseMhz <= 0) return;
            try
            {
                if (Native.PdhOpenQuery(null, IntPtr.Zero, out query) != 0) { query = IntPtr.Zero; return; }
                if (Native.PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out perfTotal) != 0)
                {
                    Native.PdhCloseQuery(query);
                    query = IntPtr.Zero;
                    return;
                }
                if (Native.PdhAddEnglishCounter(query, @"\Processor Information(*)\% Processor Performance", IntPtr.Zero, out perfEach) != 0)
                    perfEach = IntPtr.Zero;
                Native.PdhCollectQueryData(query);
            }
            catch (Exception ex)
            {
                Log.Write("cpu: счётчик частоты недоступен: " + ex.Message);
                query = IntPtr.Zero;
            }
        }

        public CpuInfo Read()
        {
            var info = new CpuInfo { Name = name, Logical = count, BaseMhz = baseMhz, Cores = new double[count] };
            uint returned;
            if (Native.NtQuerySystemInformation(Native.SystemProcessorPerformanceInformation, buf, (uint)(EntrySize * count), out returned) == 0)
            {
                int n = Math.Min(count, (int)(returned / EntrySize));
                long idleSum = 0, totalSum = 0;
                for (int i = 0; i < n; i++)
                {
                    IntPtr p = buf + i * EntrySize;
                    long idle = Marshal.ReadInt64(p, 0);
                    long total = Marshal.ReadInt64(p, 8) + Marshal.ReadInt64(p, 16);   // ядро (включая простой) + пользователь
                    long dIdle = idle - prevIdle[i], dTotal = total - prevTotal[i];
                    prevIdle[i] = idle;
                    prevTotal[i] = total;
                    if (!primed || dTotal <= 0) continue;
                    info.Cores[i] = Clamp(100.0 * (dTotal - dIdle) / dTotal);
                    if (info.Cores[i] > info.MaxCore) info.MaxCore = info.Cores[i];
                    idleSum += dIdle;
                    totalSum += dTotal;
                }
                if (totalSum > 0) info.Total = Clamp(100.0 * (totalSum - idleSum) / totalSum);
                primed = true;
            }
            ReadFrequency(info);
            return info;
        }

        void ReadFrequency(CpuInfo info)
        {
            if (query == IntPtr.Zero) return;
            try
            {
                if (Native.PdhCollectQueryData(query) != 0) return;
                Native.PDH_FMT_COUNTERVALUE v;
                const uint format = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
                if (Native.PdhGetFormattedCounterValue(perfTotal, format, IntPtr.Zero, out v) == 0 && v.CStatus <= 1 && v.DoubleValue > 0)
                    info.Mhz = baseMhz * v.DoubleValue / 100;
                if (perfEach == IntPtr.Zero) return;

                uint size = arrSize, items;
                int r = Native.PdhGetFormattedCounterArray(perfEach, format, ref size, out items, arr);
                if (r == Native.PDH_MORE_DATA)
                {
                    if (arr != IntPtr.Zero) Marshal.FreeHGlobal(arr);
                    arrSize = size + 1024;
                    arr = Marshal.AllocHGlobal((int)arrSize);
                    size = arrSize;
                    r = Native.PdhGetFormattedCounterArray(perfEach, format, ref size, out items, arr);
                }
                if (r != 0) return;
                double max = 0;
                for (int i = 0; i < items; i++)
                {
                    IntPtr item = arr + i * Native.PdhItemSize;
                    string inst = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    if (inst == null || inst.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if ((uint)Marshal.ReadInt32(item, 8) > 1) continue;
                    double pct = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    if (pct > max) max = pct;
                }
                if (max > 0) info.MaxMhz = baseMhz * max / 100;
            }
            catch (Exception ex)
            {
                Log.Write("cpu: частота: " + ex.Message);
                query = IntPtr.Zero;
            }
        }

        static double Clamp(double v)
        {
            return v < 0 ? 0 : v > 100 ? 100 : v;
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(buf);
            if (arr != IntPtr.Zero) Marshal.FreeHGlobal(arr);
            if (query != IntPtr.Zero) Native.PdhCloseQuery(query);
        }
    }
}
