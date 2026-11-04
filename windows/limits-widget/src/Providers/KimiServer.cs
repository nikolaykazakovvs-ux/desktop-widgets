using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LimitsWidget
{
    // Свой скрытый сервер Kimi Code (`kimi web --no-open`) — только когда готового локального сервера нет и это разрешено
    // (services.kimi.startServer). Сервер слушает только loopback и требует bearer-токен (флаги --host и
    // --dangerous-bypass-auth не передаются, отключённую проверку токена виджет распознаёт и такой сервер гасит).
    //
    // Владение: процесс создаётся приостановленным, сразу попадает в Job с KILL_ON_JOB_CLOSE и только потом запускается,
    // поэтому всё его дерево (cmd-шим, node, потомки) — в Job. Job закрывается при выходе виджета, а при падении виджета его
    // закрывает Windows — дерево завершается в обоих случаях. Сворачивание в трей сервер не трогает. Чужие серверы (desktop,
    // запущенный пользователем `kimi web`) виджет не останавливает никогда.
    //
    // Вывод сервера печатает bearer-токен, поэтому stdout/stderr направлены в NUL: в память и журнал виджета он не попадает.
    // Дочернему процессу наследуется только этот дескриптор (PROC_THREAD_ATTRIBUTE_HANDLE_LIST) — трубы других CLI не утекают.
    // Сессий LLM виджет не создаёт, OAuth не продлевает: вход и его продление — дело самого Kimi Code.
    sealed class KimiServerManager : IDisposable
    {
        public static readonly KimiServerManager Shared = new KimiServerManager();

        public enum Launch { Running, Started, Backoff, Disabled, Failed }

        static readonly TimeSpan MinGap = TimeSpan.FromSeconds(60);       // не чаще одного запуска в минуту
        static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
        static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(10);   // проработал столько — прошлые сбои забыты

        readonly object gate = new object();
        IntPtr job, process;
        int pid;
        DateTime startedAt, lastLaunch, nextAllowed;
        int failures, launches;
        bool disabled, disposed;
        string disabledReason;

        internal Func<DateTime> UtcNow = () => DateTime.UtcNow;

        public int Launches { get { lock (gate) return launches; } }
        public int Failures { get { lock (gate) return failures; } }
        public DateTime NextAllowedUtc { get { lock (gate) return nextAllowed; } }
        public string DisabledReason { get { lock (gate) return disabledReason; } }

        public bool IsRunning
        {
            get { lock (gate) { ReapLocked(); return process != IntPtr.Zero; } }
        }

        public int RootPid { get { lock (gate) return process != IntPtr.Zero ? pid : 0; } }

        // pid относится к дереву своего сервера (сам процесс или его потомок).
        public bool Owns(int candidatePid)
        {
            lock (gate) return job != IntPtr.Zero && Cli.Job.Contains(job, candidatePid);
        }

        // Запустить свой сервер, если он ещё не работает и не идёт пауза после сбоев.
        public Launch EnsureStarted(string exe, out string detail)
        {
            detail = null;
            lock (gate)
            {
                if (disposed) { detail = "виджет завершается"; return Launch.Disabled; }
                if (disabled) { detail = disabledReason; return Launch.Disabled; }
                ReapLocked();
                if (process != IntPtr.Zero) return Launch.Running;
                DateTime now = UtcNow();
                if (now < nextAllowed) { detail = "повтор после " + Fmt.Clock(nextAllowed); return Launch.Backoff; }

                string workDir = WorkDir();
                IntPtr j = Cli.Job.Create(0);
                if (j == IntPtr.Zero) { detail = "Job не создан"; return Launch.Failed; }
                int newPid;
                IntPtr h;
                string error;
                lastLaunch = now;
                launches++;
                if (!Native.StartSuspendedInJob(exe, "web --no-open", workDir, j, out h, out newPid, out error))
                {
                    Cli.Job.Close(j);
                    failures++;
                    nextAllowed = now + Backoff(failures);
                    detail = error;
                    Log.Write("kimi server: не запустился (" + error + ")");
                    return Launch.Failed;
                }
                job = j;
                process = h;
                pid = newPid;
                startedAt = now;
                nextAllowed = now + MinGap;
                Log.Write("kimi server: запущен скрытый kimi web --no-open, pid " + newPid);
                return Launch.Started;
            }
        }

        // Свой сервер ответил квотой: прошлые сбои забыты.
        public void MarkHealthy()
        {
            lock (gate) failures = 0;
        }

        // Свой сервер не годится (например, отвечает без проверки токена) — погасить и больше не запускать в этом сеансе.
        public void Reject(string reason)
        {
            lock (gate)
            {
                disabled = true;
                disabledReason = reason;
                StopLocked();
                Log.Write("kimi server: отклонён — " + reason);
            }
        }

        // Сервер не зарегистрировался вовремя или перестал отвечать: погасить своё дерево и выждать паузу.
        public void Fail(string reason)
        {
            lock (gate)
            {
                failures++;
                nextAllowed = UtcNow() + Backoff(failures);
                StopLocked();
                Log.Write("kimi server: сбой — " + reason + "; следующий запуск не раньше " + Fmt.Clock(nextAllowed));
            }
        }

        public void Stop()
        {
            lock (gate) StopLocked();
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                StopLocked();
            }
        }

        static TimeSpan Backoff(int n)
        {
            double minutes = Math.Pow(2, Math.Max(0, Math.Min(10, n - 1)));
            var t = TimeSpan.FromMinutes(minutes);
            return t > MaxBackoff ? MaxBackoff : t;
        }

        // Процесс сам завершился (упал или его закрыли): учесть сбой, убрать остатки дерева.
        void ReapLocked()
        {
            if (process == IntPtr.Zero || !Native.Exited(process)) return;
            DateTime now = UtcNow();
            if (now - startedAt >= HealthyRun) failures = 0;
            failures++;
            nextAllowed = now + Backoff(failures);
            Log.Write("kimi server: свой сервер завершился; следующий запуск не раньше " + Fmt.Clock(nextAllowed));
            StopLocked();
        }

        void StopLocked()
        {
            if (job != IntPtr.Zero)
            {
                Cli.Job.Terminate(job);
                Cli.Job.Close(job);
                job = IntPtr.Zero;
            }
            if (process != IntPtr.Zero)
            {
                Native.Wait(process, 3000);
                Native.CloseHandle(process);
                process = IntPtr.Zero;
            }
            pid = 0;
        }

        static string WorkDir()
        {
            string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LimitsWidget", "kimi-web");
            try { Directory.CreateDirectory(d); }
            catch { d = Path.GetTempPath(); }
            return d;
        }

        static class Native
        {
            const uint CreateSuspended = 0x00000004, CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x00000400,
                ExtendedStartupInfoPresent = 0x00080000;
            const int StartfUseStdHandles = 0x00000100;
            static readonly IntPtr ProcThreadAttributeHandleList = (IntPtr)0x00020002;
            const uint GenericWrite = 0x40000000, FileShareReadWrite = 3, OpenExisting = 3;
            const uint WaitObject0 = 0;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct StartupInfo
            {
                public int cb;
                public string lpReserved, lpDesktop, lpTitle;
                public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
                public short wShowWindow, cbReserved2;
                public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct StartupInfoEx
            {
                public StartupInfo StartupInfo;
                public IntPtr lpAttributeList;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct ProcessInformation
            {
                public IntPtr hProcess, hThread;
                public int dwProcessId, dwThreadId;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct SecurityAttributes
            {
                public int nLength;
                public IntPtr lpSecurityDescriptor;
                public bool bInheritHandle;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                IntPtr env, string dir, ref StartupInfoEx si, out ProcessInformation pi);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
            [DllImport("kernel32.dll")]
            static extern void DeleteProcThreadAttributeList(IntPtr list);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes sa, uint disposition, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern uint ResumeThread(IntPtr thread);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool TerminateProcess(IntPtr process, uint code);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern uint WaitForSingleObject(IntPtr handle, uint ms);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr handle);

            public static bool Exited(IntPtr process)
            {
                return WaitForSingleObject(process, 0) == WaitObject0;
            }

            public static void Wait(IntPtr process, int ms)
            {
                WaitForSingleObject(process, (uint)ms);
            }

            static string Quote(string s)
            {
                return "\"" + s + "\"";
            }

            // Блок окружения: окружение виджета + PATH с каталогами из реестра (как у остальных CLI).
            static IntPtr EnvironmentBlock()
            {
                var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string)e.Value;
                var probe = Cli.StartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "");
                vars["PATH"] = probe.EnvironmentVariables["PATH"];
                vars["NO_COLOR"] = "1";
                var sb = new StringBuilder();
                foreach (var kv in vars) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
                sb.Append('\0');
                return Marshal.StringToHGlobalUni(sb.ToString());
            }

            public static bool StartSuspendedInJob(string exe, string args, string dir, IntPtr job, out IntPtr hProcess, out int pid, out string error)
            {
                hProcess = IntPtr.Zero;
                pid = 0;
                error = null;
                string ext = Path.GetExtension(exe).ToLowerInvariant();
                string app, cmd;
                if (ext == ".cmd" || ext == ".bat")
                {
                    app = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                    cmd = Quote(app) + " /d /s /c \"" + Quote(exe) + " " + args + "\"";
                }
                else
                {
                    app = exe;
                    cmd = Quote(exe) + " " + args;
                }

                var sa = new SecurityAttributes { nLength = Marshal.SizeOf(typeof(SecurityAttributes)), bInheritHandle = true };
                IntPtr nul = CreateFile("NUL", GenericWrite, FileShareReadWrite, ref sa, OpenExisting, 0, IntPtr.Zero);
                if (nul == IntPtr.Zero || nul == (IntPtr)(-1)) { error = "NUL: " + Marshal.GetLastWin32Error(); return false; }
                IntPtr attrs = IntPtr.Zero, handles = IntPtr.Zero, env = IntPtr.Zero;
                var pi = new ProcessInformation();
                bool created = false;
                try
                {
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attrs = Marshal.AllocHGlobal(size);
                    if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size)) { error = "attr " + Marshal.GetLastWin32Error(); Marshal.FreeHGlobal(attrs); attrs = IntPtr.Zero; return false; }
                    handles = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(handles, nul);
                    if (!UpdateProcThreadAttribute(attrs, 0, ProcThreadAttributeHandleList, handles, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    {
                        error = "handle list " + Marshal.GetLastWin32Error();
                        return false;
                    }
                    var si = new StartupInfoEx();
                    si.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx));
                    si.StartupInfo.dwFlags = StartfUseStdHandles;
                    si.StartupInfo.hStdInput = nul;
                    si.StartupInfo.hStdOutput = nul;
                    si.StartupInfo.hStdError = nul;
                    si.lpAttributeList = attrs;
                    env = EnvironmentBlock();
                    created = CreateProcess(app, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, true,
                        CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent, env, dir, ref si, out pi);
                    if (!created) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
                    if (!AssignProcessToJobObject(job, pi.hProcess))
                    {
                        // без Job владение не гарантировано — такой процесс не запускаем
                        error = "Job: " + Marshal.GetLastWin32Error();
                        TerminateProcess(pi.hProcess, 1);
                        CloseHandle(pi.hThread);
                        CloseHandle(pi.hProcess);
                        return false;
                    }
                    ResumeThread(pi.hThread);
                    CloseHandle(pi.hThread);
                    hProcess = pi.hProcess;
                    pid = pi.dwProcessId;
                    return true;
                }
                finally
                {
                    if (attrs != IntPtr.Zero) { DeleteProcThreadAttributeList(attrs); Marshal.FreeHGlobal(attrs); }
                    if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
                    if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
                    CloseHandle(nul);
                }
            }
        }
    }
}
