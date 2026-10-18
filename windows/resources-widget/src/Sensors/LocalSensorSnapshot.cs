using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Web.Script.Serialization;
using ResourcesWidgetSensor;

namespace ResourcesWidget
{
    // Output-only transport: a protected local file, no listener, no commands, no account-controlled path.
    static class LocalSensorSnapshot
    {
        public const int MaxBytes = 2048;
        public static double? Parse(string text, string sid, DateTime nowUtc, out string sensor)
        {
            sensor = null;
            if (string.IsNullOrEmpty(text) || text.Length > MaxBytes) return null;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 4 };
                var data = json.DeserializeObject(text) as Dictionary<string, object>;
                if (data == null || data.Count != 6 || !data.ContainsKey("schema") || !data.ContainsKey("userSid") || !data.ContainsKey("utc") || !data.ContainsKey("temperatureC") || !data.ContainsKey("sensor") || !data.ContainsKey("status")) return null;
                if (!(data["schema"] is int) || (int)data["schema"] != 1 || data["userSid"] as string != sid || data["status"] as string != "ok") return null;
                DateTime at;
                if (!DateTime.TryParseExact(data["utc"] as string, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at) || at.Kind != DateTimeKind.Utc) return null;
                double age = (nowUtc - at).TotalSeconds;
                if (age < 0 || age > 5) return null;
                object number = data["temperatureC"];
                if (!(number is int) && !(number is long) && !(number is double) && !(number is decimal)) return null;
                double temperature = Convert.ToDouble(number, CultureInfo.InvariantCulture);
                string label = data["sensor"] as string;
                if (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature <= 0 || temperature >= 150 || string.IsNullOrEmpty(label) || label.Length > 96) return null;
                foreach (char c in label) if (char.IsControl(c)) return null;
                sensor = label;
                return temperature;
            }
            catch { return null; }
        }
        public static double? Read(out string sensor)
        {
            return Read(WindowsIdentity.GetCurrent().User.Value, out sensor);
        }
        public static double? Read(string sid, out string sensor)
        {
            sensor = null;
            try
            {
                SensorSecurity.UserSid(sid);
                SensorSecurity.AssertInstalledDirectory(sid);
                string path = SensorSecurity.SnapshotPath(sid);
                SensorSecurity.AssertFile(path, sid);
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (file.Length <= 0 || file.Length > MaxBytes) return null;
                    using (var reader = new StreamReader(file)) return Parse(reader.ReadToEnd(), sid, DateTime.UtcNow, out sensor);
                }
            }
            catch { return null; }
        }
    }
}
