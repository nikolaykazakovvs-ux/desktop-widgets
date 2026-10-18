using System;
using System.Globalization;

namespace ResourcesWidget
{
    static class Fmt
    {
        public const string Nbsp = " ";
        static readonly CultureInfo Ru = new CultureInfo("ru-RU");
        static readonly string[] SizeUnits = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        static readonly string[] BitUnits = { "бит/с", "Кбит/с", "Мбит/с", "Гбит/с" };

        public static string Num(double v, int decimals)
        {
            return v.ToString("F" + decimals, CultureInfo.InvariantCulture).Replace('.', ',');
        }

        // "4 120" — с неразрывным пробелом между разрядами
        public static string Int(long v)
        {
            return v.ToString("#,0", Ru).Replace(' ', ' ');
        }

        // Plural(312, "процесс", "процесса", "процессов") -> "процессов"
        public static string Plural(long n, string one, string few, string many)
        {
            long a = Math.Abs(n) % 100, b = a % 10;
            if (a >= 11 && a <= 14) return many;
            if (b == 1) return one;
            if (b >= 2 && b <= 4) return few;
            return many;
        }

        public static string Percent(double v)
        {
            if (v > 0 && v < 1) return "<1" + Nbsp + "%";
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + Nbsp + "%";
        }

        // Доля процесса: "0 %", "0,4 %", "12 %"
        public static string SharePercent(double v)
        {
            if (v < 0.05) return "0" + Nbsp + "%";
            return (v < 10 ? Num(v, 1) : Num(v, 0)) + Nbsp + "%";
        }

        static int SizeUnit(double bytes)
        {
            int u = 0;
            while (bytes >= 1024 && u < SizeUnits.Length - 1) { bytes /= 1024; u++; }
            return u;
        }

        static int SizeDecimals(int unit, double value)
        {
            if (unit <= 2) return 0;
            if (unit == 4) return 2;
            return value >= 100 ? 0 : 1;
        }

        // "154 ГБ", "19,5 ГБ", "2,73 ТБ", "512 МБ"
        public static string Size(double bytes)
        {
            if (bytes < 0) bytes = 0;
            int u = SizeUnit(bytes);
            double v = bytes / Math.Pow(1024, u);
            return Num(v, SizeDecimals(u, v)) + Nbsp + SizeUnits[u];
        }

        // Обе величины в единицах большей: "154 из 601 ГБ", "0,60 из 2,73 ТБ"
        public static string SizePair(double part, double total)
        {
            int u = SizeUnit(Math.Max(total, 0));
            double k = Math.Pow(1024, u), t = total / k;
            int d = SizeDecimals(u, t);
            return Num(Math.Max(part, 0) / k, d) + " из " + Num(t, d) + Nbsp + SizeUnits[u];
        }

        // Скорость обмена с диском: "340 КБ/с", "1,2 МБ/с", "152 МБ/с", "1,05 ГБ/с"
        public static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 0 || double.IsNaN(bytesPerSecond)) bytesPerSecond = 0;
            if (bytesPerSecond < 1024 * 1024) return Num(bytesPerSecond / 1024, 0) + Nbsp + "КБ/с";
            double mb = bytesPerSecond / (1024 * 1024);
            if (mb < 1024) return Num(mb, mb < 100 ? 1 : 0) + Nbsp + "МБ/с";
            return Num(mb / 1024, 2) + Nbsp + "ГБ/с";
        }

        // Скорость сети: в битах (как в диспетчере задач) или в байтах.
        public static string NetRate(double bytesPerSecond, bool bits)
        {
            if (bytesPerSecond < 0 || double.IsNaN(bytesPerSecond)) bytesPerSecond = 0;
            if (!bits) return Rate(bytesPerSecond);
            double v = bytesPerSecond * 8 / 1000;   // Кбит/с
            int u = 1;
            while (v >= 1000 && u < BitUnits.Length - 1) { v /= 1000; u++; }
            int d = v >= 100 || v == 0 ? 0 : 1;
            if (u == 3 && v < 10) d = 2;
            return Num(v, d) + Nbsp + BitUnits[u];
        }

        // Две скорости в общей единице — той, что подходит большей:
        // { "32,1", "14,8", "Кбит/с" }, { "9,2", "0,64", "Мбит/с" }.
        public static string[] NetPair(double a, double b, bool bits)
        {
            double max = Math.Max(Math.Max(a, b), 0), div;
            string unit;
            if (bits)
            {
                double kbit = max * 8 / 1000;
                if (kbit < 1000) { div = 1000 / 8.0; unit = BitUnits[1]; }
                else if (kbit < 1e6) { div = 1e6 / 8; unit = BitUnits[2]; }
                else { div = 1e9 / 8; unit = BitUnits[3]; }
            }
            else
            {
                if (max < 1024 * 1024) { div = 1024; unit = "КБ/с"; }
                else if (max < 1024.0 * 1024 * 1024) { div = 1024 * 1024; unit = "МБ/с"; }
                else { div = 1024.0 * 1024 * 1024; unit = "ГБ/с"; }
            }
            return new[] { PairNum(a / div), PairNum(b / div), unit };
        }

        static string PairNum(double v)
        {
            if (double.IsNaN(v) || v < 0.005) return "0";
            return Num(v, v >= 100 ? 0 : v >= 1 ? 1 : 2);
        }

        // Скорость соединения адаптера: "1 Гбит/с", "2,5 Гбит/с", "100 Мбит/с"
        public static string LinkSpeed(long bitsPerSecond)
        {
            double v = bitsPerSecond;
            int u = 0;
            while (v >= 1000 && u < BitUnits.Length - 1) { v /= 1000; u++; }
            bool whole = Math.Abs(v - Math.Round(v)) < 0.05;
            return Num(v, whole ? 0 : 1) + Nbsp + BitUnits[u];
        }

        public static string Ghz(double mhz)
        {
            return Num(mhz / 1000, 2) + Nbsp + "ГГц";
        }

        public static string Clock(DateTime t)
        {
            return t.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        // Длительность с секундами для коротких перебоев: "45 с", "2 мин 05 с", "1 ч 12 мин"
        public static string Duration(TimeSpan s)
        {
            if (s.TotalSeconds < 60) return Math.Max(1, (int)Math.Round(s.TotalSeconds)) + Nbsp + "с";
            if (s.TotalMinutes < 10) return (int)s.TotalMinutes + Nbsp + "мин " + s.Seconds.ToString("00") + Nbsp + "с";
            return Span(s);
        }

        // "2 ч 14 мин", "5 д 19 ч", "12 мин"
        public static string Span(TimeSpan s)
        {
            if (s.TotalMinutes < 1) return "<" + Nbsp + "1" + Nbsp + "мин";
            if (s.TotalHours < 1) return (int)s.TotalMinutes + Nbsp + "мин";
            if (s.TotalDays < 1)
            {
                int h = (int)s.TotalHours, m = s.Minutes;
                return m > 0 ? h + Nbsp + "ч " + m + Nbsp + "мин" : h + Nbsp + "ч";
            }
            int d = (int)s.TotalDays, hh = s.Hours;
            return hh > 0 ? d + Nbsp + "д " + hh + Nbsp + "ч" : d + Nbsp + "д";
        }
    }
}
