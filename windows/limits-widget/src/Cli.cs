using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LimitsWidget
{
    sealed class CliResult
    {
        public int ExitCode = -1;
        public string StdOut = "";
        public string StdErr = "";
        public bool TimedOut;
        public string StartError;

        public bool Ok { get { return StartError == null && !TimedOut && ExitCode == 0; } }

        public string Describe()
        {
            if (StartError != null) return "не запустился: " + StartError;
            if (TimedOut) return "не ответил вовремя";
            string first = Cli.FirstLine(StdErr) ?? Cli.FirstLine(StdOut);
            return "код " + ExitCode + (first != null ? ": " + first : "");
        }
    }

    // Запуск CLI ассистентов: авторизацию и обновление токенов делает сам CLI, виджет их не касается.
    static class Cli
    {
        static readonly string[] Exts = { ".exe", ".cmd", ".bat" };

        public static string Resolve(string configured, string name)
        {
            if (!string.IsNullOrEmpty(configured))
            {
                string p = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
                return File.Exists(p) ? p : null;
            }
            foreach (var dir in SearchDirs())
                foreach (var ext in Exts)
                {
                    string p = Path.Combine(dir, name + ext);
                    if (File.Exists(p)) return p;
                }
            return null;
        }

        // PATH процесса + пользовательский и системный PATH из реестра: виджет мог быть запущен со старым окружением.
        static List<string> SearchDirs()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            var parts = new List<string>();
            parts.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
            parts.AddRange(RegistryPath(Registry.CurrentUser, "Environment"));
            parts.AddRange(RegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"));
            foreach (var raw in parts)
            {
                string d = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (d.Length == 0 || !seen.Add(d)) continue;
                bool exists;
                try { exists = Directory.Exists(d); }
                catch { exists = false; }
                if (exists) result.Add(d);
            }
            return result;
        }

        static string[] RegistryPath(RegistryKey hive, string sub)
        {
            try
            {
                using (var k = hive.OpenSubKey(sub))
                {
                    var v = k == null ? null : k.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    return v == null ? new string[0] : v.Split(';');
                }
            }
            catch
            {
                return new string[0];
            }
        }

        public static ProcessStartInfo StartInfo(string exe, string args)
        {
            var psi = new ProcessStartInfo();
            string ext = Path.GetExtension(exe).ToLowerInvariant();
            if (ext == ".cmd" || ext == ".bat")
            {
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = "/d /s /c \"\"" + exe + "\" " + args + "\"";
            }
            else
            {
                psi.FileName = exe;
                psi.Arguments = args;
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.WorkingDirectory = WorkDir();
            psi.EnvironmentVariables["PATH"] = string.Join(";", SearchDirs());
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            return psi;
        }

        static string WorkDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "LimitsWidget");
            try { Directory.CreateDirectory(d); }
            catch { return Path.GetTempPath(); }
            return d;
        }

        // maxProcesses > 0 — CLI запускается в Job-объекте с лимитом процессов: так agy не может породить
        // фоновый апдейтер (тот вызывает `agy --version` с видимой консолью) и MCP-серверы через npx.
        // В любом случае все потомки CLI убиваются при закрытии Job после опроса.
        public static CliResult Run(string exe, string args, int timeoutMs, int maxProcesses)
        {
            var res = new CliResult();
            Process p;
            IntPtr job = Job.Create(maxProcesses);
            try
            {
                p = Process.Start(StartInfo(exe, args));
                Job.Assign(job, p);
            }
            catch (Exception ex)
            {
                Job.Close(job);
                res.StartError = ex.GetType().Name + ": " + ex.Message;
                return res;
            }
            using (p)
            try
            {
                try { p.StandardInput.Close(); }
                catch { }
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    res.TimedOut = true;
                    KillTree(p);
                }
                else
                {
                    p.WaitForExit();
                    res.ExitCode = p.ExitCode;
                }
                try { if (so.Wait(3000)) res.StdOut = so.Result ?? ""; }
                catch { }
                try { if (se.Wait(1000)) res.StdErr = se.Result ?? ""; }
                catch { }
            }
            finally
            {
                Job.Close(job);
            }
            return res;
        }

        public static void KillTree(Process p)
        {
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/T /F /PID " + p.Id);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (var k = Process.Start(psi)) k.WaitForExit(5000);
            }
            catch
            {
            }
            try { if (!p.HasExited) p.Kill(); }
            catch { }
        }

        public static class Job
        {
            const int JobObjectExtendedLimitInformation = 9;
            const uint LimitActiveProcess = 0x00000008;
            const uint LimitKillOnJobClose = 0x00002000;

            [StructLayout(LayoutKind.Sequential)]
            struct BasicLimit
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct IoCounters
            {
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct ExtendedLimit
            {
                public BasicLimit Basic;
                public IoCounters Io;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int size);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool CloseHandle(IntPtr handle);

            public static IntPtr Create(int maxProcesses)
            {
                IntPtr job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero) return IntPtr.Zero;
                var info = new ExtendedLimit();
                info.Basic.LimitFlags = LimitKillOnJobClose;
                if (maxProcesses > 0)
                {
                    info.Basic.LimitFlags |= LimitActiveProcess;
                    info.Basic.ActiveProcessLimit = (uint)maxProcesses;
                }
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(typeof(ExtendedLimit))))
                    Log.Write("job: SetInformationJobObject failed, error " + Marshal.GetLastWin32Error());
                return job;
            }

            public static void Assign(IntPtr job, Process p)
            {
                if (job != IntPtr.Zero && !AssignProcessToJobObject(job, p.Handle))
                    Log.Write("job: AssignProcessToJobObject failed, error " + Marshal.GetLastWin32Error());
            }

            public static void Close(IntPtr job)
            {
                if (job != IntPtr.Zero) CloseHandle(job);
            }
        }

        public static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line == null) return null;
            return line.Length > 140 ? line.Substring(0, 140) + "…" : line;
        }
    }
}
