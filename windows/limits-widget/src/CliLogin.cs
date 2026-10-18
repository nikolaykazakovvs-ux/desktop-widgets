using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace LimitsWidget
{
    // Вход через CLI без окна (`claude auth login`): Kimi вход не нужен — виджет подхватывает сессию приложения Kimi Code; CLI сам открывает браузер и ждёт подтверждения.
    // Вывод читается построчно: если CLI печатает код подтверждения («...enter code: ABCD-1234»), он передаётся наружу.
    // Колбэки приходят из фоновых потоков — в UI их переносит вызывающий (WidgetWindow).
    sealed class CliLogin
    {
        static readonly Regex CodeLine = new Regex(@"enter code:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        const int WaitMs = 5 * 60 * 1000;

        readonly string exe, args;
        readonly object gate = new object();
        Process proc;
        bool cancelRequested;
        int codeSent, doneSent;

        public Action<string> CodeFound;   // код подтверждения найден в выводе (один раз за вход)
        public Action<bool> Done;          // вход завершён: true — CLI вышел с кодом 0; вызывается ровно один раз

        public CliLogin(string exe, string args)
        {
            this.exe = exe;
            this.args = args;
        }

        public void Start()
        {
            var t = new Thread(Run) { IsBackground = true, Name = "cli-login" };
            t.Start();
        }

        // Убить только сам CLI (браузер пользователя не трогаем); Done(false) придёт из потока ожидания.
        public void Cancel()
        {
            lock (gate)
            {
                cancelRequested = true;
                KillLocked();
            }
        }

        void KillLocked()
        {
            try { if (proc != null && !proc.HasExited) proc.Kill(); }
            catch { }
        }

        void Run()
        {
            bool ok = false;
            try
            {
                // без Job: браузер, открытый CLI, не должен закрыться вместе с ним
                using (var p = Process.Start(Cli.StartInfo(exe, args)))
                {
                    lock (gate)
                    {
                        proc = p;
                        if (cancelRequested) KillLocked();
                    }
                    var so = Pump(p.StandardOutput);
                    var se = Pump(p.StandardError);
                    if (p.WaitForExit(WaitMs))
                    {
                        // дочитать вывод: код мог быть напечатан прямо перед выходом; после отмены не ждём
                        // (потомок убитого CLI может держать трубы открытыми)
                        bool cancelled;
                        lock (gate) cancelled = cancelRequested;
                        if (!cancelled)
                        {
                            so.Join(1000);
                            se.Join(300);
                        }
                        ok = !cancelled && p.ExitCode == 0;
                    }
                    else
                    {
                        try { p.Kill(); }
                        catch { }
                    }
                    lock (gate) proc = null;
                }
            }
            catch (Exception ex)
            {
                Log.Write("cli login (" + Path.GetFileName(exe) + "): " + ex.GetType().Name + ": " + ex.Message);
            }
            Finish(ok);
        }

        Thread Pump(StreamReader reader)
        {
            var t = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) Scan(line);
                }
                catch
                {
                    // поток закрыт при Kill — штатно
                }
            }) { IsBackground = true, Name = "cli-login-out" };
            t.Start();
            return t;
        }

        void Scan(string line)
        {
            var m = CodeLine.Match(line);
            if (!m.Success || Interlocked.Exchange(ref codeSent, 1) != 0) return;
            var cb = CodeFound;
            if (cb == null) return;
            try { cb(m.Groups[1].Value); }
            catch (Exception ex) { Log.Write("cli login code: " + ex.Message); }
        }

        void Finish(bool ok)
        {
            if (Interlocked.Exchange(ref doneSent, 1) != 0) return;
            var cb = Done;
            if (cb == null) return;
            try { cb(ok); }
            catch (Exception ex) { Log.Write("cli login done: " + ex.Message); }
        }
    }
}
