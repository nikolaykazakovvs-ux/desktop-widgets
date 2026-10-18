using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ResourcesWidgetSetup
{
    // Remove known application binaries only; preserve settings, network history and unrelated files.
    static class Uninstaller
    {
        public static int Start(Options o)
        {
            string dir = InstallDirectory.CheckedPath(o.Dir ?? Path.GetDirectoryName(Path.GetFullPath(Program.SelfPath)));
            if (!InstallDirectory.Owned(dir)) throw new IOException("Нет отметки установки виджета: удаление отменено.");
            if (!o.Quiet && MessageBox.Show("Удалить «" + Program.AppName + "»? Настройки, журналы и остальные файлы будут сохранены.", Program.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return 2;
            if (!o.Quiet && Directory.Exists(ResourcesWidgetSensor.SensorSecurity.UserDirectory(LocalSensorInstall.CurrentSid))
                && MessageBox.Show("Удалить и собственный локальный датчик температуры с его задачей SYSTEM? Windows запросит подтверждение администратора. Общий драйвер PawnIO и внешние программы мониторинга останутся.", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Program.RequestSensor(true);

            string tmp = Path.Combine(Path.GetTempPath(), "ResourcesWidget-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            File.Copy(Program.SelfPath, tmp, true);
            string args = o.Pass("--uninstall-run", "--dir", Options.Quote(dir), "--wait-pid", Process.GetCurrentProcess().Id.ToString());
            // эта копия сразу выходит: пока она работает, её exe в папке виджета не удалить
            Process.Start(new ProcessStartInfo(tmp, args) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() }).Dispose();
            return 0;
        }

        public static int RunFromTemp(Options o)
        {
            if (o.WaitPid > 0)
            {
                try
                {
                    using (var parent = Process.GetProcessById(o.WaitPid))
                        parent.WaitForExit(10000);
                }
                catch (ArgumentException) { }
            }
            string dir = InstallDirectory.CheckedPath(o.Dir);
            if (!InstallDirectory.Owned(dir)) throw new IOException("Нет отметки установки виджета: файлы не удалены.");
            Log.Write("Удаляю виджет из " + dir);
            Widget.Close(dir);

            foreach (var link in new[] { Shortcut.Desktop, Shortcut.StartMenu, Shortcut.Startup })
            {
                string target = Shortcut.Target(link);
                if (target != null && target.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(link);
                    Log.Write("Ярлык удалён: " + link);
                }
            }
            using (var k = Registry.CurrentUser.OpenSubKey(Program.UninstallKey))
            {
                string loc = k != null ? k.GetValue("InstallLocation") as string : null;
                if (loc != null && string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    k.Close();
                    Registry.CurrentUser.DeleteSubKeyTree(Program.UninstallKey, false);
                    Log.Write("Запись в «Установленных приложениях» удалена");
                }
            }
            InstallDirectory.RemoveApplicationFiles(dir);
            Log.Write("Файлы приложения удалены; настройки, журналы и остальные файлы сохранены");
            if (!o.Quiet) MessageBox.Show("«" + Program.AppName + "» удалены.", Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

            // эта временная копия удалит себя после выхода
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /f /q " + Options.Quote(Program.SelfPath))
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                }).Dispose();
            }
            catch { }
            return 0;
        }
    }
}
