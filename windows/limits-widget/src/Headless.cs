using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LimitsWidget
{
    // Режимы без окна: --dump (снимки в JSON) и --render (карточка в PNG) — для проверки и отладки.
    // state.json не трогают.
    static class Headless
    {
        static List<ServiceSnapshot> Collect(CliOptions o)
        {
            var cfg = Config.Load(o.ConfigPath ?? Path.Combine(Program.BaseDir, "config.json"));
            // история кредитов читается для тестового рендера; режим без окна state.json не сохраняет
            var state = State.Load(o.StatePath);
            return new Collector(cfg, state, o.FixturesDir).Collect();
        }

        public static int Dump(CliOptions o)
        {
            var snaps = Collect(o);
            var d = new Dictionary<string, object>();
            d["generatedAt"] = DateTime.Now;
            d["services"] = snaps.Select(s => (object)s.ToDict()).ToList();
            File.WriteAllText(o.DumpPath, Json.Serialize(d, true), new UTF8Encoding(false));
            return 0;
        }

        public static int Render(CliOptions o)
        {
            // порядок карточек — ручной, из state.json (как в окне)
            var snaps = CardOrder.Apply(Collect(o), State.Load(o.StatePath).CardOrder);
            var theme = Theme.Resolve(o.Theme ?? "auto");
            var card = CardView.Build(snaps, theme, DateTime.Now, DateTime.Now, false, new CardActions(), o.Compact, 1.0);

            // подложка «под обои», чтобы видеть полупрозрачность карточки
            var backdrop = new LinearGradientBrush(
                theme.Dark ? Theme.C("#FF1B2A44") : Theme.C("#FFB9CCE4"),
                theme.Dark ? Theme.C("#FF3A2340") : Theme.C("#FFE6D3E8"), 35);
            var root = new Grid { Background = backdrop };
            root.Children.Add(new Border { Padding = new Thickness(18), Child = card });
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();

            double scale = o.RenderScale;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(o.RenderPath)) enc.Save(fs);
            return 0;
        }
    }
}
