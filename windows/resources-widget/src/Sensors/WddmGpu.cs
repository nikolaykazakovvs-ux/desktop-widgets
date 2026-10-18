using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourcesWidget
{
    // Видеокарта любого производителя (AMD, Intel, NVIDIA…) через статистику WDDM — те же данные, что у диспетчера задач:
    // загрузка — самый занятый движок (3D, копирование, видео), выделенная и общая видеопамять;
    // температура, вентилятор и частота — если их отдаёт драйвер (WDDM 2.4+, Windows 10 1803+). Прав администратора не требует.
    sealed class WddmGpu : IDisposable
    {
        public sealed class Adapter
        {
            public uint Handle;
            public uint LuidLow;
            public int LuidHigh;
            public string FullName;       // "AMD Radeon RX 6800 XT"
            public int Vendor;            // PCI: 0x1002 AMD, 0x10DE NVIDIA, 0x8086 Intel
            public long Dedicated, Shared;
            public bool Integrated;       // встроенная: своей памяти почти нет, работает с общей
            public int Nodes, Segments;
            public int ClockNode = -1;    // движок 3D: его частота — частота видеокарты
        }

        // KMTQUERYADAPTERINFOTYPE
        const int QaiSegmentSize = 3, QaiRegistryInfo = 8, QaiAdapterType = 15, QaiNodeMetadata = 25, QaiDeviceIds = 31,
            QaiNodePerf = 61, QaiAdapterPerf = 62;
        // D3DKMT_QUERYSTATISTICS_TYPE
        const int StatAdapter = 0, StatSegment = 3, StatNode = 5;
        // D3DKMT_QUERYSTATISTICS (x64): Type @0, AdapterLuid @4, hProcess @16, QueryResult @24 (776 байт), запрос @800, всего 808
        const int StatSize = 808, StatResult = 24, StatQuery = 800;
        // D3DKMT_ADAPTERTYPE
        const int TypeSoftware = 1 << 2, TypeHybridIntegrated = 1 << 5, TypeIndirectDisplay = 1 << 6, TypeComputeOnly = 1 << 11;
        const long IntegratedBelow = 512L << 20;   // меньше 512 МБ своей памяти — встроенная, показываем общую

        readonly IntPtr stat = Marshal.AllocHGlobal(StatSize);
        readonly IntPtr info = Marshal.AllocHGlobal(2112);
        readonly Stopwatch clock = new Stopwatch();
        Adapter cur;
        long[] lastRun;
        long lastTicks;
        int failures;
        DateTime nextTry = DateTime.MinValue;
        bool disposed;

        public Adapter Current { get { return cur; } }

        // Выбирает видеокарту с наибольшей собственной памятью (дискретную, если она есть). false — подходящей нет.
        public bool TryOpen()
        {
            if (cur != null) return true;
            if (DateTime.UtcNow < nextTry) return false;
            List<Adapter> list;
            try { list = Enumerate(); }
            catch (EntryPointNotFoundException)
            {
                nextTry = DateTime.MaxValue;   // Windows старше 8 — статистики WDDM нет
                return false;
            }
            Adapter best = null;
            foreach (var a in list)
                if (best == null || a.Dedicated > best.Dedicated || (a.Dedicated == best.Dedicated && a.Shared > best.Shared)) best = a;
            foreach (var a in list)
                if (a != best) Close(a.Handle);
            if (best == null)
            {
                nextTry = DateTime.UtcNow.AddSeconds(60);
                return false;
            }
            cur = best;
            lastRun = null;
            return true;
        }

        List<Adapter> Enumerate()
        {
            var res = new List<Adapter>();
            var e = new Native.D3dkmtEnumAdapters2();
            if (Native.D3DKMTEnumAdapters2(ref e) != 0 || e.Count == 0) return res;
            uint capacity = Math.Min(e.Count, 64);
            e.Adapters = Marshal.AllocHGlobal((int)capacity * 20);
            try
            {
                e.Count = capacity;
                if (Native.D3DKMTEnumAdapters2(ref e) != 0) return res;
                for (int i = 0; i < e.Count; i++)
                {
                    // D3DKMT_ADAPTERINFO: hAdapter @0, AdapterLuid @4, NumOfSources @12, bPrecisePresentRegionsPreferred @16
                    IntPtr p = e.Adapters + i * 20;
                    var a = new Adapter
                    {
                        Handle = (uint)Marshal.ReadInt32(p, 0),
                        LuidLow = (uint)Marshal.ReadInt32(p, 4),
                        LuidHigh = Marshal.ReadInt32(p, 8),
                    };
                    if (Describe(a)) res.Add(a);
                    else Close(a.Handle);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(e.Adapters);
            }
            return res;
        }

        // Заполняет сведения о видеокарте; false — программная (Microsoft Basic Render), виртуальный дисплей или NPU.
        bool Describe(Adapter a)
        {
            if (Query(a.Handle, QaiAdapterType, 4) != 0) return false;
            int type = Marshal.ReadInt32(info);
            if ((type & (TypeSoftware | TypeIndirectDisplay | TypeComputeOnly)) != 0) return false;

            // D3DKMT_QUERY_DEVICE_IDS: PhysicalAdapterIndex @0, VendorID @4, DeviceID @8 …
            if (Query(a.Handle, QaiDeviceIds, 28) == 0) a.Vendor = Marshal.ReadInt32(info, 4);
            if (a.Vendor == 0x1414) return false;   // Microsoft: программные адаптеры
            // D3DKMT_SEGMENTSIZEINFO: DedicatedVideoMemorySize @0, DedicatedSystemMemorySize @8, SharedSystemMemorySize @16
            if (Query(a.Handle, QaiSegmentSize, 24) != 0) return false;
            a.Dedicated = Marshal.ReadInt64(info, 0);
            a.Shared = Marshal.ReadInt64(info, 16);
            a.Integrated = (type & TypeHybridIntegrated) != 0 || a.Dedicated < IntegratedBelow;
            // D3DKMT_ADAPTERREGISTRYINFO: AdapterString[260] WCHAR @0
            a.FullName = Query(a.Handle, QaiRegistryInfo, 2080) == 0 ? Marshal.PtrToStringUni(info) : null;
            if (string.IsNullOrEmpty(a.FullName)) a.FullName = VendorName(a.Vendor);

            if (Stat(a, StatAdapter, 0) != 0) return false;
            // D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION: NbSegments @0, NodeCount @4
            a.Segments = Math.Min(Marshal.ReadInt32(stat, StatResult), 32);
            a.Nodes = Math.Min(Marshal.ReadInt32(stat, StatResult + 4), 64);
            if (a.Nodes <= 0) return false;
            for (int n = 0; n < a.Nodes && a.ClockNode < 0; n++)
            {
                // D3DKMT_NODEMETADATA (упакована, 78 байт): NodeOrdinalAndAdapterIndex @0, EngineType @4 (1 — 3D)
                Zero(info, 80);
                Marshal.WriteInt32(info, 0, n);
                if (Query(a.Handle, QaiNodeMetadata, 78, false) == 0 && Marshal.ReadInt32(info, 4) == 1) a.ClockNode = n;
            }
            return true;
        }

        public GpuInfo Read()
        {
            var a = cur;
            if (a == null) return null;
            var run = new long[a.Nodes];
            for (int n = 0; n < a.Nodes; n++)
            {
                // D3DKMT_QUERYSTATISTICS_NODE_INFORMATION: GlobalInformation.RunningTime @0, в единицах 100 нс
                if (Stat(a, StatNode, n) != 0)
                {
                    Fail("статистика движка " + n);
                    return null;
                }
                run[n] = Marshal.ReadInt64(stat, StatResult);
            }
            if (!clock.IsRunning) clock.Start();
            long ticks = clock.Elapsed.Ticks;
            double load = 0;
            if (lastRun != null && ticks > lastTicks)
                for (int n = 0; n < a.Nodes; n++)
                    load = Math.Max(load, 100.0 * (run[n] - lastRun[n]) / (ticks - lastTicks));
            lastRun = run;
            lastTicks = ticks;

            long dedicated = 0, shared = 0;
            for (int s = 0; s < a.Segments; s++)
            {
                // D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION: BytesResident @16, Aperture @40 (сегмент в общей памяти)
                if (Stat(a, StatSegment, s) != 0) continue;
                long resident = Marshal.ReadInt64(stat, StatResult + 16);
                if (Marshal.ReadInt32(stat, StatResult + 40) != 0) shared += resident;
                else dedicated += resident;
            }
            failures = 0;

            var g = new GpuInfo
            {
                Name = ShortName(a.FullName, a.Vendor),
                FullName = a.FullName,
                Vendor = VendorName(a.Vendor),
                Source = "Windows (статистика WDDM, как в диспетчере задач)",
                Load = Math.Max(0, Math.Min(100, load)),
                Integrated = a.Integrated,
                SharedUsed = shared,
                SharedTotal = a.Shared,
            };
            if (a.Integrated)
            {
                g.MemShared = true;
                g.MemUsed = shared + dedicated;
                g.MemTotal = a.Shared + a.Dedicated;
            }
            else
            {
                g.MemUsed = dedicated;
                g.MemTotal = a.Dedicated;
            }

            // D3DKMT_ADAPTER_PERFDATA: FanRPM @48, Power @52, Temperature @56 (десятые доли °C). Нули — драйвер не знает.
            Zero(info, 64);
            if (Query(a.Handle, QaiAdapterPerf, 64, false) == 0)
            {
                int temp = Marshal.ReadInt32(info, 56), rpm = Marshal.ReadInt32(info, 48);
                if (temp > 0 && temp < 1500) g.Temp = temp / 10.0;
                if (rpm > 0 && rpm < 20000) g.FanRpm = rpm;
            }
            if (a.ClockNode >= 0)
            {
                // D3DKMT_NODE_PERFDATA: NodeOrdinal @0, PhysicalAdapterIndex @4, Frequency @8 (Гц)
                Zero(info, 64);
                Marshal.WriteInt32(info, 0, a.ClockNode);
                if (Query(a.Handle, QaiNodePerf, 56, false) == 0)
                {
                    long hz = Marshal.ReadInt64(info, 8);
                    if (hz > 0) g.ClockMhz = hz / 1e6;
                }
            }
            return g;
        }

        // Драйвер перезапущен, обновлён или видеокарту отключили — выбрать заново: первые повторы через 3 с, потом раз в минуту.
        void Fail(string what)
        {
            failures++;
            if (failures <= 1) Log.Write("gpu (wddm): нет ответа: " + what);
            if (cur != null) Close(cur.Handle);
            cur = null;
            nextTry = DateTime.UtcNow.AddSeconds(failures <= 5 ? 3 : 60);
        }

        int Query(uint adapter, int type, int size, bool zero = true)
        {
            if (zero) Zero(info, size);
            var q = new Native.D3dkmtQueryAdapterInfo { Adapter = adapter, Type = type, Data = info, Size = (uint)size };
            return Native.D3DKMTQueryAdapterInfo(ref q);
        }

        int Stat(Adapter a, int type, int param)
        {
            Zero(stat, StatSize);
            Marshal.WriteInt32(stat, 0, type);
            Marshal.WriteInt32(stat, 4, (int)a.LuidLow);
            Marshal.WriteInt32(stat, 8, a.LuidHigh);
            Marshal.WriteInt32(stat, StatQuery, param);
            return Native.D3DKMTQueryStatistics(stat);
        }

        static readonly byte[] Zeros = new byte[StatSize];

        static void Zero(IntPtr p, int size)
        {
            Marshal.Copy(Zeros, 0, p, Math.Min(size, Zeros.Length));
        }

        static void Close(uint handle)
        {
            try { Native.D3DKMTCloseAdapter(ref handle); }
            catch { }
        }

        public static string VendorName(int vendor)
        {
            switch (vendor)
            {
                case 0x10DE: return "NVIDIA";
                case 0x1002:
                case 0x1022: return "AMD";
                case 0x8086: return "Intel";
                case 0x5143: return "Qualcomm";
                case 0x13B5: return "Arm";
                default: return "Видеокарта";
            }
        }

        // "NVIDIA GeForce RTX 3070" -> "RTX 3070", "AMD Radeon RX 6800 XT" -> "Radeon RX 6800 XT",
        // "Intel(R) Arc(TM) A770 Graphics" -> "Arc A770 Graphics", "AMD Radeon(TM) Graphics" -> "Radeon Graphics"
        public static string ShortName(string s, int vendor)
        {
            if (string.IsNullOrEmpty(s)) return VendorName(vendor);
            foreach (var junk in new[] { "(R)", "(TM)", "(tm)", "®", "™", "NVIDIA", "GeForce", "AMD", "ATI ", "Intel" })
                s = s.Replace(junk, " ");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.Trim();
            return s.Length > 0 ? s : VendorName(vendor);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (cur != null) Close(cur.Handle);
            cur = null;
            Marshal.FreeHGlobal(stat);
            Marshal.FreeHGlobal(info);
        }
    }
}
