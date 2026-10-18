using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyTitle("ResourcesWidget local CPU sensor")]
[assembly: AssemblyProduct("ResourcesWidgetSensor")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace ResourcesWidgetSensor
{
    static class SensorProgram
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetDefaultDllDirectories(uint flags);

        static string Snapshot(string sid, double? value, string sensor, string status)
        {
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                { "schema", 1 }, { "userSid", sid }, { "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "temperatureC", value.HasValue ? (object)Math.Round(value.Value, 2) : null },
                { "sensor", sensor ?? "CPU" }, { "status", status }
            });
        }
        static void Write(string sid, string json)
        {
            SensorSecurity.AssertInstalledDirectory(sid);
            string target = SensorSecurity.SnapshotPath(sid), pending = target + ".new";
            SensorSecurity.RejectReparseFile(pending);
            if (File.Exists(pending)) SensorSecurity.AssertFile(pending, sid);
            if (File.Exists(target)) SensorSecurity.AssertFile(target, sid);
            File.WriteAllText(pending, json, new UTF8Encoding(false));
            SensorSecurity.SecureFile(pending, sid);
            if (File.Exists(target)) File.Replace(pending, target, null);
            else File.Move(pending, target);
            SensorSecurity.AssertFile(target, sid);
        }
        public static int Main(string[] args)
        {
            if (!SetDefaultDllDirectories(0x00000200 | 0x00000800)) return 2; // application directory + System32 only
            if (args.Length == 1 && args[0] == "--probe")
            {
                try
                {
                    using (var reader = new SensorReader())
                    {
                        string label;
                        double? value = reader.Read(out label);
                        Console.WriteLine(Snapshot(WindowsIdentity.GetCurrent().User.Value, value, label, value.HasValue ? "ok" : "no_data"));
                        return value.HasValue ? 0 : 3;
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
            }
            if (args.Length != 2 || args[0] != "--user-sid") return 2;
            string sid;
            try
            {
                sid = SensorSecurity.UserSid(args[1]).Value;
                if (!WindowsIdentity.GetCurrent().User.Equals(SensorSecurity.SystemSid)) return 2;
                string installed = SensorSecurity.UserDirectory(sid).TrimEnd('\\') + "\\";
                if (!string.Equals(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory), installed, StringComparison.OrdinalIgnoreCase)) return 2;
                SensorSecurity.AssertInstalledDirectory(sid);
                Environment.CurrentDirectory = SensorSecurity.UserDirectory(sid);
                foreach (string file in Directory.GetFiles(SensorSecurity.UserDirectory(sid), "*.dll")) SensorSecurity.AssertFile(file, sid);
            }
            catch { return 2; }
            using (var singleton = new Mutex(false, "Global\\ResourcesWidgetSensor-" + sid))
            {
                try { if (!singleton.WaitOne(0)) return 0; }
                catch (AbandonedMutexException) { }
                try
                {
                    using (var reader = new SensorReader())
                    {
                        while (true)
                        {
                            try
                            {
                                string label;
                                double? value = reader.Read(out label);
                                Write(sid, Snapshot(sid, value, label, value.HasValue ? "ok" : "no_data"));
                            }
                            catch { try { Write(sid, Snapshot(sid, null, "CPU", "error")); } catch { return 1; } }
                            Thread.Sleep(1000);
                        }
                    }
                }
                catch { try { Write(sid, Snapshot(sid, null, "CPU", "error")); } catch { } return 1; }
                finally { singleton.ReleaseMutex(); }
            }
        }
    }
}
