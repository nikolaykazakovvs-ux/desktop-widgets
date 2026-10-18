using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: AssemblyTitle("Системные ресурсы — установка")]
[assembly: AssemblyProduct("ResourcesWidget")]
[assembly: AssemblyDescription("Установка виджета «Системные ресурсы»")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace ResourcesWidgetSetup
{
    // Ключи командной строки. Без ключей — окно установки.
    //   --uninstall            удалить виджет (из «Установленных приложений»)
    // Для проверки и тихой установки:
    //   --quiet  --log <файл>  --dir <папка>  --no-desktop  --no-startmenu  --no-autostart  --no-register  --no-launch
    //   --sensor/--with-sensor/--sensor-remove rejected: external sensors are configured independently.
    sealed class Options
    {
        public bool Quiet, Uninstall, UninstallRun, Sensor, SensorRemove, DryRun, Fresh, WithSensor;
        public bool NoDesktop, NoStartMenu, NoAutostart, NoRegister, NoLaunch, Autostart;
        public string Dir, LogPath, ForUser, ExtractDir;
        public int WaitPid;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--quiet": o.Quiet = true; break;
                    case "--uninstall": o.Uninstall = true; break;
                    case "--uninstall-run": o.UninstallRun = true; break;
                    case "--sensor": o.Sensor = true; break;
                    case "--sensor-remove": o.SensorRemove = true; break;
                    case "--dry-run": o.DryRun = true; break;
                    case "--dry-run-fresh": o.DryRun = o.Fresh = true; break;
                    case "--with-sensor": o.WithSensor = true; break;
                    case "--autostart": o.Autostart = true; break;
                    case "--extract":
                        if (string.IsNullOrWhiteSpace(next) || next.StartsWith("--")) throw new ArgumentException("--extract требует новую папку");
                        o.ExtractDir = next; i++; break;
                    case "--no-desktop": o.NoDesktop = true; break;
                    case "--no-startmenu": o.NoStartMenu = true; break;
                    case "--no-autostart": o.NoAutostart = true; break;
                    case "--no-register": o.NoRegister = true; break;
                    case "--no-launch": o.NoLaunch = true; break;
                    case "--dir": o.Dir = next; i++; break;
                    case "--log": o.LogPath = next; i++; break;
                    case "--for-user": o.ForUser = next; i++; break;
                    case "--wait-pid":
                        int pid;
                        if (int.TryParse(next, out pid)) o.WaitPid = pid;
                        i++;
                        break;
                }
            }
            return o;
        }

        // Те же ключи для дочернего процесса (с правами администратора или копии для удаления).
        public string Pass(params string[] extra)
        {
            var parts = new List<string>(extra);
            if (Quiet) parts.Add("--quiet");
            if (DryRun) parts.Add("--dry-run");
            if (LogPath != null) { parts.Add("--log"); parts.Add(Quote(LogPath)); }
            return string.Join(" ", parts.ToArray());
        }

        public static string Quote(string s)
        {
            return "\"" + s.TrimEnd('\\') + "\"";
        }
    }

    static class Program
    {
        public const string AppName = "Системные ресурсы";
        public const string Version = "1.1.0";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourcesWidget";
        public static Options Opts;

        [STAThread]
        static int Main(string[] args)
        {
            Opts = Options.Parse(args);
            if (Opts.Sensor || Opts.SensorRemove || Opts.WithSensor)
            {
                const string message = "Этот выпуск не устанавливает и не удаляет сторонние датчики. Используйте отдельно настроенные HWiNFO, AIDA64 или Core Temp.";
                if (!Opts.Quiet) MessageBox.Show(message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 3;
            }
            // Извлечение только файлов: не создаёт GUI, ярлыки, ключи реестра, процессы или драйверы.
            if (Opts.ExtractDir != null)
            {
                try { Payload.ExtractAll(Opts.ExtractDir); return 0; }
                catch { return 1; }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (Opts.UninstallRun) return Uninstaller.RunFromTemp(Opts);
                if (Opts.Uninstall) return Uninstaller.Start(Opts);
                return Installer.Start(Opts);
            }
            catch (Exception ex)
            {
                Log.Write("ошибка: " + ex);
                if (!Opts.Quiet) MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        public static string SelfPath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        // .NET Framework 4.8 есть в Windows 10 с версии 1903 и в Windows 11.
        public static bool HasNet48()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            {
                object v = k != null ? k.GetValue("Release") : null;
                return v is int && (int)v >= 528040;
            }
        }
    }

    // Файлы, вшитые в установщик.
    static class Payload
    {
        public const string Widget = "payload.ResourcesWidget.exe", Config = "payload.config.json", Notice = "payload.THIRD-PARTY.txt";

        public static Stream Open(string name)
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) throw new InvalidOperationException("В установщике нет файла " + name);
            return s;
        }

        public static void ExtractAll(string directory)
        {
            string target = InstallDirectory.CheckedPath(directory);
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Папка извлечения уже существует");
            Directory.CreateDirectory(target);
            foreach (string name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (!name.StartsWith("payload.", StringComparison.Ordinal)) continue;
                string file = name.Substring("payload.".Length);
                if (Path.GetFileName(file) != file) throw new IOException("Недопустимое имя ресурса");
                Extract(name, Path.Combine(target, file));
            }
        }

        // Запись через временный файл: если старый файл занят, он не портится.
        public static void Extract(string name, string path)
        {
            string tmp = path + ".new";
            using (var src = Open(name))
            using (var dst = File.Create(tmp))
                src.CopyTo(dst);
            Replace(tmp, path);
        }

        public static void Replace(string tmp, string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                    return;
                }
                catch (IOException)
                {
                    if (attempt >= 20) throw;
                    Thread.Sleep(250);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= 20) throw;
                    Thread.Sleep(250);
                }
            }
        }
    }

    // Журнал шагов: в окно установки и в файл --log.
    static class Log
    {
        static readonly object Gate = new object();
        public static event Action<string> Line;

        public static void Write(string line)
        {
            lock (Gate)
            {
                var path = Program.Opts != null ? Program.Opts.LogPath : null;
                if (path != null)
                {
                    try { File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine, new UTF8Encoding(false)); }
                    catch { }
                }
            }
            var h = Line;
            if (h != null) h(line);
        }
    }

    // Ярлыки через WScript.Shell, как у самого виджета.
    static class Shortcut
    {
        public static void Create(string link, string target, string args, string description)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            try
            {
                object l = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var lt = l.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, l, new object[] { target });
                lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, l, new object[] { args ?? "" });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, l, new object[] { Path.GetDirectoryName(target) });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, l, new object[] { description });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, l, new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, l, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(l);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        // Куда указывает ярлык; null — не прочитать.
        public static string Target(string link)
        {
            if (!File.Exists(link)) return null;
            var type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            try
            {
                object l = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                string t = l.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, l, null) as string;
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(l);
                return t;
            }
            catch
            {
                return null;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }

        public static string Desktop { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.AppName + ".lnk"); } }
        public static string StartMenu { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.AppName + ".lnk"); } }
        public static string Startup { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Program.AppName + ".lnk"); } }
    }

    // Работающий виджет: попросить выйти (он сохранит положение и уберёт значок из трея).
    static class Widget
    {
        const string ExitEvent = "Local\\ResourcesWidget.Exit";

        public static void Close(string directory)
        {
            var found = Process.GetProcessesByName("ResourcesWidget");
            var targets = new List<Process>();
            string executable = Path.Combine(Path.GetFullPath(directory), "ResourcesWidget.exe");
            foreach (var item in found)
            {
                try
                {
                    if (string.Equals(item.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase)) targets.Add(item);
                    else item.Dispose();
                }
                catch (System.ComponentModel.Win32Exception) { item.Dispose(); }
                catch (InvalidOperationException) { item.Dispose(); }
            }
            if (targets.Count == 0) return;
            Log.Write("Закрываю работающий виджет");
            try
            {
                using (var e = EventWaitHandle.OpenExisting(ExitEvent)) e.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            foreach (var p in targets)
            {
                try
                {
                    if (!p.WaitForExit(6000))
                    {
                        Log.Write("Виджет не закрылся сам — завершаю процесс " + p.Id);
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
        }
    }
}
