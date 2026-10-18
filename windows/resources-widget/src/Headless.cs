using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResourcesWidget
{
    // Режимы без окна: --dump (показатели в JSON) и --render (карточка в PNG) — для проверки и отладки.
    // state.json не читают и не пишут.
    static class Headless
    {
        static Config LoadConfig(CliOptions o)
        {
            return Config.Load(o.ConfigPath ?? Path.Combine(Program.BaseDir, "config.json"));
        }

        public static int Dump(CliOptions o)
        {
            var cfg = LoadConfig(o);
            using (var sampler = new Sampler(cfg, Program.NetLogDir, true))
            {
                sampler.Prime();
                Thread.Sleep(1000);
                var s = sampler.Collect(false);
                File.WriteAllText(o.DumpPath, Json.Serialize(s.ToDict(), true), new UTF8Encoding(false));
            }
            return 0;
        }

        // Журнал сети в PNG: список дней или один день (--journal-day yyyy-MM-dd). Журнал только читается.
        public static int RenderJournal(CliOptions o)
        {
            var cfg = LoadConfig(o);
            var theme = Theme.Resolve(o.Theme ?? "auto");
            using (var journal = new NetJournal(Program.NetLogDir, true, cfg))
            {
                var view = new PeakJournal(theme, cfg, MenuStyles.ToolTip(theme), journal);
                DateTime day;
                if (o.JournalDay != null && DateTime.TryParseExact(o.JournalDay, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day))
                    view.ShowDay(day);
                Brush backdrop = o.Transparent ? (Brush)Brushes.Transparent : new SolidColorBrush(theme.Dark ? Theme.C("#FF1B2A44") : Theme.C("#FFB9CCE4"));
                var root = new Grid { Background = backdrop };
                root.Children.Add(view.Root);
                Save(root, o.JournalPath);
            }
            return 0;
        }

        static void Save(FrameworkElement root, string path)
        {
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();
            const double scale = 2;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        public static int Render(CliOptions o)
        {
            var cfg = LoadConfig(o);
            var state = State.Load(null);
            state.Compact = o.Compact;
            state.Columns = o.Columns;
            Sample s = null;
            using (var sampler = new Sampler(cfg, Program.NetLogDir, true))
            {
                // 20 замеров по 0,5 с, чтобы на графиках была история
                sampler.Prime();
                for (int i = 0; i < 20; i++)
                {
                    Thread.Sleep(500);
                    s = sampler.Collect(false);
                }
            }
            var theme = Theme.Resolve(o.Theme ?? "auto");
            string hotkey = null;
            uint mods, vk;
            Hotkey.TryParse(cfg.Hotkey ?? "", out mods, out vk, out hotkey);
            var card = new Card(s, theme, state, cfg, new CardActions(), hotkey);

            // подложка «под обои», чтобы видеть полупрозрачность карточки
            var backdrop = new LinearGradientBrush(
                theme.Dark ? Theme.C("#FF1B2A44") : Theme.C("#FFB9CCE4"),
                theme.Dark ? Theme.C("#FF3A2340") : Theme.C("#FFE6D3E8"), 35);
            var root = new Grid { Background = o.Transparent ? (Brush)Brushes.Transparent : backdrop };
            root.Children.Add(new Border { Padding = new Thickness(18), Child = card.Root });
            root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();

            const double scale = 2;
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
