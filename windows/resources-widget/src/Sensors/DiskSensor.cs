using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ResourcesWidget
{
    // Место — через DriveInfo (раз в 15 с), обмен — системные счётчики тома (IOCTL_DISK_PERFORMANCE).
    // Том открывается без прав доступа и сразу закрывается: открытый дескриптор мешал бы безопасному извлечению.
    // Счётчики ведёт драйвер, сам диск запрос не будит.
    sealed class DiskSensor
    {
        sealed class Volume
        {
            public string Letter, Label;
            public long Total, Free;
            public long Read, Written, Idle, Query;
            public bool Primed, HasCounters;
        }

        static readonly uint PerfSize = (uint)Marshal.SizeOf(typeof(Native.DISK_PERFORMANCE));
        readonly List<string> wanted;
        readonly Dictionary<string, Volume> volumes = new Dictionary<string, Volume>();
        DateTime nextScan = DateTime.MinValue;

        public DiskSensor(List<string> wanted)
        {
            this.wanted = wanted;
        }

        void Scan()
        {
            var seen = new HashSet<string>();
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch (Exception ex)
            {
                Log.Write("disks: " + ex.Message);
                return;
            }
            foreach (var d in drives)
            {
                string letter = Config.NormalizeDisk(d.Name);
                if (letter == null) continue;
                bool listed = wanted.Contains(letter);
                if (wanted.Count > 0 ? !listed : d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                try
                {
                    if (!d.IsReady) continue;
                    Volume v;
                    if (!volumes.TryGetValue(letter, out v))
                    {
                        v = new Volume { Letter = letter };
                        Native.DISK_PERFORMANCE p;
                        v.HasCounters = Query(letter, out p);
                        // без счётчиков — виртуальный диск (облако, RAM-диск); показываем, только если он назван в config.json
                        if (!v.HasCounters && !listed) continue;
                        volumes[letter] = v;
                    }
                    v.Label = d.VolumeLabel;
                    v.Total = d.TotalSize;
                    v.Free = d.TotalFreeSpace;
                    seen.Add(letter);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            foreach (string gone in new List<string>(volumes.Keys))
                if (!seen.Contains(gone)) volumes.Remove(gone);
        }

        static bool Query(string letter, out Native.DISK_PERFORMANCE p)
        {
            p = new Native.DISK_PERFORMANCE();
            using (var h = Native.CreateFile(@"\\.\" + letter, 0, Native.FILE_SHARE_READ_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                uint returned;
                return Native.DeviceIoControl(h, Native.IOCTL_DISK_PERFORMANCE, IntPtr.Zero, 0, out p, PerfSize, out returned, IntPtr.Zero);
            }
        }

        // scan — можно ли обновить список томов и свободное место (раз в 15 с)
        public List<DiskInfo> Read(bool scan)
        {
            if (scan && DateTime.UtcNow >= nextScan)
            {
                Scan();
                nextScan = DateTime.UtcNow.AddSeconds(15);
            }
            var list = new List<DiskInfo>();
            var letters = new List<string>(volumes.Keys);
            letters.Sort(StringComparer.Ordinal);
            foreach (string letter in letters)
            {
                var v = volumes[letter];
                var info = new DiskInfo { Letter = v.Letter, Label = v.Label, Total = v.Total, Free = v.Free };
                Native.DISK_PERFORMANCE p;
                if (v.HasCounters && Query(letter, out p))
                {
                    double seconds = (p.QueryTime - v.Query) / 1e7;
                    // после долгой паузы в опросе (сон компьютера) среднее за всю паузу не показываем
                    if (v.Primed && seconds > 0.05 && seconds < 10 && p.BytesRead >= v.Read && p.BytesWritten >= v.Written)
                    {
                        info.ReadBps = (p.BytesRead - v.Read) / seconds;
                        info.WriteBps = (p.BytesWritten - v.Written) / seconds;
                        double idle = (p.IdleTime - v.Idle) / 1e7;
                        info.Active = Math.Min(100, Math.Max(0, 100 * (1 - idle / seconds)));
                    }
                    v.Read = p.BytesRead;
                    v.Written = p.BytesWritten;
                    v.Idle = p.IdleTime;
                    v.Query = p.QueryTime;
                    v.Primed = true;
                }
                list.Add(info);
            }
            return list;
        }
    }
}
