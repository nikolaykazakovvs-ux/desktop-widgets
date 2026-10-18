using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ResourcesWidgetSetup
{
    // Filesystem boundary shared by installation and removal. Never recursively deletes a user-selected directory.
    static class InstallDirectory
    {
        public const string Marker = ".resources-widget-install";
        const string Identity = "ResourcesWidget owned installation v1";
        static readonly string[] ApplicationFiles = { "ResourcesWidget.exe", "Setup.exe", "THIRD-PARTY.txt" };

        public static string CheckedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3) throw new IOException("Выберите абсолютный путь к отдельной папке виджета.");
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || string.Equals(full, Path.GetPathRoot(full).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Корень диска и сетевые пути не подходят для установки.");
            if (File.Exists(full)) throw new IOException("Путь указывает на файл.");
            for (var item = new DirectoryInfo(full); item != null; item = item.Parent) RejectReparsePoint(item.FullName);
            foreach (Environment.SpecialFolder special in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.Programs, Environment.SpecialFolder.Startup,
                Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos })
            {
                string reserved = Environment.GetFolderPath(special);
                if (!string.IsNullOrEmpty(reserved) && string.Equals(full, Path.GetFullPath(reserved).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Нужна отдельная подпапка приложения, а не общая системная или пользовательская папка.");
            }
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (string.Equals(full, downloads, StringComparison.OrdinalIgnoreCase)) throw new IOException("Выберите отдельную подпапку, а не папку загрузок.");
            foreach (string name in new[] { "ResourcesWidget.exe", "ResourcesWidget.exe.new", "Setup.exe", "Setup.exe.new", "THIRD-PARTY.txt", "THIRD-PARTY.txt.new", "config.json", "config.json.new", "state.json", Marker, "network-log" })
            {
                string child = Path.Combine(full, name);
                RejectReparsePoint(child);
            }
            return full;
        }

        static void RejectReparsePoint(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Ссылки и точки подключения в пути установки не поддерживаются.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        public static bool Owned(string path)
        {
            string marker = Path.Combine(path, Marker);
            return File.Exists(marker) && File.ReadAllText(marker, Encoding.UTF8) == Identity;
        }

        static bool RegisteredLegacy(string path)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(Program.UninstallKey))
            {
                string registered = key != null ? key.GetValue("InstallLocation") as string : null;
                if (registered == null || !string.Equals(Path.GetFullPath(registered).TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)) return false;
            }
            foreach (string name in new[] { "ResourcesWidget.exe", "Setup.exe" })
            {
                string file = Path.Combine(path, name);
                if (!File.Exists(file) || FileVersionInfo.GetVersionInfo(file).ProductName != "ResourcesWidget") return false;
            }
            return true;
        }

        public static string ValidateInstall(string path)
        {
            string full = CheckedPath(path);
            if (Directory.Exists(full) && Directory.GetFileSystemEntries(full).Length != 0 && !Owned(full) && !RegisteredLegacy(full))
                throw new IOException("Папка не пустая и не принадлежит установленному виджету. Выберите отдельную пустую папку.");
            return full;
        }

        public static void MarkOwned(string path)
        {
            File.WriteAllText(Path.Combine(path, Marker), Identity, new UTF8Encoding(false));
        }

        public static void RemoveApplicationFiles(string path)
        {
            string full = CheckedPath(path);
            if (!Owned(full)) throw new IOException("Нет отметки установки виджета: файлы не удалены.");
            foreach (string name in ApplicationFiles)
            {
                string file = Path.Combine(full, name);
                if (File.Exists(file)) File.Delete(file);
            }
            // Keep all data and unknown files. Retain ownership marker for safe reinstallation.
            if (Directory.GetFileSystemEntries(full).Length == 1 && File.Exists(Path.Combine(full, Marker)))
            {
                File.Delete(Path.Combine(full, Marker));
                Directory.Delete(full, false);
            }
        }
    }
}
