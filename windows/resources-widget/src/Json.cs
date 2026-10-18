using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace ResourcesWidget
{
    // Тонкая обёртка над JavaScriptSerializer: объекты -> Dictionary<string, object>, массивы -> object[].
    static class Json
    {
        public static object Parse(string text)
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 512;
            return s.DeserializeObject(text);
        }

        public static object TryParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try { return Parse(text); }
            catch { return null; }
        }

        public static object Get(object node, string path)
        {
            if (node == null || string.IsNullOrEmpty(path)) return node;
            foreach (string part in path.Split('.'))
            {
                var d = node as IDictionary<string, object>;
                if (d == null) return null;
                object v;
                if (!d.TryGetValue(part, out v) || v == null) return null;
                node = v;
            }
            return node;
        }

        public static IDictionary<string, object> Obj(object node)
        {
            return node as IDictionary<string, object>;
        }

        public static IList<object> Arr(object node)
        {
            var arr = node as object[];
            if (arr != null) return arr;
            if (node is string) return null;
            var list = node as IList;
            if (list == null) return null;
            var res = new List<object>();
            foreach (var x in list) res.Add(x);
            return res;
        }

        public static string Str(object node)
        {
            if (node == null) return null;
            var s = node as string;
            if (s != null) return s;
            if (node is bool) return (bool)node ? "true" : "false";
            var f = node as IFormattable;
            return f != null ? f.ToString(null, CultureInfo.InvariantCulture) : null;
        }

        public static double? Num(object node)
        {
            if (node == null) return null;
            if (node is int) return (int)node;
            if (node is long) return (long)node;
            if (node is decimal) return (double)(decimal)node;
            if (node is double) return (double)node;
            if (node is float) return (float)node;
            var s = node as string;
            double d;
            if (s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        public static bool? Bool(object node)
        {
            if (node is bool) return (bool)node;
            var s = node as string;
            if (s == "true") return true;
            if (s == "false") return false;
            return null;
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
            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is DateTime)
            {
                WriteString(sb, ((DateTime)v).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
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
            var dict = v as IDictionary<string, object>;
            if (dict != null)
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
            var seq = v as IEnumerable;
            if (seq != null)
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
