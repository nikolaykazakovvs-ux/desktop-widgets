using System;
using System.Collections.Generic;
using System.Globalization;

namespace LimitsWidget
{
    // Расчёт расхода и прогноза по истории остатка кредитов. Знаменателя у кредитов нет,
    // поэтому расход виджет считает сам: сумма падений остатка между соседними точками.
    static class CreditStats
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double Unix(DateTime t)
        {
            return (t.ToUniversalTime() - Epoch).TotalSeconds;
        }

        // Расход после fromUnix; рост остатка (пополнение) расход не уменьшает.
        public static double Spent(List<double[]> s, double fromUnix)
        {
            double sum = 0;
            for (int i = 1; i < s.Count; i++)
            {
                if (s[i][0] <= fromUnix) continue;
                double drop = Math.Max(0, s[i - 1][1] - s[i][1]);
                double dt = s[i][0] - s[i - 1][0];
                // пара пересекает начало окна (разрыв наблюдений) — берём пропорциональную долю
                sum += s[i - 1][0] >= fromUnix || dt <= 0 ? drop : drop * (s[i][0] - fromUnix) / dt;
            }
            return sum;
        }

        // 52224.37 -> «52 224»
        public static string Number(double v)
        {
            var nf = new NumberFormatInfo { NumberGroupSeparator = Fmt.Nbsp, NumberGroupSizes = new[] { 3 } };
            return Math.Round(v).ToString("N0", nf);
        }

        // 248.78 USD -> «$248.78», 1250 USD -> «$1 250», 36.5 CNY -> «¥36.50»; неизвестная валюта — число и код
        public static string Money(double amount, string currency)
        {
            string cur = string.IsNullOrEmpty(currency) ? "USD" : currency.ToUpperInvariant();
            bool whole = Math.Abs(amount - Math.Round(amount)) < 0.005;
            var nf = new NumberFormatInfo { NumberGroupSeparator = Fmt.Nbsp, NumberGroupSizes = new[] { 3 }, NumberDecimalSeparator = "." };
            string num = amount.ToString(whole ? "N0" : "N2", nf);
            switch (cur)
            {
                case "USD": return "$" + num;
                case "EUR": return "€" + num;
                case "CNY": return "¥" + num;
                case "GBP": return "£" + num;
                default: return num + " " + cur;
            }
        }

        public static string Money(double amount)
        {
            return Money(amount, null);
        }

        public static ExtraItem Describe(double balance, bool unlimited, bool spending, List<double[]> samples, DateTime now)
        {
            var c = new ExtraItem();
            c.Icon = "\uE8C7";
            c.Label = "Кредиты";
            c.Accent = spending ? "warn" : "info";
            if (unlimited)
            {
                c.Value = "без ограничений";
                c.Detail = null;
                c.Tip = "Кредиты без ограничений.";
                return c;
            }
            c.Value = Number(balance);

            double nowU = Unix(now);
            bool has = samples != null && samples.Count > 0;
            double t0 = has ? samples[0][0] : nowU;
            double span = nowU - t0;
            if (!has || samples.Count < 2 || span < 3600)
            {
                c.Detail = "расход появится через час наблюдений";
            }
            else
            {
                var parts = new List<string>();
                double d24 = Spent(samples, nowU - 86400);
                string w = Fmt.Span(TimeSpan.FromSeconds(span));
                if (span >= 86400)
                    parts.Add(d24 >= 1 ? "за сутки −" + Number(d24) : "за сутки не тратились");
                else
                    parts.Add(d24 >= 1 ? "за " + w + " −" + Number(d24) : "за " + w + " не тратились");

                if (span >= 86400 + 3600)
                {
                    double d7 = Spent(samples, nowU - 7 * 86400);
                    if (d7 >= 1)
                        parts.Add("за " + (span >= 7 * 86400 ? "неделю" : w) + " −" + Number(d7));
                }

                if (spending && d24 >= 1 && balance >= 1)
                {
                    double window = Math.Min(span, 86400);
                    double left = balance / (d24 / window);
                    parts.Add(left >= 99 * 86400 ? "хватит больше чем на 99" + Fmt.Nbsp + "д"
                        : "хватит на ~" + Fmt.Span(TimeSpan.FromSeconds(left)));
                }
                c.Detail = string.Join(" · ", parts);
            }

            c.Tip = "Кредиты списываются, когда исчерпан недельный лимит Codex и «Работы».\nРасход считает виджет по истории остатка"
                + (has ? " с " + Fmt.When(Epoch.AddSeconds(t0).ToLocalTime(), now) : "") + ".";
            return c;
        }
    }
}
