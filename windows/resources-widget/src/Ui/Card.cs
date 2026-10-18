using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ResourcesWidget
{
    sealed class CardActions
    {
        public Action Minimize;        // свернуть в трей
        public Action TogglePin;       // скрепка: закрепить / открепить поверх всех окон
        public Action CycleTheme;      // кнопка темы в шапке
        public Action<FrameworkElement> Menu;
        public Action<string> Sort;
        public Func<NetJournal> Journal;   // журнал сети (щелчок по «пик сегодня»)
    }

    // Карточка строится один раз под набор разделов, дальше каждый такт только меняет значения.
    // Если набор изменился (тема, вид, появилась видеокарта, подключили диск) — окно строит карточку заново.
    // Устройство как у «Лимитов нейросетей»: шапка — отдельная плашка, под ней плашка с разделами.
    sealed partial class Card
    {
        public const double ColumnWidth = 316;
        readonly Theme t;
        readonly State state;
        readonly Config cfg;
        readonly CardActions act;
        readonly string hotkey;
        readonly Style tipStyle;
        readonly Brush warnBrush, dangerBrush;
        readonly List<Action<Sample>> updaters = new List<Action<Sample>>();

        public readonly FrameworkElement Root;
        public readonly string Signature;

        public Card(Sample s, Theme t, State state, Config cfg, CardActions act, string hotkey)
        {
            this.t = t;
            this.state = state;
            this.cfg = cfg;
            this.act = act ?? new CardActions();
            this.hotkey = hotkey;
            tipStyle = MenuStyles.ToolTip(t);
            warnBrush = Theme.B(t.Warn);
            dangerBrush = Theme.B(t.Danger);
            Signature = SignatureOf(s, state, t);
            Root = Build(s);
            Update(s);
        }

        public static string SignatureOf(Sample s, State st, Theme t)
        {
            var sb = new StringBuilder();
            sb.Append(t.Dark ? 'd' : 'l').Append(st.Compact ? 'c' : 'f').Append(st.Columns).Append(st.Topmost ? 'p' : '-').Append(st.Theme);
            foreach (var id in State.SectionIds) sb.Append(st.Section(id) ? '1' : '0');
            sb.Append('|').Append(s.Gpu != null ? s.Gpu.Name : "-");
            sb.Append('|').Append(s.Net != null ? s.Net.Name + s.Net.LinkBps : "-");
            sb.Append('|');
            foreach (var d in s.Disks) sb.Append(d.Letter);
            sb.Append('|').Append(s.Procs != null ? s.Procs.Count : -1);
            return sb.ToString();
        }

        public void Update(Sample s)
        {
            foreach (var u in updaters)
            {
                try { u(s); }
                catch (Exception ex) { Log.Write("card: " + ex.Message); }
            }
        }

        double Gap
        {
            get { return state.Compact ? 9 : 11; }
        }

        FrameworkElement Build(Sample s)
        {
            // корень — столбик из двух плашек: шапка и разделы
            var root = new StackPanel();
            // почти прозрачный фон: щель между плашками ловит перетаскивание и правый щелчок
            root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            root.Margin = new Thickness(14, 10, 14, 18);   // запас под тени плашек
            root.Resources["RW.Hover"] = t.Hover;
            TextElement.SetForeground(root, t.Text);
            TextElement.SetFontFamily(root, Ui.Font);
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);

            root.Children.Add(Panel(Header(), new Thickness(14, 8, 8, 8)));

            var body = new StackPanel();
            var left = new List<FrameworkElement>();
            var right = new List<FrameworkElement>();
            if (state.Section("cpu")) left.Add(Cpu(s));
            if (state.Section("memory") && s.Mem != null) left.Add(Memory(s));
            if (state.Section("gpu") && s.Gpu != null) left.Add(Gpu(s));
            if (state.Section("disks") && s.Disks.Count > 0) right.Add(Disks(s));
            if (state.Section("network") && s.Net != null) right.Add(Network(s));
            if (state.Section("processes") && s.Procs != null) right.Add(Processes(s));

            if (state.Columns == 2 && left.Count > 0 && right.Count > 0)
            {
                var g = Ui.Columns(Ui.Px(ColumnWidth), Ui.Px(29), Ui.Px(ColumnWidth));
                g.Children.Add(Column(left));
                g.Children.Add(Ui.At(new Border { Width = 1, Background = t.Separator }, 1));
                g.Children.Add(Ui.At(Column(right), 2));
                body.Children.Add(g);
            }
            else
            {
                left.AddRange(right);
                body.Children.Add(Column(left));
            }
            if (left.Count == 0)
            {
                var empty = Ui.Small(t.Muted, new Thickness(0));
                empty.Text = "Все разделы скрыты: меню ⋯ → «Разделы»";
                body.Children.Add(empty);
            }
            var bodyPanel = Panel(body, new Thickness(16, 12, 16, 14));
            bodyPanel.Margin = new Thickness(0, state.Compact ? 6 : 8, 0, 0);
            root.Children.Add(bodyPanel);
            return root;
        }

        // Плашка: скруглённая подложка с рамкой и тенью, поверх неё содержимое.
        // Подложка неподвижна и кешируется в битмап: ежесекундные обновления цифр не заставляют заново размывать тень.
        // Прозрачным становится только фон: текст, графики и полосы остаются чёткими при любой настройке.
        FrameworkElement Panel(UIElement content, Thickness padding)
        {
            var plate = new Border();
            plate.CornerRadius = new CornerRadius(12);
            Color bg = t.CardBg.Color;
            plate.Background = Theme.B(Color.FromArgb((byte)Math.Round(255 * Math.Max(0, Math.Min(1, state.Opacity))), bg.R, bg.G, bg.B));
            plate.BorderBrush = t.CardBorder;
            plate.BorderThickness = new Thickness(1);
            plate.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = t.Dark ? 0.45 : 0.14, Color = Colors.Black };
            plate.CacheMode = new BitmapCache(Math.Max(1, state.Scale));

            var inner = new Border { BorderThickness = new Thickness(1), Padding = padding, Child = content };
            var layers = new Grid();
            layers.Children.Add(plate);
            layers.Children.Add(inner);
            return layers;
        }

        StackPanel Column(List<FrameworkElement> sections)
        {
            var sp = new StackPanel { Width = ColumnWidth };
            foreach (var e in sections)
            {
                if (sp.Children.Count > 0)
                    sp.Children.Add(new Border { Height = 1, Background = t.Separator, Margin = new Thickness(-2, Gap, -2, Gap) });
                sp.Children.Add(e);
            }
            return sp;
        }

        FrameworkElement Header()
        {
            var g = Ui.Columns(Ui.Star, Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = "Системные ресурсы", FontFamily = Ui.DisplayFont, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = t.Text });
            var sub = Ui.Small(t.Muted, new Thickness(0, 1, 0, 0));
            titles.Children.Add(sub);
            g.Children.Add(titles);
            updaters.Add(x => sub.Text = "время работы " + Fmt.Span(x.Uptime) + (string.IsNullOrEmpty(hotkey) ? "" : " · " + hotkey));

            string themeGlyph = state.Theme == "light" ? Glyph.ThemeLight : state.Theme == "dark" ? Glyph.ThemeDark : Glyph.ThemeAuto;
            string themeTip = state.Theme == "light" ? "Тема: светлая — нажмите для тёмной"
                : state.Theme == "dark" ? "Тема: тёмная — нажмите, чтобы как в Windows"
                : "Тема: как в Windows — нажмите для светлой";
            var theme = Ui.IconButton(themeGlyph, themeTip, t, tipStyle, 28, 13);
            theme.Click += (o, e) => { if (act.CycleTheme != null) act.CycleTheme(); };
            g.Children.Add(Ui.At(theme, 1));

            // в шрифте Pin и Pinned нарисованы одинаково, поэтому закреплённая скрепка — тот же значок, повёрнутый и цветной
            var pin = Ui.IconButton(Glyph.Pin, state.Topmost ? "Открепить (сейчас поверх всех окон)" : "Закрепить поверх всех окон", t, tipStyle, 28, 13);
            if (state.Topmost)
            {
                var pg = (TextBlock)pin.Content;
                pg.Foreground = Theme.B(t.Info);
                pg.RenderTransformOrigin = new Point(0.5, 0.5);
                pg.RenderTransform = new RotateTransform(-45);
            }
            pin.Click += (o, e) => { if (act.TogglePin != null) act.TogglePin(); };
            g.Children.Add(Ui.At(pin, 2));

            var more = Ui.IconButton(Glyph.More, "Меню", t, tipStyle, 28, 13);
            more.Click += (o, e) => { if (act.Menu != null) act.Menu(more); };
            g.Children.Add(Ui.At(more, 3));

            var min = Ui.IconButton(Glyph.Minimize, "Свернуть в трей", t, tipStyle, 28, 13);
            min.Click += (o, e) => { if (act.Minimize != null) act.Minimize(); };
            g.Children.Add(Ui.At(min, 4));
            return g;
        }

        // Строка раздела: значок, название, пометка (модель, объём) и крупное значение справа.
        Grid TitleRow(string glyph, Color accent, string title, string note, out TextBlock value)
        {
            var g = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star, Ui.Auto);
            g.Children.Add(Ui.Badge(glyph, accent));
            var name = new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = t.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
            g.Children.Add(Ui.At(name, 1));
            if (!string.IsNullOrEmpty(note))
            {
                var chip = Ui.Chip(note, t);
                chip.Margin = new Thickness(7, 1, 8, 0);
                g.Children.Add(Ui.At(chip, 2));
            }
            value = Ui.Number(15, t.Text, true);
            value.FontFamily = Ui.DisplayFont;
            g.Children.Add(Ui.At(value, 3));
            return g;
        }

        // Цвет крупного значения: обычный текст, а у порогов — предупреждение и тревога.
        Brush Level(double v, double warn, double danger)
        {
            return v >= danger ? dangerBrush : v >= warn ? warnBrush : t.Text;
        }
    }
}
