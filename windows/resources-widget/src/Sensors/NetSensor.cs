using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ResourcesWidget
{
    // Считаются только физические адаптеры (с разъёмом, не фильтры): трафик VPN и виртуальных
    // коммутаторов уже входит в их счётчики и дважды не складывается.
    sealed class NetSensor
    {
        readonly Dictionary<long, long[]> prev = new Dictionary<long, long[]>();   // LUID -> { принято, отдано }

        public NetInfo Read(double seconds)
        {
            IntPtr table;
            if (Native.GetIfTable2(out table) != 0) return null;
            try
            {
                int rows = Marshal.ReadInt32(table);
                IntPtr row = table + Native.IfTableFirstRow;
                var info = new NetInfo();
                var seen = new HashSet<long>();
                long down = 0, up = 0, best = -1;
                bool any = false;
                for (int i = 0; i < rows; i++, row += Native.IfRowSize)
                {
                    byte flags = Marshal.ReadByte(row, Native.IfFlags);
                    if ((flags & Native.IfFlagConnector) == 0 || (flags & Native.IfFlagFilter) != 0) continue;
                    long luid = Marshal.ReadInt64(row, Native.IfLuid);
                    long rx = Marshal.ReadInt64(row, Native.IfInOctets), tx = Marshal.ReadInt64(row, Native.IfOutOctets);
                    seen.Add(luid);
                    long[] was;
                    if (prev.TryGetValue(luid, out was))
                    {
                        if (rx >= was[0]) down += rx - was[0];
                        if (tx >= was[1]) up += tx - was[1];
                    }
                    prev[luid] = new[] { rx, tx };
                    if (Marshal.ReadInt32(row, Native.IfOperStatus) != Native.IfOperStatusUp) continue;
                    any = true;
                    info.TotalDown += rx;
                    info.TotalUp += tx;
                    if (rx + tx > best)
                    {
                        best = rx + tx;
                        info.Name = Marshal.PtrToStringUni(row + Native.IfAlias);
                        info.LinkBps = Marshal.ReadInt64(row, Native.IfReceiveLinkSpeed);
                    }
                }
                foreach (long gone in new List<long>(prev.Keys))
                    if (!seen.Contains(gone)) prev.Remove(gone);
                if (!any) return null;
                if (seconds > 0.05)
                {
                    info.DownBps = down / seconds;
                    info.UpBps = up / seconds;
                }
                return info;
            }
            finally
            {
                Native.FreeMibTable(table);
            }
        }
    }
}
