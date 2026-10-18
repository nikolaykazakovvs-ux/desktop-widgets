using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using ResourcesWidgetSensor;
using Microsoft.Win32;

namespace ResourcesWidgetSetup
{
    // Own, per-user, local read-only helper. Never touches LibreHardwareMonitor GUI/task/config or removes PawnIO.
    static class LocalSensorInstall
    {
        const string PawnHash = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
        const uint GenericRead = 0x80000000, OpenExisting = 3;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string existing, string replacement, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        public static string CurrentSid { get { return WindowsIdentity.GetCurrent().User.Value; } }
        static string Executable(string sid) { return Path.Combine(SensorSecurity.UserDirectory(sid), "ResourcesWidgetSensor.exe"); }
        static string Arguments(string sid) { return "--user-sid " + SensorSecurity.UserSid(sid).Value; }

        public static int Start(Options options)
        {
            string sid = SensorSecurity.UserSid(options.ForUser ?? CurrentSid).Value;
            // The intended interactive account must have a real Windows user profile; no arbitrary service identities.
            using (var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid))
                if (profile == null) throw new IOException("Не найден профиль пользователя для локального датчика.");
            if (options.DryRun)
            {
                Log.Write("Локальный датчик: " + SensorSecurity.UserDirectory(sid));
                Log.Write("Задача SYSTEM: " + SensorSecurity.TaskName(sid) + "; только локальный защищённый temperature.json, без сетевых серверов.");
                Log.Write(PawnAvailable() ? "PawnIO доступен: новая установка драйвера не нужна." : "PawnIO недоступен: при установке будет проверен и установлен подписанный драйвер.");
                return 0;
            }
            if (options.SensorStatus)
            {
                SensorSecurity.AssertInstalledDirectory(sid);
                string label;
                double? value = ResourcesWidget.LocalSensorSnapshot.Read(sid, out label);
                Log.Write("Local sensor: " + (value.HasValue ? "fresh; " + value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " C; " + label : "unavailable or stale"));
                return value.HasValue ? 0 : 3;
            }
            if (!SensorSecurity.IsAdministrator())
            {
                if (options.SensorRemove && string.Equals(Path.GetFullPath(Program.SelfPath), Path.Combine(SensorSecurity.UserDirectory(sid), "SensorSetup.exe"), StringComparison.OrdinalIgnoreCase))
                {
                    // Protected manager cannot be replaced by the caller; do not keep the parent lock
                    // across UAC when its trusted child needs to rename that same manager to a tombstone.
                    SensorSecurity.AssertInstalledDirectory(sid);
                    SensorSecurity.AssertFile(Program.SelfPath, sid);
                    Program.ReleaseProtectedManagerImageLock();
                }
                string flags = options.SensorRemove ? "--sensor-remove" : options.SensorStart ? "--sensor-start" : options.SensorStop ? "--sensor-stop" : "--sensor";
                using (var child = Process.Start(new ProcessStartInfo(Program.SelfPath, flags + " --quiet --for-user " + sid) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System) }))
                {
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            try
            {
                if (options.SensorStart || options.SensorStop) return Control(sid, options.SensorStart);
                return options.SensorRemove ? Remove(sid) : Install(sid);
            }
            catch (Exception ex)
            {
                try
                {
                    SensorSecurity.EnsureDirectory(SensorSecurity.Root, null);
                    string receipt = Path.Combine(SensorSecurity.Root, "failure-" + sid + ".txt");
                    SensorSecurity.RejectReparseFile(receipt);
                    if (File.Exists(receipt)) SensorSecurity.AssertFile(receipt, sid);
                    File.WriteAllText(receipt, DateTime.UtcNow.ToString("o") + Environment.NewLine + ex.ToString());
                    SensorSecurity.SecureFile(receipt, sid);
                }
                catch { /* Preserve the original failure if the evidence directory is itself untrusted. */ }
                throw;
            }
        }

