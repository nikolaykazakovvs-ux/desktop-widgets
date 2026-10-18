using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Системные ресурсы")]
[assembly: AssemblyProduct("ResourcesWidget")]
[assembly: AssemblyDescription("Виджет в трее: процессор, память, видеокарта, диски, сеть и процессы")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace ResourcesWidget
{
    sealed class CliOptions
    {
        public string DumpPath;     // --dump out.json : снять показатели и выйти
        public string JournalPath;  // --render-journal out.png : отрисовать журнал сети в PNG (--journal-day yyyy-MM-dd — день)
        public string JournalDay;
        public bool Transparent;    // --transparent : без подложки «под обои» (для --render и --render-journal)
        public string RenderPath;   // --render out.png : отрисовать карточку в PNG и выйти
        public string Theme;        // --theme dark|light (для --render)
        public bool Compact;        // --compact (для --render)
        public int Columns = 1;     // --columns 2 (для --render)
        public bool Show;           // --show : сразу показать карточку
        public bool Restart;        // --restart : дождаться выхода прежней копии
        public bool Exit;           // --exit : попросить работающую копию завершиться
        public string ConfigPath, StatePath;

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--dump": o.DumpPath = next; i++; break;
                    case "--render-journal": o.JournalPath = next; i++; break;
                    case "--journal-day": o.JournalDay = next; i++; break;
                    case "--render": o.RenderPath = next; i++; break;
                    case "--theme": o.Theme = next; i++; break;
                    case "--columns": o.Columns = next == "2" ? 2 : 1; i++; break;
                    case "--config": o.ConfigPath = next; i++; break;
                    case "--state": o.StatePath = next; i++; break;
                    case "--compact": o.Compact = true; break;
                    case "--transparent": o.Transparent = true; break;
                    case "--show": o.Show = true; break;
                    case "--restart": o.Restart = true; break;
                    case "--exit": o.Exit = true; break;
                }
            }
            return o;
        }
    }

    static class Program
    {
        public const string ShowEventName = "Local\\ResourcesWidget.Show";
        public const string ExitEventName = "Local\\ResourcesWidget.Exit";
        public static string BaseDir;
        public static string SetupPath { get { string p = Path.Combine(BaseDir, "Setup.exe"); return File.Exists(p) ? p : null; } }

        // Журнал сети: network-log рядом с exe.
        public static string NetLogDir
        {
            get { return Path.Combine(BaseDir, "network-log"); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            var opts = CliOptions.Parse(args);
            try
            {
                if (opts.DumpPath != null) return Headless.Dump(opts);
                if (opts.RenderPath != null) return Headless.Render(opts);
                if (opts.JournalPath != null) return Headless.RenderJournal(opts);
            }
            catch (Exception ex)
            {
                Log.Write("headless: " + ex);
                return 2;
            }

            if (opts.Exit)
            {
                try
                {
                    using (var exit = EventWaitHandle.OpenExisting(ExitEventName)) exit.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    return 1;   // виджет не запущен
                }
                return 0;
            }

            bool owned;
            using (var mutex = new Mutex(true, "Local\\ResourcesWidget.SingleInstance", out owned))
            {
                if (!owned && opts.Restart)
                {
                    try { owned = mutex.WaitOne(5000); }
                    catch (AbandonedMutexException) { owned = true; }
                }
                if (!owned)
                {
                    // Виджет уже в трее: разрешить ему выйти на передний план и попросить показать карточку.
                    Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                    try
                    {
                        using (var show = EventWaitHandle.OpenExisting(ShowEventName)) show.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                    }
                    return 0;
                }

                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Write("ui: " + e.Exception);
                    e.Handled = true;
                };
                using (new TrayApp(opts))
                {
                    app.Run();
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }

    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    string path = Path.Combine(Program.BaseDir, "widget.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 256 * 1024)
                    {
                        string old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
