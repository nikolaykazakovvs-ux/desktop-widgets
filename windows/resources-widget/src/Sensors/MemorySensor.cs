using System;
using System.Runtime.InteropServices;

namespace ResourcesWidget
{
    sealed class MemorySensor
    {
        public MemInfo Read()
        {
            Native.PERFORMANCE_INFORMATION p;
            uint size = (uint)Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION));
            if (!Native.GetPerformanceInfo(out p, size)) return null;
            long page = (long)p.PageSize.ToUInt64();
            var m = new MemInfo();
            m.Total = (long)p.PhysicalTotal.ToUInt64() * page;
            m.Available = (long)p.PhysicalAvailable.ToUInt64() * page;
            m.Cache = (long)p.SystemCache.ToUInt64() * page;
            m.CommitTotal = (long)p.CommitTotal.ToUInt64() * page;
            m.CommitLimit = (long)p.CommitLimit.ToUInt64() * page;
            m.Processes = (int)p.ProcessCount;
            m.Threads = (int)p.ThreadCount;
            m.Handles = (int)p.HandleCount;
            return m;
        }
    }
}
