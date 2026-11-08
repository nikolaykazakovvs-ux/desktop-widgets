using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopWidgets.Limits.Core
{
    // Перенос windows/limits-widget/src/Json.cs: JavaScriptSerializer (только .NET Framework) заменён на Utf8JsonReader.
    // Форма данных та же: объекты -> Dictionary<string, object>, массивы -> object[], целые -> int/long, дробные -> double.
    // Serialize повторяет формат WPF-версии байт в байт — от него зависит контракт телеметрии.
    public static class Json
    {
        static readonly Regex LongFraction = new Regex(@"(\.\d{7})\d+", RegexOptions.Compiled);
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static readonly JsonReaderOptions ReaderOptions = new JsonReaderOptions { MaxDepth = 512, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        public static object Parse(string text)
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), ReaderOptions);
            if (!reader.Read()) throw new FormatException("empty_json");
            object value = ReadValue(ref reader);
            if (reader.Read()) throw new FormatException("trailing_json");
            return value;
        }

        public static object TryParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return Parse(text); }
            catch { return null; }
        }

        static object ReadValue(ref Utf8JsonReader r)
        {
            switch (r.TokenType)
            {
                case JsonTokenType.StartObject:
                    var d = new Dictionary<string, object>(StringComparer.Ordinal);
                    while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                    {
                        string key = r.GetString();
                        r.Read();
                        d[key] = ReadValue(ref r);
                    }
                    return d;
                case JsonTokenType.StartArray:
                    var list = new List<object>();
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray) list.Add(ReadValue(ref r));
                    return list.ToArray();
                case JsonTokenType.String: return r.GetString();
                case JsonTokenType.True: return true;
                case JsonTokenType.False: return false;
                case JsonTokenType.Null: return null;
                case JsonTokenType.Number:
                    if (r.TryGetInt32(out int i)) return i;
                    if (r.TryGetInt64(out long l)) return l;
                    return r.GetDouble();
                default: throw new FormatException("unexpected_token");
            }
        }

        public static object Get(object node, string path)
        {
            if (node == null || string.IsNullOrEmpty(path)) return node;
            foreach (string part in path.Split('.'))
            {
                if (!(node is IDictionary<string, object> d)) return null;
                if (!d.TryGetValue(part, out object v) || v == null) return null;
                node = v;
            }
            return node;
        }

        public static IDictionary<string, object> Obj(object node) => node as IDictionary<string, object>;

        public static IList<object> Arr(object node)
        {
            if (node is object[] arr) return arr;
            if (node is string || !(node is IList list)) return null;
            var res = new List<object>();
            foreach (var x in list) res.Add(x);
            return res;
        }

        public static string Str(object node)
        {
            if (node == null) return null;
            if (node is string s) return s;
            if (node is bool b) return b ? "true" : "false";
            return node is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : null;
        }

        public static double? Num(object node)
        {
            switch (node)
            {
                case null: return null;
                case int i: return i;
                case long l: return l;
                case decimal m: return (double)m;
                case double d: return d;
                case float f: return f;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v): return v;
                default: return null;
            }
        }

        public static bool? Bool(object node)
        {
            if (node is bool b) return b;
            if (node is string s) return s == "true" ? true : s == "false" ? false : (bool?)null;
            return null;
        }

        // ISO-строка или epoch (с/мс/мкс) -> локальное время.
        public static DateTime? Time(object node)
        {
            if (node == null) return null;
            if (node is DateTime dt) return dt.ToLocalTime();
            if (node is string s)
            {
                s = s.Trim();
                if (s.Length == 0) return null;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return FromEpoch(n);
                if (DateTimeOffset.TryParse(LongFraction.Replace(s, "$1"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
                    return dto.LocalDateTime;
                return null;
            }
            var num = Num(node);
            return num.HasValue ? FromEpoch(num.Value) : null;
        }

        public static DateTime? FromEpoch(double n)
        {
            if (n <= 0 || double.IsNaN(n) || double.IsInfinity(n)) return null;
            if (n > 1e17) n /= 1e9;
            else if (n > 1e14) n /= 1e6;
            else if (n > 1e11) n /= 1e3;
            try { return Epoch.AddSeconds(n).ToLocalTime(); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        public static string Serialize(object value, bool pretty)
        {
            var sb = new StringBuilder();
            Write(sb, value, pretty, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v, bool pretty, int indent)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { WriteString(sb, s); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is DateTime t)
            {
                WriteString(sb, t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                return;
            }
            if (v is Enum) { WriteString(sb, v.ToString()); return; }
            if (v is double || v is float)
            {
                double dv = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(dv) || double.IsInfinity(dv)) sb.Append("null");
                else sb.Append(dv.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is decimal || v is short || v is uint || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, pretty, indent + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(pretty ? ": " : ":");
                    Write(sb, kv.Value, pretty, indent + 1);
                }
                if (!first) NewLine(sb, pretty, indent);
                sb.Append('}');
                return;
            }
            if (v is IEnumerable seq)
            {
                sb.Append('[');
                bool first = true;
                foreach (var x in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, pretty, indent + 1);
                    Write(sb, x, pretty, indent + 1);
                }
                if (!first) NewLine(sb, pretty, indent);
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        static void NewLine(StringBuilder sb, bool pretty, int indent)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', indent * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
