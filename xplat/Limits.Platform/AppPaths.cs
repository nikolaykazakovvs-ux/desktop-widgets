using System;
using System.Collections.Generic;
using System.IO;
using DesktopWidgets.Limits.Core;

namespace DesktopWidgets.Limits.Platform
{
    public enum OsKind { Windows, MacOS, Linux }

    public static class Os
    {
        public static OsKind Current => OperatingSystem.IsWindows() ? OsKind.Windows : OperatingSystem.IsMacOS() ? OsKind.MacOS : OsKind.Linux;
        public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    // Стандартные каталоги ОС. Каталог телеметрии выбирается как в контракте: аргумент → DESKTOP_WIDGETS_TELEMETRY_DIR → стандартный.
    //   Windows: %LOCALAPPDATA%\DesktopWidgets\telemetry           (как у WPF-версии и в docs/ai-telemetry-contract.md)
    //   macOS:   ~/Library/Application Support/DesktopWidgets/telemetry   (предложение; требует согласования с Bridge/S.I.G.M.A.)
    //   Linux:   $XDG_STATE_HOME или ~/.local/state, затем DesktopWidgets/telemetry
    public sealed class AppPaths : IAppPaths
    {
        readonly string telemetryOverride, configOverride;

        public AppPaths(string telemetryDir = null, string configDir = null)
        {
            telemetryOverride = telemetryDir;
            configOverride = configDir;
        }

        public static string StandardRoot()
        {
            switch (Os.Current)
            {
                case OsKind.Windows: return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopWidgets");
                case OsKind.MacOS: return Path.Combine(Os.Home, "Library", "Application Support", "DesktopWidgets");
                default:
                    string state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                    return Path.Combine(string.IsNullOrEmpty(state) || !Path.IsPathRooted(state) ? Path.Combine(Os.Home, ".local", "state") : state, "DesktopWidgets");
            }
        }

        public static string StandardTelemetryDirectory() => Path.Combine(StandardRoot(), "telemetry");

        public string TelemetryDirectory
        {
            get
            {
                if (!string.IsNullOrEmpty(telemetryOverride)) return telemetryOverride;
                string env = Environment.GetEnvironmentVariable("DESKTOP_WIDGETS_TELEMETRY_DIR");
                return string.IsNullOrEmpty(env) ? StandardTelemetryDirectory() : env;
            }
        }

        // Прототип хранит настройки отдельно от WPF-версии, чтобы не трогать её config.json/state.json.
        public string ConfigDirectory => !string.IsNullOrEmpty(configOverride) ? configOverride : Path.Combine(StandardRoot(), "limits-xplat");

        public IEnumerable<string> ClaudeDesktopHistoryCandidates()
        {
            switch (Os.Current)
            {
                case OsKind.Windows:
                    string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                    if (Directory.Exists(packages))
                        foreach (var d in Directory.GetDirectories(packages, "Claude_*"))
                            yield return Path.Combine(d, "LocalCache", "Roaming", "Claude", "plan-usage-history.json");
                    yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "plan-usage-history.json");
                    break;
                case OsKind.MacOS:
                    yield return Path.Combine(Os.Home, "Library", "Application Support", "Claude", "plan-usage-history.json");
                    break;
                default:
                    yield return Path.Combine(Os.Home, ".config", "Claude", "plan-usage-history.json");
                    break;
            }
        }
    }

    public sealed class FileLog : ILog
    {
        readonly string path;
        readonly object gate = new object();

        public FileLog(string directory)
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "widget.log");
        }

        public void Write(string line)
        {
            lock (gate)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Move(path, path + ".1", true);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
                }
                catch { }
            }
        }
    }
}