        static int Control(string sid, bool start)
        {
            SensorSecurity.AssertInstalledDirectory(sid);
            dynamic service = Scheduler();
            dynamic folder = service.GetFolder("\\");
            dynamic task = ExistingTask(folder, SensorSecurity.TaskName(sid));
            if (task == null) throw new IOException("Задача собственного локального датчика не найдена.");
            AssertTask(task, sid);
            if (start) task.Run(null);
            else { task.Stop(0); WaitForStop(task); }
            return 0;
        }

        public static bool PawnAvailable()
        {
            // Both actual service state and successful device-open are required; registry version alone is insufficient.
            using (var installed = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
            {
                Version version;
                if (installed == null || !Version.TryParse(installed.GetValue("DisplayVersion") as string, out version) || version < new Version(2, 2)) return false;
            }
            using (var manager = new System.ServiceProcess.ServiceController("PawnIO"))
            {
                try { if (manager.Status != System.ServiceProcess.ServiceControllerStatus.Running) return false; }
                catch (InvalidOperationException) { return false; }
                using (var device = CreateFile(@"\\.\GLOBALROOT\Device\PawnIO", GenericRead | 0x40000000, 7, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero)) return !device.IsInvalid;
            }
        }

        static dynamic Scheduler()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            service.Connect();
            return service;
        }
        static dynamic ExistingTask(dynamic folder, string name)
        {
            try { return folder.GetTask(name); }
            catch (COMException ex) { if ((uint)ex.ErrorCode == 0x80070002) return null; throw; }
            catch (FileNotFoundException ex) { if ((uint)ex.HResult == 0x80070002 && string.IsNullOrEmpty(ex.FileName)) return null; throw; }
        }
        static void AssertTask(dynamic task, string sid)
        {
            dynamic definition = task.Definition;
            if (definition.Actions.Count != 1) throw new IOException("Существующая задача датчика содержит чужие действия.");
            dynamic action = definition.Actions.Item(1);
            string principal = (string)definition.Principal.UserId;
            if (!string.Equals((string)action.Path, Executable(sid), StringComparison.OrdinalIgnoreCase)
                || (string)action.Arguments != Arguments(sid)
                || !string.Equals((string)action.WorkingDirectory, SensorSecurity.UserDirectory(sid), StringComparison.OrdinalIgnoreCase)
                || CanonicalSid(principal) != SensorSecurity.SystemSid.Value || definition.Principal.LogonType != 5 || definition.Principal.RunLevel != 1
                || definition.Triggers.Count != 1 || definition.Triggers.Item(1).Type != 9 || CanonicalSid((string)definition.Triggers.Item(1).UserId) != sid)
                throw new IOException("Существующая задача не принадлежит локальному датчику: оставлена без изменений.");
        }

