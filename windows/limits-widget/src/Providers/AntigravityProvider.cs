using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LimitsWidget
{
    // agy -p "/usage" --output-format json: read-only команда Antigravity CLI, отвечает без запроса к модели.
    // command.data.groups[] -> buckets[] { window: "5h" | "weekly", remaining_fraction, reset_time }
    sealed class AntigravityProvider : IProvider
    {
        public string Id { get { return "antigravity"; } }

        public ServiceSnapshot Collect(ProviderContext ctx)
        {
            var snap = ProviderUtil.NewSnap(ctx, Id);
            snap.Source = "agy -p \"/usage\" — квоты групп моделей Antigravity";
            string text = ProviderUtil.Fixture(ctx, "antigravity-usage.json");
            bool fixture = text != null;
            if (!fixture)
            {
                string exe = Cli.Resolve(ctx.Svc.Command, "agy");
                if (exe == null)
                    return ProviderUtil.Fail(snap, SvcStatus.NoData, "нет CLI", "agy (Antigravity CLI) не найден в PATH", Placeholders());
                // Лимит в 1 процесс: agy отвечает на /usage сам, а фоновый апдейтер (вспышка окна терминала
                // от `agy --version`) и MCP-серверы через npx запуститься не могут.
                var r = Cli.Run(exe, "-p \"/usage\" --output-format json", ctx.Svc.TimeoutSeconds * 1000, ctx.Svc.MaxProcesses);
                if (!r.Ok)
                    return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка", "agy /usage: " + r.Describe(), Placeholders());
                text = r.StdOut;
            }

            var root = Json.TryParse(text);
            var groups = Json.Arr(Json.Get(root, "command.data.groups"));
            if (groups != null)
            {
                foreach (var g in groups)
                {
                    string group = Json.Str(Json.Get(g, "name")) ?? "Модели";
                    string groupTip = Json.Str(Json.Get(g, "description"));
                    var buckets = Json.Arr(Json.Get(g, "buckets"));
                    if (buckets == null) continue;
                    foreach (var b in buckets.OrderBy(x => WindowOrder(Json.Str(Json.Get(x, "window")))))
                    {
                        // proto3 опускает нулевые значения: нет remaining_fraction -> квота исчерпана
                        double remaining = Json.Num(Json.Get(b, "remaining_fraction")) ?? 0;
                        var bar = MakeBar(group, Json.Str(Json.Get(b, "window")), remaining,
                            Json.Time(Json.Get(b, "reset_time")), groupTip);
                        ProviderUtil.MarkIdle(bar, ctx.Now, WindowMinutes(Json.Str(Json.Get(b, "window"))));
                        if (Json.Bool(Json.Get(b, "disabled")) == true) bar.Note = "отключено";
                        snap.Bars.Add(bar);
                    }
                }
            }
            else
            {
                // запасной разбор табличного ответа: группа \t окно \t 100% \t ISO-время
                string response = Json.Str(Json.Get(root, "response")) ?? text;
                foreach (var line in response.Split('\n'))
                {
                    var f = line.Split('\t');
                    if (f.Length < 4) continue;
                    double pct;
                    if (!double.TryParse(f[2].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out pct)) continue;
                    string window = f[1].IndexOf("five", StringComparison.OrdinalIgnoreCase) >= 0 ? "5h"
                        : f[1].IndexOf("week", StringComparison.OrdinalIgnoreCase) >= 0 ? "weekly" : f[1].Trim();
                    var bar = MakeBar(f[0].Trim(), window, pct / 100.0, Json.Time(f[3].Trim()), null);
                    ProviderUtil.MarkIdle(bar, ctx.Now, WindowMinutes(window));
                    snap.Bars.Add(bar);
                }
            }

            if (snap.Bars.Count == 0)
            {
                string hint = Cli.FirstLine(Json.Str(Json.Get(root, "response")) ?? text);
                return ProviderUtil.Fail(snap, SvcStatus.Error, "ошибка",
                    "agy /usage не вернул квот" + (hint != null ? ": " + hint : ""), Placeholders());
            }
            AddCredits(snap, ctx, fixture);
            ProviderUtil.MarkFresh(snap, ctx, fixture);
            return snap;
        }

        // agy -p "/credits": локальная команда без модели; command.data.remaining_credits — платные кредиты Google One.
        // Ошибка не показывается пользователю: полосы квот важнее, только запись в журнал.
        static void AddCredits(ServiceSnapshot snap, ProviderContext ctx, bool fixture)
        {
            string text = ProviderUtil.Fixture(ctx, "antigravity-credits.json");
            if (text == null)
            {
                if (fixture) return;
                string exe = Cli.Resolve(ctx.Svc.Command, "agy");
                if (exe == null) return;
                var r = Cli.Run(exe, "-p \"/credits\" --output-format json", ctx.Svc.TimeoutSeconds * 1000, ctx.Svc.MaxProcesses);
                if (!r.Ok)
                {
                    Log.Write("agy /credits: " + r.Describe());
                    return;
                }
                text = r.StdOut;
            }
            double? remaining = Json.Num(Json.Get(Json.TryParse(text), "command.data.remaining_credits"));
            if (!remaining.HasValue || remaining.Value <= 0) return;
            var e = new ExtraItem();
            e.Icon = "\uE8C7";
            e.Label = "AI-кредиты Google";
            e.Value = CreditStats.Number(remaining.Value);
            e.Accent = "info";
            e.Tip = "Платные AI-кредиты Google One: расходуются в Antigravity, когда исчерпаны квоты групп моделей.";
            snap.Extras.Add(e);
        }

        static double? WindowMinutes(string window)
        {
            return window == "5h" ? 300 : window == "daily" ? 1440 : window == "weekly" ? 10080 : (double?)null;
        }

        static int WindowOrder(string window)
        {
            return window == "5h" ? 0 : window == "daily" ? 1 : window == "weekly" ? 2 : 3;
        }

        static LimitBar MakeBar(string group, string window, double remaining, DateTime? reset, string tip)
        {
            var bar = new LimitBar();
            string g = ShortGroup(group);
            switch (window)
            {
                case "5h": bar.Chip = "5" + Fmt.Nbsp + "ч"; bar.Label = g + " · окно 5 часов"; break;
                case "daily": bar.Chip = "24" + Fmt.Nbsp + "ч"; bar.Label = g + " · сутки"; break;
                case "weekly": bar.Chip = "7" + Fmt.Nbsp + "д"; bar.Label = g + " · неделя"; break;
                default: bar.Chip = window ?? "?"; bar.Label = g; break;
            }
            bar.Used = Math.Max(0, Math.Min(1, 1 - remaining)) * 100;
            bar.ResetsAt = reset;
            bar.Tip = tip;
            return bar;
        }

        static string ShortGroup(string group)
        {
            if (group.Equals("Gemini Models", StringComparison.OrdinalIgnoreCase)) return "Gemini";
            if (group.Equals("Claude and GPT models", StringComparison.OrdinalIgnoreCase)) return "Claude и GPT";
            return group;
        }

        static LimitBar[] Placeholders()
        {
            return new[]
            {
                ProviderUtil.Placeholder("5" + Fmt.Nbsp + "ч", "Gemini · окно 5 часов"),
                ProviderUtil.Placeholder("7" + Fmt.Nbsp + "д", "Gemini · неделя"),
                ProviderUtil.Placeholder("5" + Fmt.Nbsp + "ч", "Claude и GPT · окно 5 часов"),
                ProviderUtil.Placeholder("7" + Fmt.Nbsp + "д", "Claude и GPT · неделя")
            };
        }
    }
}
