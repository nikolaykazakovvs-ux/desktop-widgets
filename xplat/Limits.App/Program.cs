using System;
using System.IO;
using Avalonia;

namespace DesktopWidgets.Limits.App
{
    public sealed class Options
    {
        public string TelemetryDir, ConfigDir, SelfTestDir, FixtureUsage, ClaudeCommand;
        public int RefreshSeconds = 300;
        public int SelfTestSeconds = 75;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("value required: " + args[i]);
                switch (args[i])
                {
                    case "--telemetry-dir": o.TelemetryDir = Path.GetFullPath(Next()); break;
                    case "--config-dir": o.ConfigDir = Path.GetFullPath(Next()); break;
                    case "--selftest": o.SelfTestDir = Path.GetFullPath(Next()); break;
                    case "--selftest-seconds": o.SelfTestSeconds = int.Parse(Next()); break;
                    case "--fixture-usage": o.FixtureUsage = Path.GetFullPath(Next()); break;
                    case "--claude-command": o.ClaudeCommand = Next(); break;
                    case "--refresh-seconds": o.RefreshSeconds = Math.Max(60, int.Parse(Next())); break;
                    default: throw new ArgumentException("unknown argument: " + args[i]);
                }
            }
            return o;
        }
    }

    static class Program
    {
        public static Options Options;

        [STAThread]
        public static int Main(string[] args)
        {
            try { Options = Options.Parse(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>(), Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
            return SelfTest.ExitCode;
        }

        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })   // виджет живёт в строке меню, а не в Dock
            .LogToTrace();
    }
}