        public static string CanonicalSid(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) throw new IOException("Отсутствует учётная запись задачи.");
            try { return new SecurityIdentifier(identity).Value; }
            catch (ArgumentException) { return ((SecurityIdentifier)new NTAccount(identity).Translate(typeof(SecurityIdentifier))).Value; }
        }

        static List<string> PayloadNames()
        {
            var names = new List<string>();
            using (var stream = Payload.Open("payload.SensorHelper.zip"))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name != entry.FullName || !(entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || entry.Name == "ResourcesWidgetSensor.exe" || entry.Name == "ResourcesWidgetSensor.exe.config"))
                        throw new IOException("Недопустимый файл полезной нагрузки датчика.");
                    names.Add(entry.Name);
                }
            return names;
        }
        static void CheckKnownFiles(string dir, string sid)
        {
            foreach (string name in PayloadNames())
            {
                string file = Path.Combine(dir, name);
                SensorSecurity.RejectReparseFile(file);
                if (File.Exists(file)) SensorSecurity.AssertFile(file, sid);
            }
            foreach (string name in new[] { SensorSecurity.MarkerName, "temperature.json", "temperature.json.new", "PawnIO_setup.exe", "THIRD-PARTY.txt", "sensor-install.log", "SensorSetup.exe" })
            {
                string file = Path.Combine(dir, name);
                SensorSecurity.RejectReparseFile(file);
                if (File.Exists(file)) SensorSecurity.AssertFile(file, sid);
            }
        }
        static void Record(string sid, string line)
        {
            string path = Path.Combine(SensorSecurity.UserDirectory(sid), "sensor-install.log");
            if (File.Exists(path)) SensorSecurity.AssertFile(path, sid);
            File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + line + Environment.NewLine);
            SensorSecurity.SecureFile(path, sid);
        }

        static int Install(string sid)
        {
            AssertRegistration(sid);
            dynamic service = Scheduler();
            dynamic folder = service.GetFolder("\\");
            dynamic existing = ExistingTask(folder, SensorSecurity.TaskName(sid));
            string dir = SensorSecurity.UserDirectory(sid);
            if (existing != null) { SensorSecurity.AssertInstalledDirectory(sid); AssertTask(existing, sid); }
            SensorSecurity.EnsureDirectory(SensorSecurity.Root, null);
            if (Directory.Exists(dir))
            {
                SensorSecurity.AssertInstalledDirectory(sid);
                CheckKnownFiles(dir, sid);
            }
            else SensorSecurity.EnsureDirectory(dir, sid);
            CheckKnownFiles(dir, sid);
            if (existing != null) { existing.Stop(0); WaitForStop(existing); }
            string marker = Path.Combine(dir, SensorSecurity.MarkerName);
            File.WriteAllText(marker, SensorSecurity.MarkerValue);
            SensorSecurity.SecureFile(marker, sid);
            Record(sid, "Preparing local read-only CPU sensor 1.1.1; no HTTP, no remote interface.");
            using (var stream = Payload.Open("payload.SensorHelper.zip"))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                foreach (var entry in archive.Entries)
                {
                    string file = Path.Combine(dir, entry.Name);
                    using (var input = entry.Open())
                    using (var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                    SensorSecurity.SecureFile(file, sid);
                }
            Payload.Extract(Payload.Notice, Path.Combine(dir, "THIRD-PARTY.txt"));
            SensorSecurity.SecureFile(Path.Combine(dir, "THIRD-PARTY.txt"), sid);
            string manager = Path.Combine(dir, "SensorSetup.exe");
            if (!string.Equals(Path.GetFullPath(Program.SelfPath), manager, StringComparison.OrdinalIgnoreCase))
            {
                Program.CopyApprovedImage(manager);
                SensorSecurity.SecureFile(manager, sid);
            }
            if (!PawnAvailable())
            {
                string driver = Path.Combine(dir, "PawnIO_setup.exe");
                Payload.Extract("payload.PawnIO_setup.exe", driver);
                SensorSecurity.SecureFile(driver, sid);
                if (Hash(driver) != PawnHash || !Authenticode.VerifyPawnIo(driver)) throw new IOException("Подпись или контрольная сумма PawnIO не подтверждена.");
                using (var process = Process.Start(new ProcessStartInfo(driver, "-install -silent") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir }))
                {
                    if (!process.WaitForExit(45000)) throw new IOException("Установщик PawnIO не завершился за 45 секунд.");
                    if (process.ExitCode != 0 && process.ExitCode != 3010) throw new IOException("Установщик PawnIO: код " + process.ExitCode);
                }
                if (!PawnAvailable()) throw new IOException("PawnIO после установки недоступен. Может потребоваться перезагрузка Windows.");
            }
            Record(sid, "PawnIO available. Registering own SYSTEM task.");
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "ResourcesWidget local read-only CPU temperature; protected file snapshot only.";
            definition.Principal.UserId = "SYSTEM";
            definition.Principal.LogonType = 5;
            definition.Principal.RunLevel = 1;
            dynamic trigger = definition.Triggers.Create(9);
            trigger.UserId = sid;
            trigger.Enabled = true;
            dynamic action = definition.Actions.Create(0);
            action.Path = Executable(sid);
            action.Arguments = Arguments(sid);
            action.WorkingDirectory = dir;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.RestartCount = 3;
            definition.Settings.RestartInterval = "PT1M";
            string sddl = "O:BAG:BAD:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;" + sid + ")";
            dynamic registered = folder.RegisterTaskDefinition(SensorSecurity.TaskName(sid), definition, 6, "SYSTEM", null, 5, sddl);
            using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey(sid)))
            {
                key.SetValue("DisplayName", "Системные ресурсы — локальный датчик температуры");
                key.SetValue("DisplayVersion", "1.1.1");
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", Options.Quote(manager) + " --sensor-remove --for-user " + sid);
                key.SetValue("NoModify", 1);
                key.SetValue("NoRepair", 1);
            }
            registered.Run(null);
            string snapshot = SensorSecurity.SnapshotPath(sid);
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(500);
                if (!File.Exists(snapshot)) continue;
                SensorSecurity.AssertFile(snapshot, sid);
                string label;
                if (ResourcesWidget.LocalSensorSnapshot.Read(sid, out label).HasValue)
                {
                    Record(sid, "PASS: fresh CPU temperature snapshot available.");
                    return 0;
                }
            }
            Record(sid, "Helper installed, but no fresh CPU temperature. Review temperature.json and task status.");
            return 3;
        }

        static void WaitForStop(dynamic task)
        {
            for (int i = 0; i < 40; i++) { if (task.State != 4) return; Thread.Sleep(100); }
            throw new IOException("Локальный помощник не остановился: обновление отменено.");
        }
        static int Remove(string sid)
        {
            AssertRegistration(sid);
            SensorSecurity.AssertInstalledDirectory(sid);
            string dir = SensorSecurity.UserDirectory(sid);
            CheckKnownFiles(dir, sid);
            dynamic service = Scheduler();
            dynamic folder = service.GetFolder("\\");
            dynamic task = ExistingTask(folder, SensorSecurity.TaskName(sid));
            if (task != null)
            {
                AssertTask(task, sid);
                task.Stop(0);
                WaitForStop(task);
                folder.DeleteTask(SensorSecurity.TaskName(sid), 0);
            }
            var remove = PayloadNames();
            remove.AddRange(new[] { "temperature.json", "temperature.json.new", "PawnIO_setup.exe", "THIRD-PARTY.txt", "sensor-install.log" });
            foreach (string name in remove) { string path = Path.Combine(dir, name); if (File.Exists(path)) File.Delete(path); }
            string manager = Path.Combine(dir, "SensorSetup.exe");
            if (File.Exists(manager))
            {
                if (string.Equals(Path.GetFullPath(Program.SelfPath), manager, StringComparison.OrdinalIgnoreCase))
                {
                    // Queue only a unique tombstone: a later reinstall must never be removed after reboot.
                    string tombstone = NewRemovalTombstone(dir);
                    // Source manager is now verified under an admin-owned path; the user cannot replace it.
                    Program.ReleaseProtectedManagerImageLock();
                    File.Move(manager, tombstone);
                    if (!MoveFileEx(tombstone, null, 4)) throw new IOException("Датчик остановлен; временный файл удаления остался: " + tombstone);
                }
                else File.Delete(manager);
            }
            Registry.LocalMachine.DeleteSubKey(UninstallKey(sid), false);
            if (Directory.GetFileSystemEntries(dir).Length == 1)
            {
                File.Delete(Path.Combine(dir, SensorSecurity.MarkerName));
                Directory.Delete(dir, false);
            }
            if (Directory.GetFileSystemEntries(SensorSecurity.Root).Length == 0) Directory.Delete(SensorSecurity.Root, false);
            return 0; // shared PawnIO and all unrelated files/processes/tasks remain untouched
        }
        static string UninstallKey(string sid) { return @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ResourcesWidgetSensor-" + SensorSecurity.UserSid(sid).Value; }
        public static string NewRemovalTombstone(string directory) { return Path.Combine(directory, "SensorSetup.removed-" + Guid.NewGuid().ToString("N") + ".exe"); }
        static void AssertRegistration(string sid)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(UninstallKey(sid)))
            {
                if (key == null) return;
                string location = key.GetValue("InstallLocation") as string;
                if (!string.Equals(location, SensorSecurity.UserDirectory(sid), StringComparison.OrdinalIgnoreCase)) throw new IOException("Чужая запись удаления датчика оставлена без изменений.");
            }
        }
        static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        }
    }
}
