using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourcesWidget
{
    // Один системный вызов возвращает все процессы с их временем процессора и памятью;
    // открывать процессы и права администратора не нужны. Процессы с одним именем складываются.
    sealed class ProcessSensor : IDisposable
    {
        // SYSTEM_PROCESS_INFORMATION, x64
        const int OffNext = 0, OffPrivateWorkingSet = 8, OffCreateTime = 32, OffUserTime = 40, OffKernelTime = 48;
        const int OffNameLength = 56, OffNameBuffer = 64, OffPid = 80;

        struct Seen
        {
            public long Created, Cpu;
        }

        readonly int cores = Environment.ProcessorCount;
        readonly Stopwatch clock = Stopwatch.StartNew();
        Dictionary<long, Seen> prev = new Dictionary<long, Seen>();
        IntPtr buf;
        int size = 1 << 20;
        long prevTicks;
        bool primed;

        public ProcessSensor()
        {
            buf = Marshal.AllocHGlobal(size);
        }

        // sort: "cpu" или "memory". Первый вызов после паузы возвращает память без загрузки процессора.
        public List<ProcInfo> Read(string sort, int top)
        {
            uint needed;
            int status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, buf, (uint)size, out needed);
            for (int attempt = 0; status == Native.STATUS_INFO_LENGTH_MISMATCH && attempt < 4; attempt++)
            {
                size = Math.Max(size * 2, (int)needed + (256 << 10));
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
                status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, buf, (uint)size, out needed);
            }
            if (status != 0) return null;

            long now = clock.Elapsed.Ticks;
            double wall = now - prevTicks;                      // 100-нс интервалы, как и время процессора
            bool fresh = primed && wall > 0 && wall < TimeSpan.FromSeconds(10).Ticks;
            var next = new Dictionary<long, Seen>();
            var groups = new Dictionary<string, ProcInfo>(StringComparer.OrdinalIgnoreCase);

            IntPtr p = buf;
            while (true)
            {
                long pid = Marshal.ReadInt64(p, OffPid);
                if (pid != 0)   // 0 — «Бездействие системы»
                {
                    long created = Marshal.ReadInt64(p, OffCreateTime);
                    long cpu = Marshal.ReadInt64(p, OffUserTime) + Marshal.ReadInt64(p, OffKernelTime);
                    next[pid] = new Seen { Created = created, Cpu = cpu };

                    string name = null;
                    int length = (ushort)Marshal.ReadInt16(p, OffNameLength);
                    IntPtr text = Marshal.ReadIntPtr(p, OffNameBuffer);
                    if (length > 0 && text != IntPtr.Zero) name = Marshal.PtrToStringUni(text, length / 2);
                    if (string.IsNullOrEmpty(name)) name = pid == 4 ? "System" : "pid " + pid;
                    else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);

                    ProcInfo g;
                    if (!groups.TryGetValue(name, out g))
                    {
                        g = new ProcInfo { Name = name };
                        groups[name] = g;
                    }
                    g.Count++;
                    g.Memory += Marshal.ReadInt64(p, OffPrivateWorkingSet);
                    Seen was;
                    if (fresh && prev.TryGetValue(pid, out was) && was.Created == created && cpu >= was.Cpu)
                        g.Cpu += 100.0 * (cpu - was.Cpu) / (wall * cores);
                }
                int offset = Marshal.ReadInt32(p, OffNext);
                if (offset == 0) break;
                p += offset;
            }
            prev = next;
            prevTicks = now;
            primed = true;

            var list = new List<ProcInfo>(groups.Values);
            if (sort == "memory") list.Sort((a, b) => b.Memory.CompareTo(a.Memory));
            else list.Sort((a, b) =>
            {
                int c = b.Cpu.CompareTo(a.Cpu);
                return c != 0 ? c : b.Memory.CompareTo(a.Memory);
            });
            if (list.Count > top) list.RemoveRange(top, list.Count - top);
            foreach (var g in list) g.Cpu = Math.Min(100, g.Cpu);
            return list;
        }

        public void Dispose()
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            buf = IntPtr.Zero;
        }
    }
}
