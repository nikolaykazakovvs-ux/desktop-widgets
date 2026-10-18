using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Лимиты нейросетей")]
[assembly: AssemblyProduct("LimitsWidget")]
[assembly: AssemblyDescription("Десктоп-виджет лимитов Claude Code, Kimi Code, Antigravity и GPT/Codex")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace LimitsWidget
{
    sealed class CliOptions
    {
        public string DumpPath;     // --dump out.json : собрать данные один раз и выйти
        public string RenderPath;   // --render out.png : отрисовать карточку в PNG и выйти
        public string FixturesDir;  // --fixtures dir  : подставить тестовые ответы вместо живых источников
        public string Theme;        // --theme dark|light
        public string ConfigPath;
        public string StatePath;
        public bool Compact;        // --compact : для --render
        public double RenderScale = 2;  // --render-scale 6 : крупнее, для проверки мелких деталей
        public bool Show;           // --show : сразу поднять карточку поверх окон
        public bool Exit;           // --exit : попросить работающую копию завершиться (так делает build.ps1)

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--dump": o.DumpPath = next; i++; break;
                    case "--render": o.RenderPath = next; i++; break;
                    case "--fixtures": o.FixturesDir = next; i++; break;
                    case "--theme": o.Theme = next; i++; break;
                    case "--config": o.ConfigPath = next; i++; break;
                    case "--state": o.StatePath = next; i++; break;
                    case "--compact": o.Compact = true; break;
                    case "--show": o.Show = true; break;
                    case "--exit": o.Exit = true; break;
                    case "--render-scale":
                        double sc;
                        if (double.TryParse(next, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sc) && sc >= 1 && sc <= 12) o.RenderScale = sc;
                        i++;
                        break;
                }
            }
            return o;
        }
    }

    static class Program
    {
        public const string ShowEventName = "Local\\LimitsWidget.Show";
        public const string ExitEventName = "Local\\LimitsWidget.Exit";
        public static string BaseDir;

        [STAThread]
        static int Main(string[] args)
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; // + TLS 1.3
            }
            catch (NotSupportedException)
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }

            var opts = CliOptions.Parse(args);
            try
            {
                if (opts.DumpPath != null) return Headless.Dump(opts);
                if (opts.RenderPath != null) return Headless.Render(opts);
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

            bool created;
            using (var mutex = new Mutex(true, "Local\\LimitsWidget.SingleInstance", out created))
            {
                if (!created)
                {
                    // Виджет уже работает (свёрнут в трей или на другом мониторе): разрешить ему выйти на передний план
                    // и попросить показать карточку.
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
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log.Write("ui: " + e.Exception);
                    e.Handled = true;
                };
                var w = new WidgetWindow(opts);
                app.MainWindow = w;
                w.Start();   // окно может остаться скрытым (свёрнуто в трей), поэтому Run без окна
                app.Run();
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    static class Log
    {
        static readonly object Gate = new object();

        // Токены и ответы API сюда не пишутся — только тип ошибки и служебный текст.
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
