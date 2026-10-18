using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LimitsWidget
{
    interface IProvider
    {
        string Id { get; }
        ServiceSnapshot Collect(ProviderContext ctx);
    }

    sealed class ProviderContext
    {
        public Config Config;
        public ServiceConfig Svc;
        public string FixturesDir;
        public DateTime Now;
        public State State;   // история остатка кредитов и прочее состояние; может быть null
    }

    static class ProviderUtil
    {
        static readonly Regex NowToken = new Regex(@"\{\{now((?:[+-]?\d+[dhm])*)\}\}", RegexOptions.Compiled);
        static readonly Regex NowPart = new Regex(@"([+-]?)(\d+)([dhm])", RegexOptions.Compiled);

        public static ServiceSnapshot NewSnap(ProviderContext ctx, string id)
        {
            var s = new ServiceSnapshot();
            s.Id = id;
            s.Title = ctx.Svc.Title;
            s.Plan = string.IsNullOrEmpty(ctx.Svc.Plan) ? null : ctx.Svc.Plan;
            s.Url = ctx.Svc.Url;
            return s;
        }

        public static LimitBar Placeholder(string chip, string label)
        {
            return new LimitBar { Chip = chip, Label = label, Placeholder = true };
        }

        public static ServiceSnapshot Fail(ServiceSnapshot snap, SvcStatus status, string statusText, string message, params LimitBar[] placeholders)
        {
            snap.Status = status;
            snap.StatusText = statusText;
            snap.Message = message;
            snap.Bars.Clear();
            snap.Bars.AddRange(placeholders);
            return snap;
        }

        public static void MarkFresh(ServiceSnapshot snap, ProviderContext ctx, bool fixture)
        {
            snap.DataTime = ctx.Now;
            snap.Status = fixture ? SvcStatus.Fixture : SvcStatus.Ok;
            snap.StatusText = fixture ? "тест" : Fmt.Clock(ctx.Now);
        }

        // Тестовые ответы для --fixtures; {{now+2h13m}} заменяется на ISO-время относительно текущего момента.
        public static string Fixture(ProviderContext ctx, string name)
        {
            if (string.IsNullOrEmpty(ctx.FixturesDir)) return null;
            string p = Path.Combine(ctx.FixturesDir, name);
            if (!File.Exists(p)) return null;
            string text = File.ReadAllText(p, Encoding.UTF8);
            DateTime now = ctx.Now;
            return NowToken.Replace(text, m =>
            {
                DateTime t = now;
                int sign = 1;
                foreach (Match part in NowPart.Matches(m.Groups[1].Value))
                {
                    if (part.Groups[1].Value.Length > 0) sign = part.Groups[1].Value == "-" ? -1 : 1;
                    double v = double.Parse(part.Groups[2].Value) * sign;
                    switch (part.Groups[3].Value)
                    {
                        case "d": t = t.AddDays(v); break;
                        case "h": t = t.AddHours(v); break;
                        default: t = t.AddMinutes(v); break;
                    }
                }
                return t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            });
        }

        // Неначатое окно: 0 % и сброс «сейчас + длина окна» — время сброса плавает, показывать его бессмысленно.
        public static void MarkIdle(LimitBar bar, DateTime now, double? windowMinutes)
        {
            if (!windowMinutes.HasValue || !bar.ResetsAt.HasValue || !bar.Used.HasValue || bar.Used.Value > 0) return;
            var expected = now.AddMinutes(windowMinutes.Value);
            if (Math.Abs((bar.ResetsAt.Value - expected).TotalMinutes) <= 10)
            {
                bar.ResetsAt = null;
                bar.Note = "окно не начато";
            }
        }

        public static string WindowChip(double? minutes)
        {
            if (!minutes.HasValue || minutes.Value <= 0) return "?";
            double m = minutes.Value;
            if (Math.Abs(m - 10080) < 1) return "7" + Fmt.Nbsp + "д";
            if (m % 1440 == 0) return (m / 1440) + Fmt.Nbsp + "д";
            if (m % 60 == 0) return (m / 60) + Fmt.Nbsp + "ч";
            return m + Fmt.Nbsp + "мин";
        }

        public static string WindowWord(double? minutes)
        {
            if (minutes.HasValue && Math.Abs(minutes.Value - 10080) < 1) return "неделя";
            if (minutes.HasValue && Math.Abs(minutes.Value - 300) < 1) return "окно 5 часов";
            return "окно " + WindowChip(minutes);
        }
    }
}
