using System;
using System.Globalization;

namespace LimitsWidget
{
    static class Fmt
    {
        public const string Nbsp = " ";
        static readonly string[] Dow = { "вс", "пн", "вт", "ср", "чт", "пт", "сб" };

        // Хранение — UTC, показ — местное время. Unspecified в этом приложении всегда означает местное время
        // (так его отдают разборщики текстовых ответов), UTC-значения переводятся на границе отображения.
        public static DateTime Local(DateTime t)
        {
            if (t.Kind == DateTimeKind.Utc) return t.ToLocalTime();
            return t.Kind == DateTimeKind.Local ? t : DateTime.SpecifyKind(t, DateTimeKind.Local);
        }

        public static DateTime Utc(DateTime t)
        {
            if (t.Kind == DateTimeKind.Utc) return t;
            return (t.Kind == DateTimeKind.Local ? t : DateTime.SpecifyKind(t, DateTimeKind.Local)).ToUniversalTime();
        }

        public static DateTime? Utc(DateTime? t)
        {
            return t.HasValue ? (DateTime?)Utc(t.Value) : null;
        }

        // Сравнение моментов независимо от Kind (DateTime сравнивает такты и Kind не учитывает).
        public static bool NotAfter(DateTime a, DateTime b)
        {
            return Utc(a) <= Utc(b);
        }

        public static string Clock(DateTime t)
        {
            return Local(t).ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        static int DayDiff(DateTime t, DateTime now)
        {
            return (int)Math.Round((Local(t).Date - Local(now).Date).TotalDays);
        }

        // "16:05", "завтра 06:50", "ср 06:50", "30.09 06:50"
        public static string When(DateTime t, DateTime now)
        {
            t = Local(t);
            int days = DayDiff(t, now);
            if (days == 0) return Clock(t);
            if (days == 1) return "завтра " + Clock(t);
            if (days == -1) return "вчера " + Clock(t);
            if (days > 1 && days < 7) return Dow[(int)t.DayOfWeek] + " " + Clock(t);
            return t.ToString("dd.MM", CultureInfo.InvariantCulture) + " " + Clock(t);
        }

        // Время, к которому относятся данные: "11:27", "вчера 22:11", "15.09 03:17"
        public static string Age(DateTime t, DateTime now)
        {
            t = Local(t);
            int days = -DayDiff(t, now);
            if (days == 0) return Clock(t);
            if (days == 1) return "вчера " + Clock(t);
            return t.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
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

        // Длительность в секундах: «45 с», «12 мин 5 с», «1 ч 20 мин»
        public static string Duration(long seconds)
        {
            if (seconds < 60) return seconds + Nbsp + "с";
            if (seconds < 3600) return (seconds / 60) + Nbsp + "мин" + (seconds % 60 > 0 ? " " + (seconds % 60) + Nbsp + "с" : "");
            return Span(TimeSpan.FromSeconds(seconds));
        }

        // 1 234 567 -> «1,2 млн», 45 300 -> «45,3 тыс», 999 -> «999»
        public static string Tokens(long v)
        {
            var ru = CultureInfo.GetCultureInfo("ru-RU");
            if (v < 1000) return v.ToString(CultureInfo.InvariantCulture);
            if (v < 1000000) return (v / 1e3).ToString(v < 100000 ? "0.#" : "0", ru) + Nbsp + "тыс";
            if (v < 1000000000) return (v / 1e6).ToString(v < 100000000 ? "0.#" : "0", ru) + Nbsp + "млн";
            return (v / 1e9).ToString("0.#", ru) + Nbsp + "млрд";
        }

        public static string Percent(double v)
        {
            if (v > 0 && v < 1) return "<1" + Nbsp + "%";
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + Nbsp + "%";
        }
    }
}
