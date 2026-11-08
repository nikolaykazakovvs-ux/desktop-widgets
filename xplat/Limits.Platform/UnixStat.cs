using System;
using System.Runtime.InteropServices;

namespace DesktopWidgets.Limits.Platform
{
    // Владелец и права файла без перехода по ссылке (lstat). В .NET нет управляемого API для uid владельца,
    // поэтому раскладка struct stat задана для проверенных ABI: macOS arm64/x64, Linux glibc x64/arm64.
    // Неизвестная ABI — исключение (писатель телеметрии тогда не стартует: fail closed).
    internal static unsafe class UnixStat
    {
        public readonly struct Info
        {
            public readonly uint Uid, Mode;
            public Info(uint uid, uint mode) { Uid = uid; Mode = mode; }
            public bool IsLink => (Mode & 0xF000) == 0xA000;
            public bool IsDir => (Mode & 0xF000) == 0x4000;
            public bool IsFile => (Mode & 0xF000) == 0x8000;
            public uint Permissions => Mode & 0xFFF;
        }

        static readonly delegate* unmanaged<byte*, byte*, int> lstat;
        static readonly delegate* unmanaged<uint> geteuid;
        static readonly int uidOffset, modeOffset;
        static readonly bool modeIs16;

        static UnixStat()
        {
            if (OperatingSystem.IsWindows()) return;
            IntPtr lib = NativeLibrary.Load(OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6");
            geteuid = (delegate* unmanaged<uint>)NativeLibrary.GetExport(lib, "geteuid");
            var arch = RuntimeInformation.OSArchitecture;
            if (OperatingSystem.IsMacOS())
            {
                // struct stat (64-bit inode): dev_t(4) mode_t(2) nlink_t(2) ino_t(8) uid_t(4)
                lstat = (delegate* unmanaged<byte*, byte*, int>)NativeLibrary.GetExport(lib, arch == Architecture.X64 ? "lstat$INODE64" : "lstat");
                modeOffset = 4; modeIs16 = true; uidOffset = 16;
            }
            else if (arch == Architecture.X64 || arch == Architecture.Arm64)
            {
                // glibc >= 2.33 экспортирует lstat. x86_64: dev(8) ino(8) nlink(8) mode(4) uid(4); generic(arm64): dev(8) ino(8) mode(4) nlink(4) uid(4)
                if (NativeLibrary.TryGetExport(lib, "lstat", out IntPtr fn)) lstat = (delegate* unmanaged<byte*, byte*, int>)fn;
                if (arch == Architecture.X64) { modeOffset = 24; uidOffset = 28; }
                else { modeOffset = 16; uidOffset = 24; }
            }
        }

        public static bool Available => lstat != null && geteuid != null;

        public static uint EffectiveUid
        {
            get
            {
                if (geteuid == null) throw new PlatformNotSupportedException("unix_stat_unavailable");
                return geteuid();
            }
        }

        // null — файла нет.
        public static Info? LStat(string path)
        {
            if (!Available) throw new PlatformNotSupportedException("unix_stat_unavailable");
            byte[] name = System.Text.Encoding.UTF8.GetBytes(path + "\0");
            byte* buf = stackalloc byte[512];
            fixed (byte* n = name)
            {
                if (lstat(n, buf) != 0)
                {
                    int errno = Marshal.GetLastSystemError();
                    if (errno == 2 || errno == 20) return null;   // ENOENT, ENOTDIR
                    // Без SetLastError: на всякий случай — отсутствие файла проверяется управляемо.
                    if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path) && new System.IO.FileInfo(path).LinkTarget == null) return null;
                    throw new System.IO.IOException("lstat_failed");
                }
            }
            uint mode = modeIs16 ? *(ushort*)(buf + modeOffset) : *(uint*)(buf + modeOffset);
            return new Info(*(uint*)(buf + uidOffset), mode);
        }
    }
}
