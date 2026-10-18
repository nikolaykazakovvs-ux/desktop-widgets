using System;
using System.Globalization;

namespace LimitsWidget
{
    static class Fmt
    {
        public const string Nbsp = " ";
        static readonly string[] Dow = { "вс", "пн", "вт", "ср", "чт", "пт", "сб" };

        public static string Clock(DateTime t)
        {
            return t.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        static int DayDiff(DateTime t, DateTime now)
        {
            return (int)Math.Round((t.Date - now.Date).TotalDays);
        }

        // "16:05", "завтра 06:50", "ср 06:50", "30.09 06:50"
        public static string When(DateTime t, DateTime now)
        {
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

        public static string Percent(double v)
        {
            if (v > 0 && v < 1) return "<1" + Nbsp + "%";
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + Nbsp + "%";
        }
    }
}
