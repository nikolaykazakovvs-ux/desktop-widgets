using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("LimitsWidget Test Fixture")]
[assembly: AssemblyProduct("LimitsWidget")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Isolated helper for LimitsWidget updater integration tests:
// - Creates bounded NTFS junctions in scratch directories without elevation.
// - Acts as a synthetic legacy LimitsWidget.exe / child process in scratch directories
//   without touching global mutexes/events or real user installations.
static class LimitsUpdaterTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string file, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle file, uint control, byte[] input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    static void CreateJunction(string junctionPath, string targetPath)
    {
        string fullJunction = Path.GetFullPath(junctionPath);
        string fullTarget = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(fullJunction);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + fullTarget);
        byte[] printable = Encoding.Unicode.GetBytes(fullTarget);
        byte[] data = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        Array.Copy(BitConverter.GetBytes(0xA0000003u), 0, data, 0, 4);
        Array.Copy(BitConverter.GetBytes((ushort)(data.Length - 8)), 0, data, 4, 2);
        Array.Copy(BitConverter.GetBytes((ushort)substitute.Length), 0, data, 10, 2);
        Array.Copy(BitConverter.GetBytes((ushort)(substitute.Length + 2)), 0, data, 12, 2);
        Array.Copy(BitConverter.GetBytes((ushort)printable.Length), 0, data, 14, 2);
        Array.Copy(substitute, 0, data, 16, substitute.Length);
        Array.Copy(printable, 0, data, 16 + substitute.Length + 2, printable.Length);
        using (var handle = CreateFile(fullJunction, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            uint returned;
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, data, (uint)data.Length, IntPtr.Zero, 0, out returned, IntPtr.Zero))
            {
                throw new IOException("Could not create NTFS junction fixture: win32=" + Marshal.GetLastWin32Error());
            }
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--create-junction")
        {
            CreateJunction(args[1], args[2]);
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--sleep")
        {
            int seconds = int.Parse(args[1]);
            if (args.Length >= 4 && args[2] == "--spawn-child")
            {
                string childExe = args[3];
                string pidFile = args.Length >= 5 ? args[4] : null;
                var psi = new ProcessStartInfo(childExe, "--sleep " + seconds)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(childExe)
                };
                var child = Process.Start(psi);
                if (!string.IsNullOrEmpty(pidFile) && child != null)
                {
                    File.WriteAllText(pidFile, child.Id.ToString(), new UTF8Encoding(false));
                }
            }
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return 0;
        }
        return 0;
    }
}
