using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourcesWidget
{
    // Все вызовы Windows API в одном месте. Сборка только x64: смещения в структурах ниже посчитаны для 64 бит.
    static class Native
    {
        // ---------- окно, значок, горячая клавиша ----------

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WM_HOTKEY = 0x0312;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);
        public const int ASFW_ANY = -1;
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

        // Панель задач и панель скрытых значков получают фокус от щелчка по значку в трее: возвращать фокус им незачем.
        public static bool IsTaskbar(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            string c = sb.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland";
        }
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hWnd);

        // Сделать окно активным. Если Windows отказала (пользователь как раз работал в другом окне),
        // на миг подключиться к очереди ввода активного окна — так система разрешает смену фокуса.
        public static void ForceForeground(IntPtr h)
        {
            if (GetForegroundWindow() == h || SetForegroundWindow(h) && GetForegroundWindow() == h) return;
            uint other = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            if (other == 0 || other == me || !AttachThreadInput(me, other, true)) return;
            try
            {
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally
            {
                AttachThreadInput(me, other, false);
            }
        }
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public static void AddExStyle(IntPtr h, int add)
        {
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() | (uint)add));
        }

        public static void BringToTop(IntPtr h)
        {
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // ---------- процессор, память, процессы ----------

        public const int SystemProcessInformation = 5;
        public const int SystemProcessorPerformanceInformation = 8;
        public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak;
            public UIntPtr PhysicalTotal, PhysicalAvailable, SystemCache;
            public UIntPtr KernelTotal, KernelPaged, KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, uint length, out uint returned);
        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        // ---------- счётчики производительности (частота процессора) ----------

        public const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
        public const int PDH_MORE_DATA = unchecked((int)0x800007D2);
        public const int PdhItemSize = 24;   // PDH_FMT_COUNTERVALUE_ITEM_W: имя (8) + статус (4+4) + значение (8)

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
        public static extern int PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
        public static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        public static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")]
        public static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
        public static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
        [DllImport("pdh.dll")]
        public static extern int PdhCloseQuery(IntPtr query);

        // ---------- диски ----------

        public const uint IOCTL_DISK_PERFORMANCE = 0x00070020;
        public const uint FILE_SHARE_READ_WRITE = 3, OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISK_PERFORMANCE
        {
            public long BytesRead, BytesWritten, ReadTime, WriteTime, IdleTime;
            public uint ReadCount, WriteCount, QueueDepth, SplitCount;
            public long QueryTime;
            public uint StorageDeviceNumber;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string StorageManagerName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, out DISK_PERFORMANCE outBuf, uint outSize, out uint returned, IntPtr overlapped);

        // ---------- сеть ----------

        // MIB_IF_ROW2 (1352 байта): смещения нужных полей.
        public const int IfRowSize = 1352, IfTableFirstRow = 8;
        public const int IfLuid = 0, IfAlias = 28, IfDescription = 542, IfFlags = 1152, IfOperStatus = 1156;
        public const int IfReceiveLinkSpeed = 1200, IfInOctets = 1208, IfOutOctets = 1280;
        public const byte IfFlagFilter = 2, IfFlagConnector = 4;
        public const int IfOperStatusUp = 1;

        [DllImport("iphlpapi.dll")]
        public static extern int GetIfTable2(out IntPtr table);
        [DllImport("iphlpapi.dll")]
        public static extern void FreeMibTable(IntPtr table);

        // ---------- общая память программ мониторинга (HWiNFO, AIDA64) ----------

        public const uint FILE_MAP_READ = 0x0004;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);
        [DllImport("kernel32.dll")]
        public static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        // ---------- видеокарта NVIDIA (NVML ставится вместе с драйвером) ----------

        public const int NVML_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlUtilization
        {
            public uint Gpu, Memory;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlMemory
        {
            public ulong Total, Free, Used;
        }

        // Версия 2 не считает занятой память, зарезервированную драйвером (как nvidia-smi и диспетчер задач).
        [StructLayout(LayoutKind.Sequential)]
        public struct NvmlMemoryV2
        {
            public uint Version;
            public ulong Total, Reserved, Free, Used;
        }

        public const uint NvmlMemoryV2Version = 40 | (2u << 24);   // sizeof(nvmlMemory_v2_t) | (2 << 24)

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        public static extern int NvmlInit();
        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        public static extern int NvmlShutdown();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        public static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
        public static extern int NvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
        public static extern int NvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization util);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
        public static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo_v2")]
        public static extern int NvmlDeviceGetMemoryInfoV2(IntPtr device, ref NvmlMemoryV2 memory);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
        public static extern int NvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
        public static extern int NvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")]
        public static extern int NvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
        public static extern int NvmlDeviceGetClockInfo(IntPtr device, int clockType, out uint mhz);

        // ---------- видеокарта любого производителя: статистика WDDM (gdi32), как у диспетчера задач ----------
        // Структуры — из d3dkmthk.h (WDK 10.0.26100); смещения для x64 — в WddmGpu.cs.

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtQueryAdapterInfo
        {
            public uint Adapter;
            public int Type;
            public IntPtr Data;
            public uint Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3dkmtEnumAdapters2
        {
            public uint Count;
            public IntPtr Adapters;   // D3DKMT_ADAPTERINFO[Count], по 20 байт
        }

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTEnumAdapters2(ref D3dkmtEnumAdapters2 e);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryAdapterInfo(ref D3dkmtQueryAdapterInfo q);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryStatistics(IntPtr q);
        [DllImport("gdi32.dll")]
        public static extern int D3DKMTCloseAdapter(ref uint adapter);
    }
}
