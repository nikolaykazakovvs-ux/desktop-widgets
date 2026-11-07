using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("LimitsWidget Test Fixture")]
[assembly: AssemblyProduct("LimitsWidget")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// Isolated helper for LimitsWidget updater integration tests:
// - Creates bounded NTFS junctions in scratch directories without elevation.
// - Acts as a synthetic legacy/1.1 LimitsWidget.exe or child process in scratch directories
//   with graceful Win32 WM_CLOSE and named-event shutdown controls (no forced kills, no global mutexes).
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

    sealed class GracefulHiddenWindow : NativeWindow, IDisposable
    {
        readonly bool ignoreClose;
        readonly ManualResetEvent gracefulStopRequested;

        public GracefulHiddenWindow(bool ignoreClose, ManualResetEvent gracefulStopRequested)
        {
            this.ignoreClose = ignoreClose;
            this.gracefulStopRequested = gracefulStopRequested;
            var cp = new CreateParams
            {
                Caption = "\u041B\u0438\u043C\u0438\u0442\u044B \u043D\u0435\u0439\u0440\u043E\u0441\u0435\u0442\u0435\u0439",
                Style = 0,
                ExStyle = 0x00000080, // WS_EX_TOOLWINDOW
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_CLOSE = 0x0010;
            const int WM_QUIT = 0x0012;
            if (m.Msg == WM_CLOSE || m.Msg == WM_QUIT)
            {
                if (!ignoreClose)
                {
                    gracefulStopRequested.Set();
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }
    }

    static void WriteStateAtomically(string statePath, int tick, bool finalSave)
    {
        try
        {
            string tmp = statePath + ".tmp";
            string json = "{\"left\":140,\"top\":95,\"topmost\":true,\"theme\":\"dark\",\"unknownStateRootField\":\"keep-state-root\",\"unknownNestedObject\":{\"flag\":true,\"values\":[10,20,30]},\"creditLog\":{\"antigravity\":[[1762000000,120.5]]},\"writerTick\":" + tick + ",\"closedCleanly\":" + (finalSave ? "true" : "false") + "}";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(statePath))
            {
                File.Replace(tmp, statePath, null);
            }
            else
            {
                File.Move(tmp, statePath);
            }
        }
        catch
        {
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--create-junction")
        {
            CreateJunction(args[1], args[2]);
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--signal-event")
        {
            try
            {
                using (var evt = EventWaitHandle.OpenExisting(args[1]))
                {
                    evt.Set();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return 0;
        }

        int maxSeconds = 20;
        bool ignoreClose = false;
        string stopEventName = null;
        string readyFile = null;
        string stateWritePath = null;
        int stateWriteIntervalMs = 50;
        string ownedChildExe = null;
        string ownedChildPidFile = null;
        string foreignChildExe = null;
        string foreignChildPidFile = null;
        string foreignChildStopEvent = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--sleep" && i + 1 < args.Length)
            {
                int parsed;
                if (int.TryParse(args[++i], out parsed) && parsed > 0) maxSeconds = Math.Min(parsed, 30);
            }
            else if (arg == "--ignore-close")
            {
                ignoreClose = true;
            }
            else if (arg == "--stop-event" && i + 1 < args.Length)
            {
                stopEventName = args[++i];
            }
            else if (arg == "--ready-file" && i + 1 < args.Length)
            {
                readyFile = args[++i];
            }
            else if (arg == "--write-state" && i + 2 < args.Length)
            {
                stateWritePath = args[++i];
                int.TryParse(args[++i], out stateWriteIntervalMs);
                if (stateWriteIntervalMs < 10) stateWriteIntervalMs = 10;
            }
            else if (arg == "--spawn-child" && i + 2 < args.Length)
            {
                ownedChildExe = args[++i];
                ownedChildPidFile = args[++i];
            }
            else if (arg == "--spawn-foreign-child" && i + 3 < args.Length)
            {
                foreignChildExe = args[++i];
                foreignChildPidFile = args[++i];
                foreignChildStopEvent = args[++i];
            }
        }

        int myPid = Process.GetCurrentProcess().Id;
        string pidEventName = "Local\\LimitsWidget.GracefulExit." + myPid;
        string ownedChildStopEventName = null;

        using (var stopTriggered = new ManualResetEvent(false))
        using (var pidExitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, pidEventName))
        using (var customStopEvent = !string.IsNullOrEmpty(stopEventName) ? new EventWaitHandle(false, EventResetMode.ManualReset, stopEventName) : null)
        using (var window = new GracefulHiddenWindow(ignoreClose, stopTriggered))
        {
            if (!string.IsNullOrEmpty(ownedChildExe))
            {
                ownedChildStopEventName = "Local\\LimitsWidget.OwnedChildExit." + myPid + "." + Guid.NewGuid().ToString("N");
                var psi = new ProcessStartInfo(ownedChildExe, "--sleep " + maxSeconds + " --stop-event " + ownedChildStopEventName)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(ownedChildExe)
                };
                var child = Process.Start(psi);
                if (!string.IsNullOrEmpty(ownedChildPidFile) && child != null)
                {
                    File.WriteAllText(ownedChildPidFile, child.Id.ToString(), new UTF8Encoding(false));
                }
            }

            if (!string.IsNullOrEmpty(foreignChildExe))
            {
                string fEvtArg = !string.IsNullOrEmpty(foreignChildStopEvent) ? (" --stop-event " + foreignChildStopEvent) : "";
                var psi = new ProcessStartInfo(foreignChildExe, "--sleep " + maxSeconds + fEvtArg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(foreignChildExe)
                };
                var fChild = Process.Start(psi);
                if (!string.IsNullOrEmpty(foreignChildPidFile) && fChild != null)
                {
                    File.WriteAllText(foreignChildPidFile, fChild.Id.ToString(), new UTF8Encoding(false));
                }
            }

            if (!string.IsNullOrEmpty(readyFile))
            {
                File.WriteAllText(readyFile, myPid.ToString(), new UTF8Encoding(false));
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
            DateTime nextStateWrite = DateTime.UtcNow;
            int tick = 1;

            while (DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                if (stopTriggered.WaitOne(0)) break;
                if (!ignoreClose && pidExitEvent.WaitOne(0)) break;
                if (customStopEvent != null && customStopEvent.WaitOne(0)) break;

                if (!string.IsNullOrEmpty(stateWritePath) && DateTime.UtcNow >= nextStateWrite)
                {
                    WriteStateAtomically(stateWritePath, tick++, false);
                    nextStateWrite = DateTime.UtcNow.AddMilliseconds(stateWriteIntervalMs);
                }

                Thread.Sleep(20);
            }

            // Simulate WidgetWindow.Closing -> SavePosition() -> state.Save() on graceful exit
            if (!string.IsNullOrEmpty(stateWritePath))
            {
                WriteStateAtomically(stateWritePath, tick, true);
            }

            // Simulate WidgetWindow.Closed -> KimiServerManager.Shared.Dispose() stopping owned child server on graceful exit
            if (!string.IsNullOrEmpty(ownedChildStopEventName))
            {
                try
                {
                    using (var childEvt = EventWaitHandle.OpenExisting(ownedChildStopEventName))
                    {
                        childEvt.Set();
                    }
                }
                catch
                {
                }
            }
        }

        return 0;
    }
}
