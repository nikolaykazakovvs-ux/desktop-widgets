using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ResourcesWidget
{
    // Содержимое окна «Сеть по дням». Список дней: пик за секунду, пик средней за минуту, доля канала, трафик, пинг.
    // Щелчок по дню — подробно: карточки итогов, графики скорости и пинга по минутам и события связи за день.
    // Шрифты не мельче 12,5 px: заголовок 17, значения 15–16, подписи и единицы 13, оси графиков 12,5.
    sealed class PeakJournal
    {
        // колонки: день, пик (1 с), пик (1 мин), канал, трафик, пинг, «›»
        const double DayW = 84, PairW = 170, ShareW = 74, TrafficW = 158, PingW = 70, ArrowW = 18, RowPad = 8;
        const double Width = RowPad + DayW + 2 * PairW + ShareW + TrafficW + PingW + ArrowW;
        const double ValueSize = 15, CardValueSize = 16, TextSize = 13, AxisSize = 12.5, TitleSize = 17;
        static readonly CultureInfo Ru = new CultureInfo("ru-RU");

        readonly Theme t;
        readonly Config cfg;
        readonly Style tipStyle;
        readonly NetJournal journal;
        readonly Brush down, up, warn, danger, head, unit, plate;
        readonly Border host = new Border();
        List<DayPeaks> days;
        string adapter;
        long link, linkMaxSeen;
        DateTime? shownDay;          // null — список дней
        ScrollViewer scroll;

        public readonly FrameworkElement Root;
        public Action Close;

        public PeakJournal(Theme t, Config cfg, Style tipStyle, NetJournal journal)
        {
            this.t = t;
            this.cfg = cfg;
            this.journal = journal;
            // подсказки этого окна — тоже не мельче 13 px
            var tip = new Style(typeof(ToolTip), tipStyle);
            tip.Setters.Add(new Setter(Control.FontSizeProperty, TextSize));
            this.tipStyle = tip;
            down = Theme.B(t.NetDown);
            up = Theme.B(t.NetUp);
            warn = Theme.B(t.Warn);
            danger = Theme.B(t.Danger);
            head = Theme.B(Theme.C(t.Dark ? "#FFA0A8B8" : "#FF586273"));   // заголовки колонок и карточек
            unit = Theme.B(Theme.C(t.Dark ? "#FFB0B8C4" : "#FF4D5766"));   // единицы, подписи, оси
            plate = Theme.B(Theme.Alpha(t.Track.Color, 0.55));               // фон плашек и карточек

            TextElement.SetFontFamily(host, Ui.Font);
            TextElement.SetForeground(host, t.Text);
            TextElement.SetFontSize(host, TextSize);
            TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
            host.Resources["RW.Hover"] = t.Hover;
            host.Width = Width;
            Root = new Border
            {
                Background = t.CardBg,
                BorderBrush = t.CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 12, 12, 14),
                Margin = new Thickness(12, 6, 12, 16),   // запас под тень
                Child = host,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = t.Dark ? 0.5 : 0.18, Color = Colors.Black },
            };
            Load();
            ShowList();
        }

        // Свежие данные журнала: окно может стоять открытым долго.
        void Load()
        {
            days = journal.Snapshot();
            journal.Current(out adapter, out link);
            linkMaxSeen = 0;
            foreach (var d in days)
            {
                if (adapter == null && d.Adapter != null) { adapter = d.Adapter; link = d.LinkMin; }
                if (d.Adapter == adapter) linkMaxSeen = Math.Max(linkMaxSeen, d.LinkMax);
            }
        }

        // Перечитать журнал и перерисовать текущий вид, сохранив прокрутку.
        public void Refresh()
        {
            double offset = scroll != null ? scroll.VerticalOffset : 0;
            Load();
            if (shownDay.HasValue) ShowDay(shownDay.Value);
            else ShowList();
            if (scroll != null && offset > 0) scroll.ScrollToVerticalOffset(offset);
        }

        double Channel
        {
            get { return cfg.InternetMbps > 0 ? cfg.InternetMbps * 1e6 : link; }   // бит/с
        }

        // ---------- список дней ----------

        public void ShowList()
        {
            shownDay = null;
            var root = new StackPanel();
            root.Children.Add(TopBar(null, "Сеть по дням", ListChips(), ListHelp()));

            var heads = Row();
            heads.Margin = new Thickness(RowPad, 14, 0, 5);
            heads.Children.Add(Caption("День", 0, false));
            heads.Children.Add(Caption("Пик (1 с)", 1, false));
            heads.Children.Add(Caption("Пик (1 мин)", 2, false));
            heads.Children.Add(Caption("Канал", 3, true));
            heads.Children.Add(Caption("Трафик", 4, true));
            heads.Children.Add(Caption("Пинг", 5, true));
            root.Children.Add(heads);
            root.Children.Add(new Border { Height = 1, Background = t.Separator, Margin = new Thickness(0, 0, 0, 2) });

            var list = new StackPanel();
            foreach (var d in days) list.Children.Add(DayRow(d));
            if (days.Count == 0) list.Children.Add(Body("Данных пока нет: журнал начинается с первого запуска этой версии виджета.", new Thickness(RowPad, 8, 0, 0)));
            scroll = new ScrollViewer { Content = list, MaxHeight = 360, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            root.Children.Add(scroll);
            root.Children.Add(Plate(Footer(), new Thickness(0, 10, 0, 0)));
            host.Child = root;
        }

        // Плашки у заголовка: адаптер со скоростью соединения и состояние связи за 30 дней.
        List<FrameworkElement> ListChips()
        {
            var chips = new List<FrameworkElement>();
            chips.Add(Chip((adapter ?? "Сеть") + (link > 0 ? " · " + Fmt.LinkSpeed(link) : "")));
            int problems = 0;
            foreach (var d in days) if (d.Day >= DateTime.Today.AddDays(-29)) problems += d.Problems;
            chips.Add(problems > 0
                ? DotChip(t.Danger, problems + Fmt.Nbsp + Fmt.Plural(problems, "перебой", "перебоя", "перебоев") + " за 30" + Fmt.Nbsp + "дн")
                : DotChip(t.Ok, "Без перебоев"));
            if (link > 0 && linkMaxSeen > link)
            {
                var c = DotChip(t.Warn, "Соединение ниже обычного");
                Ui.Tip(c, "Сейчас " + Fmt.LinkSpeed(link) + ", а бывало " + Fmt.LinkSpeed(linkMaxSeen) + ": проверьте кабель, разъём и порт роутера", tipStyle);
                chips.Add(c);
            }
            return chips;
        }

        string ListHelp()
        {
            string channel = cfg.InternetMbps > 0
                ? "от тарифа " + Fmt.LinkSpeed((long)Channel) + " (internetMbps в config.json)"
                : "от скорости соединения адаптера" + (link > 0 ? " (" + Fmt.LinkSpeed(link) + ")" : "") + ". Чтобы считать от тарифа, укажите его в config.json: \"internetMbps\": 1000";
            return "Пик (1 с) — самый быстрый момент за день: загрузка файла, обновление.\n"
                + "Пик (1 мин) — самая высокая средняя скорость за минуту: устойчивая нагрузка.\n"
                + "Канал — какую долю канала занял пик за секунду; считается " + channel + ".\n"
                + "Трафик — сколько принято и отправлено за день.\n"
                + "Пинг — средний пинг до интернета (" + (cfg.PingHost.Length > 0 ? cfg.PingHost : "выключен") + "); красным — были обрывы, жёлтым — высокий пинг или падение скорости соединения.\n"
                + "Считается только этот компьютер и только пока работает виджет: трафик других устройств сюда не попадает.\n"
                + "Щелчок по дню — карточки итогов, графики по минутам и события связи.\n"
                + "Окно перетаскивается за шапку или фон; место запоминается. Журнал — папка network-log рядом с виджетом.";
        }

        Grid Row()
        {
            return Ui.Columns(Ui.Px(DayW), Ui.Px(PairW), Ui.Px(PairW), Ui.Px(ShareW), Ui.Px(TrafficW), Ui.Px(PingW), Ui.Px(ArrowW));
        }

        TextBlock Caption(string text, int column, bool right)
        {
            var tb = new TextBlock { Text = text, FontSize = TextSize, FontWeight = FontWeights.Medium, Foreground = head, VerticalAlignment = VerticalAlignment.Center };
            if (right) tb.HorizontalAlignment = HorizontalAlignment.Right;
            return Ui.At(tb, column);
        }

        TextBlock Body(string text, Thickness margin)
        {
            return new TextBlock { Text = text, FontSize = TextSize, Foreground = unit, TextWrapping = TextWrapping.Wrap, Margin = margin };
        }

        FrameworkElement DayRow(DayPeaks d)
        {
            var g = Row();
            bool today = d.Day == DateTime.Today;
            var day = new TextBlock
            {
                Text = today ? "сегодня" : d.Day.ToString("dd.MM ddd", Ru),
                FontSize = 14, FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal, Foreground = today ? t.Text : t.Text2,
                VerticalAlignment = VerticalAlignment.Center,
            };
            g.Children.Add(day);
            g.Children.Add(Ui.At(Pair(d.Down, d.Up), 1));
            g.Children.Add(Ui.At(Pair(d.MinuteDown, d.MinuteUp), 2));

            // канал: процент и полоска, как у дисков
            double share = Share(Math.Max(d.Down, d.Up));
            var channel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var pct = Line();
            pct.HorizontalAlignment = HorizontalAlignment.Right;
            if (Channel > 0)
            {
                pct.Inlines.Add(Value(Math.Round(share).ToString("0", CultureInfo.InvariantCulture), share >= 90 ? danger : share >= 70 ? warn : t.Text));
                pct.Inlines.Add(Unit("%"));
            }
            else pct.Inlines.Add(Unit("—"));
            channel.Children.Add(pct);
            if (Channel > 0)
            {
                var bar = new Meter(t) { Height = 4, Width = 52, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 3, 0, 0) };
                bar.Set(share, t.Level(share, 70, 90, t.NetDown));
                channel.Children.Add(bar);
            }
            g.Children.Add(Ui.At(channel, 3));

            var traffic = Traffic(d.BytesDown, d.BytesUp);
            traffic.HorizontalAlignment = HorizontalAlignment.Right;
            g.Children.Add(Ui.At(traffic, 4));

            // пинг: только значение; красным — были обрывы или отключения, жёлтым — высокий пинг или падение скорости соединения
            var ping = Line();
            ping.HorizontalAlignment = HorizontalAlignment.Right;
            if (d.PingSent > d.PingLost)
            {
                ping.Inlines.Add(Value(Fmt.Num(d.PingAvg, 0), PingBrush(d)));
                ping.Inlines.Add(Unit("мс"));
            }
            else ping.Inlines.Add(d.PingSent > 0 ? Value("нет", danger) : Unit("—"));
            g.Children.Add(Ui.At(ping, 5));

            var chevron = new TextBlock { Text = "›", FontSize = 17, Foreground = unit, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(Ui.At(chevron, 6));

            return Clickable(g, () => ShowDay(d.Day), RowTip(d));
        }

        Brush PingBrush(DayPeaks d)
        {
            return d.Outages + d.Disconnects > 0 ? danger : d.Lags > 0 || (d.LinkMin > 0 && d.LinkMin < d.LinkMax) ? warn : t.Text;
        }

        // Строка значений: числа одинаковой ширины.
        static TextBlock Line()
        {
            var tb = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            return tb;
        }

        Run Value(string text, Brush brush, double size = ValueSize)
        {
            return new Run(text) { FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = brush };
        }

        Run Unit(string text)
        {
            return new Run(Fmt.Nbsp + text) { FontSize = TextSize, FontWeight = FontWeights.Normal, Foreground = unit };
        }

        Run Plain(string text)
        {
            return new Run(text) { FontSize = TextSize, FontWeight = FontWeights.Normal, Foreground = unit };
        }

        static Run Arrow(string glyph, Brush brush)
        {
            return new Run(glyph) { FontFamily = Ui.IconFont, FontSize = 12.5, Foreground = brush, BaselineAlignment = BaselineAlignment.Center };
        }

        // Строка-кнопка: подсветка под мышью и щелчок. Нажатие помечается обработанным, чтобы окно не начинало перетаскивание.
        FrameworkElement Clickable(FrameworkElement content, Action click, string tip)
        {
            var b = new Border { Child = content, Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Padding = new Thickness(RowPad, 5, 0, 5), Cursor = Cursors.Hand };
            bool pressed = false;   // щелчок — нажали и отпустили на этой строке, а не отпустили над ней после перетаскивания
            b.MouseEnter += (s, e) => b.Background = t.Hover;
            b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; pressed = false; };
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; pressed = true; };
            b.MouseLeftButtonUp += (s, e) =>
            {
                if (!pressed) return;
                pressed = false;
                e.Handled = true;
                click();
            };
            if (tip != null) Ui.Tip(b, tip, tipStyle);
            return b;
        }

        double Share(double bytesPerSecond)
        {
            return Channel > 0 ? 100.0 * bytesPerSecond * 8 / Channel : 0;
        }

        // «↓ 34,9  ↑ 2,5 Мбит/с» — цветные стрелки, крупные числа, общая единица.
        TextBlock Pair(double a, double b)
        {
            var p = Fmt.NetPair(a, b, cfg.NetworkBits);
            var tb = Line();
            tb.TextWrapping = TextWrapping.NoWrap;
            tb.Inlines.Add(Arrow(Glyph.Down, down));
            tb.Inlines.Add(Value(Fmt.Nbsp + p[0], t.Text));
            tb.Inlines.Add(Plain("  "));
            tb.Inlines.Add(Arrow(Glyph.Up, up));
            tb.Inlines.Add(Value(Fmt.Nbsp + p[1], t.Text));
            tb.Inlines.Add(Unit(p[2]));
            return tb;
        }

        // «↓ 11 МБ  ↑ 4 МБ»
        TextBlock Traffic(double bytesDown, double bytesUp)
        {
            var tb = Line();
            tb.TextWrapping = TextWrapping.NoWrap;
            string[] d = SizeParts(bytesDown), u = SizeParts(bytesUp);
            tb.Inlines.Add(Arrow(Glyph.Down, down));
            tb.Inlines.Add(Value(Fmt.Nbsp + d[0], t.Text));
            tb.Inlines.Add(Unit(d[1]));
            tb.Inlines.Add(Plain("  "));
            tb.Inlines.Add(Arrow(Glyph.Up, up));
            tb.Inlines.Add(Value(Fmt.Nbsp + u[0], t.Text));
            tb.Inlines.Add(Unit(u[1]));
            return tb;
        }

        // "11 МБ" -> { "11", "МБ" }
        static string[] SizeParts(double bytes)
        {
            string s = Fmt.Size(bytes);
            int i = s.LastIndexOf(Fmt.Nbsp, StringComparison.Ordinal);
            return i > 0 ? new[] { s.Substring(0, i), s.Substring(i + 1) } : new[] { s, "" };
        }

        // Плашка с лёгким фоном: итоги, события, карточки дня.
        Border Plate(UIElement child, Thickness margin)
        {
            return new Border { Background = plate, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 9), Margin = margin, Child = child };
        }

        // «Макс. за 30 дн: ↓ 34,9 Мбит/с (35 %) 18.10 в 10:12 · 1 мин: ↓ 1,9 Мбит/с»
        FrameworkElement Footer()
        {
            var sp = new StackPanel();
            sp.Children.Add(MaxLine("Макс. за 30" + Fmt.Nbsp + "дн", DateTime.Today.AddDays(-29)));
            if (days.Count > 0 && days[days.Count - 1].Day < DateTime.Today.AddDays(-29))
                sp.Children.Add(MaxLine("Макс. за всё время", DateTime.MinValue));
            return sp;
        }

        TextBlock MaxLine(string label, DateTime from)
        {
            DayPeaks best = null, bestMinute = null;
            foreach (var d in days)
            {
                if (d.Day < from) continue;
                if (best == null || Math.Max(d.Down, d.Up) > Math.Max(best.Down, best.Up)) best = d;
                if (bestMinute == null || Math.Max(d.MinuteDown, d.MinuteUp) > Math.Max(bestMinute.MinuteDown, bestMinute.MinuteUp)) bestMinute = d;
            }
            var tb = Line();
            tb.Margin = new Thickness(0, 1, 0, 1);
            tb.Inlines.Add(Plain(label + ": "));
            if (best == null)
            {
                tb.Inlines.Add(Plain("данных пока нет"));
                return tb;
            }
            bool downBest = best.Down >= best.Up;
            double peak = downBest ? best.Down : best.Up;
            tb.Inlines.Add(new Run((downBest ? "↓ " : "↑ ") + Rate(peak)) { FontSize = TextSize, FontWeight = FontWeights.SemiBold, Foreground = t.Text });
            tb.Inlines.Add(Plain((Channel > 0 ? " (" + Fmt.Percent(Share(peak)) + ")" : "") + " " + best.Day.ToString("dd.MM", Ru) + At(peak, downBest ? best.DownAt : best.UpAt)));
            bool downMinute = bestMinute.MinuteDown >= bestMinute.MinuteUp;
            double minute = downMinute ? bestMinute.MinuteDown : bestMinute.MinuteUp;
            tb.Inlines.Add(Plain(" · 1" + Fmt.Nbsp + "мин: "));
            tb.Inlines.Add(new Run((downMinute ? "↓ " : "↑ ") + Rate(minute)) { FontSize = TextSize, FontWeight = FontWeights.SemiBold, Foreground = t.Text });
            return tb;
        }

        string RowTip(DayPeaks d)
        {
            var lines = DayLines(d);
            lines.Add("щелчок — карточки итогов, графики по минутам и события");
            return string.Join("\n", lines.ToArray());
        }

        // Итоги дня строками — для подсказки строки списка.
        List<string> DayLines(DayPeaks d)
        {
            var l = new List<string>();
            l.Add(d.Day.ToString("dddd, d MMMM yyyy", Ru));
            l.Add("пик за секунду: ↓ " + Rate(d.Down) + At(d.Down, d.DownAt) + " · ↑ " + Rate(d.Up) + At(d.Up, d.UpAt));
            l.Add("пик за минуту: ↓ " + Rate(d.MinuteDown) + At(d.MinuteDown, d.MinuteDownAt) + " · ↑ " + Rate(d.MinuteUp) + At(d.MinuteUp, d.MinuteUpAt));
            l.Add("трафик: принято " + Fmt.Size(d.BytesDown) + " · отправлено " + Fmt.Size(d.BytesUp));
            if (d.LinkMax > 0) l.Add("соединение адаптера: " + LinkText(d));
            if (d.PingSent > 0) l.Add("пинг до интернета: " + PingText(d));
            l.Add("перебои: " + ProblemsText(d));
            return l;
        }

        static string LinkText(DayPeaks d)
        {
            return (d.LinkMin == d.LinkMax ? Fmt.LinkSpeed(d.LinkMax) : "от " + Fmt.LinkSpeed(d.LinkMin) + " до " + Fmt.LinkSpeed(d.LinkMax))
                + (d.LinkChanges > 0 ? ", менялось " + d.LinkChanges + Fmt.Nbsp + Fmt.Plural(d.LinkChanges, "раз", "раза", "раз") : "");
        }

        static string PingText(DayPeaks d)
        {
            return (d.PingSent > d.PingLost ? "средний " + Fmt.Num(d.PingAvg, 0) + " мс, наибольший " + Fmt.Num(d.PingMax, 0) + " мс, " : "ответов не было, ")
                + "потери " + Loss(d.PingLost, d.PingSent);
        }

        static string ProblemsText(DayPeaks d)
        {
            return (d.Outages > 0 ? d.Outages + " " + Fmt.Plural(d.Outages, "обрыв", "обрыва", "обрывов") + " (всего " + Fmt.Duration(TimeSpan.FromSeconds(d.OutageSeconds)) + ")" : "обрывов нет")
                + (d.Lags > 0 ? ", высокий пинг " + d.Lags + Fmt.Nbsp + Fmt.Plural(d.Lags, "раз", "раза", "раз") : "")
                + (d.Disconnects > 0 ? ", сеть отключалась " + d.Disconnects + Fmt.Nbsp + Fmt.Plural(d.Disconnects, "раз", "раза", "раз") : "");
        }

        static string Loss(int lost, int sent)
        {
            double p = sent > 0 ? 100.0 * lost / sent : 0;
            return (lost == 0 ? "0" : p < 0.1 ? "<0,1" : Fmt.Num(p, p < 10 ? 1 : 0)) + Fmt.Nbsp + "%";
        }

        // ---------- один день ----------

        public void ShowDay(DateTime dayDate)
        {
            shownDay = dayDate;
            DayPeaks d = null;
            foreach (var x in days) if (x.Day == dayDate) d = x;
            if (d == null) d = new DayPeaks { Day = dayDate };
            var detail = journal.ReadDay(dayDate);

            var root = new StackPanel();
            root.Children.Add(TopBar(() => ShowList(), d.Day.ToString(d.Day.Year == DateTime.Today.Year ? "dddd, d MMMM" : "dddd, d MMMM yyyy", Ru), DayChips(d), DayHelp()));

            // четыре карточки итогов, 2 × 2
            var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCard(grid, 0, 0, "Пик за 1 с", PeakLine(d.Down, d.DownAt, d.Up, d.UpAt));
            AddCard(grid, 0, 1, "Пик за 1 мин", PeakLine(d.MinuteDown, d.MinuteDownAt, d.MinuteUp, d.MinuteUpAt));
            AddCard(grid, 1, 0, "Трафик и канал", TrafficLine(d));
            AddCard(grid, 1, 1, "Пинг и потери", PingLine(d));
            root.Children.Add(grid);

            // скорость по минутам: заливка — средняя получения, линия — средняя отдачи, бледная — пик за секунду
            double top = 0;
            foreach (var v in new[] { detail.MaxDown, detail.MaxUp })
                foreach (var x in v) if (!double.IsNaN(x) && x > top) top = x;
            top = Card.NiceScale(top, cfg.NetworkBits);
            var speed = new DayChart(t, t.NetDown, t.NetUp, t.NetDown) { Height = 84, Area = detail.AvgDown, Line = detail.AvgUp, Faint = detail.MaxDown, Max = top };
            root.Children.Add(ChartBlock(Legend("Скорость по минутам", "●", t.NetDown, "Получение", "─", t.NetUp, "Отдача",
                "шкала " + (cfg.NetworkBits ? Fmt.LinkSpeed((long)Math.Round(top * 8)) : Fmt.Rate(top))), speed));

            // пинг по минутам: заливка — до интернета, бледная линия — до роутера, красное — потери
            double pmax = 0;
            foreach (var x in detail.PingMax) if (!double.IsNaN(x) && x > pmax) pmax = x;
            double ptop = 20;
            foreach (var k in new double[] { 20, 50, 100, 200, 500, 1000, 2000 }) { ptop = k; if (k >= pmax) break; }
            var marks = new int[DayDetail.Minutes];
            for (int i = 0; i < marks.Length; i++) marks[i] = detail.Lost[i] + detail.GwLost[i];
            var ping = new DayChart(t, t.Info, null, t.Text2.Color) { Height = 52, Area = detail.Ping, Faint = detail.GwPing, Marks = marks, Max = ptop };
            root.Children.Add(ChartBlock(cfg.PingHost.Length > 0
                ? Legend("Пинг по минутам, мс", "●", t.Info, "Интернет (" + cfg.PingHost + ")", "─", t.Text2.Color, "Роутер", "шкала " + ptop + Fmt.Nbsp + "мс")
                : Legend("Пинг выключен (pingHost в config.json)", null, t.Info, null, null, t.Info, null, null), ping));

            root.Children.Add(Plate(Events(dayDate), new Thickness(0, 12, 0, 0)));
            host.Child = root;
            scroll = null;
        }

        List<FrameworkElement> DayChips(DayPeaks d)
        {
            var chips = new List<FrameworkElement>();
            if (d.LinkMax > 0)
            {
                var c = d.LinkMin < d.LinkMax ? DotChip(t.Warn, (d.Adapter ?? "Сеть") + " · " + Fmt.LinkSpeed(d.LinkMin) + "…" + Fmt.LinkSpeed(d.LinkMax))
                    : Chip((d.Adapter ?? "Сеть") + " · " + Fmt.LinkSpeed(d.LinkMax));
                Ui.Tip(c, "Соединение адаптера: " + LinkText(d), tipStyle);
                chips.Add(c);
            }
            int outages = d.Outages + d.Disconnects;
            chips.Add(outages > 0
                ? DotChip(t.Danger, outages + Fmt.Nbsp + Fmt.Plural(outages, "обрыв", "обрыва", "обрывов"))
                : d.Lags > 0 ? DotChip(t.Warn, "Обрывов нет, высокий пинг " + d.Lags + Fmt.Nbsp + Fmt.Plural(d.Lags, "раз", "раза", "раз"))
                : DotChip(t.Ok, "Обрывов нет"));
            var counted = Chip("Учтено: " + Fmt.Span(TimeSpan.FromSeconds(d.Seconds)));
            Ui.Tip(counted, "Сколько времени в этот день работал виджет: только за это время есть данные", tipStyle);
            chips.Add(counted);
            return chips;
        }

        string DayHelp()
        {
            return "Пик за 1 с — самый быстрый момент; в скобках — когда он был.\n"
                + "Пик за 1 мин — самая высокая средняя скорость за минуту: устойчивая нагрузка.\n"
                + "Трафик и канал — сколько принято и отправлено и какую долю канала занял пик за секунду"
                + (cfg.InternetMbps > 0 ? " (канал — тариф из config.json)." : " (канал — скорость соединения; тариф: \"internetMbps\" в config.json).") + "\n"
                + "Пинг и потери — средний и наибольший пинг до интернета и потери пакетов до интернета и до роутера.\n"
                + "График скорости: заливка — средняя скорость получения за минуту, линия — отдачи, бледная линия — пик за секунду в этой минуте.\n"
                + "График пинга: заливка — пинг до интернета, линия — до роутера, красные отметки — потери пакетов.\n"
                + "Пропуски на графиках — время, когда компьютер был выключен или виджет не работал.\n"
                + "События: смена скорости соединения, отключение сети, обрыв (3 пинга подряд без ответа; если роутер отвечал — сбой у провайдера), "
                + "высокий пинг (3 ответа подряд дольше " + cfg.PingWarnMs + " мс).";
        }

        void AddCard(Grid grid, int row, int column, string title, TextBlock value)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = TextSize, FontWeight = FontWeights.Medium, Foreground = head, Margin = new Thickness(0, 0, 0, 3) });
            sp.Children.Add(value);
            var card = Plate(sp, new Thickness(column == 0 ? 0 : 5, row == 0 ? 0 : 8, column == 0 ? 5 : 0, 0));
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            grid.Children.Add(card);
        }

        // «↓ 34,9 (10:12) · ↑ 32,8 Мбит/с (10:18)»
        TextBlock PeakLine(double a, DateTime aAt, double b, DateTime bAt)
        {
            var p = Fmt.NetPair(a, b, cfg.NetworkBits);
            var tb = Line();
            tb.Inlines.Add(Arrow(Glyph.Down, down));
            tb.Inlines.Add(Value(Fmt.Nbsp + p[0], t.Text, CardValueSize));
            if (a > 0) tb.Inlines.Add(Plain(" (" + aAt.ToString("HH:mm", CultureInfo.InvariantCulture) + ")"));
            tb.Inlines.Add(Plain("  ·  "));
            tb.Inlines.Add(Arrow(Glyph.Up, up));
            tb.Inlines.Add(Value(Fmt.Nbsp + p[1], t.Text, CardValueSize));
            tb.Inlines.Add(Unit(p[2]));
            if (b > 0) tb.Inlines.Add(Plain(" (" + bAt.ToString("HH:mm", CultureInfo.InvariantCulture) + ")"));
            return tb;
        }

        // «↓ 70 МБ · ↑ 45 МБ (пик канала 35 %)»
        TextBlock TrafficLine(DayPeaks d)
        {
            string[] dn = SizeParts(d.BytesDown), upp = SizeParts(d.BytesUp);
            var tb = Line();
            tb.Inlines.Add(Arrow(Glyph.Down, down));
            tb.Inlines.Add(Value(Fmt.Nbsp + dn[0], t.Text, CardValueSize));
            tb.Inlines.Add(Unit(dn[1]));
            tb.Inlines.Add(Plain("  ·  "));
            tb.Inlines.Add(Arrow(Glyph.Up, up));
            tb.Inlines.Add(Value(Fmt.Nbsp + upp[0], t.Text, CardValueSize));
            tb.Inlines.Add(Unit(upp[1]));
            if (Channel > 0)
            {
                double share = Share(Math.Max(d.Down, d.Up));
                tb.Inlines.Add(Plain(" (пик канала "));
                tb.Inlines.Add(new Run(Fmt.Percent(share)) { FontSize = TextSize, FontWeight = FontWeights.SemiBold, Foreground = share >= 90 ? danger : share >= 70 ? warn : t.Text });
                tb.Inlines.Add(Plain(")"));
            }
            return tb;
        }

        // «Интернет 4 мс (макс 30 мс, 0 %) · Роутер 0 % потерь»
        TextBlock PingLine(DayPeaks d)
        {
            var tb = Line();
            if (d.PingSent == 0)
            {
                tb.Inlines.Add(Plain(cfg.PingHost.Length > 0 ? "данных нет" : "пинг выключен (pingHost в config.json)"));
                return tb;
            }
            tb.Inlines.Add(Plain("Интернет "));
            if (d.PingSent > d.PingLost)
            {
                tb.Inlines.Add(Value(Fmt.Num(d.PingAvg, 0), PingBrush(d), CardValueSize));
                tb.Inlines.Add(Unit("мс"));
                tb.Inlines.Add(Plain(" (макс " + Fmt.Num(d.PingMax, 0) + Fmt.Nbsp + "мс, " + Loss(d.PingLost, d.PingSent) + ")"));
            }
            else tb.Inlines.Add(Value("нет ответов", danger, CardValueSize));
            if (d.GwSent > 0)
            {
                tb.Inlines.Add(Plain("  ·  Роутер "));
                tb.Inlines.Add(new Run(Loss(d.GwLost, d.GwSent)) { FontSize = TextSize, FontWeight = FontWeights.SemiBold, Foreground = d.GwLost > 0 ? warn : t.Text });
                tb.Inlines.Add(Plain(" потерь"));
            }
            return tb;
        }

        // «Скорость по минутам   ● Получение   ─ Отдача   (шкала 50 Мбит/с)»
        TextBlock Legend(string title, string markA, Color colorA, string textA, string markB, Color colorB, string textB, string scale)
        {
            var tb = Line();
            tb.Inlines.Add(new Run(title) { FontSize = TextSize, FontWeight = FontWeights.Medium, Foreground = t.Text });
            if (markA != null)
            {
                tb.Inlines.Add(Plain("    "));
                tb.Inlines.Add(new Run(markA) { FontSize = TextSize, Foreground = Theme.B(colorA) });
                tb.Inlines.Add(Plain(" " + textA));
            }
            if (markB != null)
            {
                tb.Inlines.Add(Plain("    "));
                tb.Inlines.Add(new Run(markB) { FontSize = TextSize, FontWeight = FontWeights.Bold, Foreground = Theme.B(colorB) });
                tb.Inlines.Add(Plain(" " + textB));
            }
            if (scale != null) tb.Inlines.Add(Plain("    (" + scale + ")"));
            return tb;
        }

        FrameworkElement ChartBlock(TextBlock legend, DayChart chart)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            legend.Margin = new Thickness(0, 0, 0, 5);
            sp.Children.Add(legend);
            sp.Children.Add(chart);
            var hours = Ui.Columns(Ui.Star, Ui.Star, Ui.Star, Ui.Star);
            hours.Margin = new Thickness(0, 2, 0, 0);
            string[] labels = { "0:00", "6:00", "12:00", "18:00" };
            for (int i = 0; i < 4; i++)
                hours.Children.Add(Ui.At(new TextBlock { Text = labels[i], FontSize = AxisSize, Foreground = unit }, i));
            hours.Children.Add(Ui.At(new TextBlock { Text = "24:00", FontSize = AxisSize, Foreground = unit, HorizontalAlignment = HorizontalAlignment.Right }, 3));
            sp.Children.Add(hours);
            return sp;
        }

        // События дня: плашка как итоги на первом экране.
        FrameworkElement Events(DateTime dayDate)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = "События", FontSize = TextSize, FontWeight = FontWeights.Medium, Foreground = head, Margin = new Thickness(0, 0, 0, 3) });
            var events = journal.Events(dayDate);
            if (events.Count == 0)
            {
                sp.Children.Add(Body("Событий нет: соединение не менялось, обрывов и долгого высокого пинга не было.", new Thickness(0)));
                return sp;
            }
            var list = new StackPanel();
            foreach (var e in events)
            {
                var g = Ui.Columns(Ui.Px(14), Ui.Px(68), Ui.Star);
                g.Margin = new Thickness(0, 2, 0, 1);
                g.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = e.Bad ? danger : Theme.B(t.Info), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) });
                var time = Line();
                time.VerticalAlignment = VerticalAlignment.Top;
                time.Inlines.Add(Plain(e.Start.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
                g.Children.Add(Ui.At(time, 1));
                g.Children.Add(Ui.At(new TextBlock { Text = e.Text, FontSize = TextSize, Foreground = t.Text, TextWrapping = TextWrapping.Wrap }, 2));
                list.Children.Add(g);
            }
            sp.Children.Add(new ScrollViewer { Content = list, MaxHeight = 170, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            return sp;
        }

        // ---------- шапка ----------

        Border Chip(string text)
        {
            return new Border
            {
                Background = t.ChipBg, CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = TextSize, FontWeight = FontWeights.Medium, Foreground = t.ChipText },
            };
        }

        // Плашка с цветной точкой: «● Без перебоев».
        Border DotChip(Color dot, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Theme.B(dot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0) });
            sp.Children.Add(new TextBlock { Text = text, FontSize = TextSize, FontWeight = FontWeights.Medium, Foreground = t.ChipText, VerticalAlignment = VerticalAlignment.Center });
            return new Border { Background = t.ChipBg, CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2, 7, 3), VerticalAlignment = VerticalAlignment.Center, Child = sp };
        }

        // «‹ все дни» (в виде дня), заголовок 17 px, плашки, справка «?» и закрытие. За шапку окно перетаскивается.
        FrameworkElement TopBar(Action back, string title, List<FrameworkElement> chips, string help)
        {
            var g = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star, Ui.Auto, Ui.Auto);
            g.Background = Brushes.Transparent;
            if (back != null)
            {
                var b = Ui.ChipButton("‹ все дни");
                ((TextBlock)b.Content).FontSize = TextSize;
                ((TextBlock)b.Content).FontWeight = FontWeights.Medium;
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(-6, 0, 6, 0);
                b.Foreground = t.Text2;
                b.Click += (s, e) => back();
                g.Children.Add(b);
            }
            var tb = new TextBlock { Text = title, FontSize = TitleSize, FontWeight = FontWeights.SemiBold, FontFamily = Ui.DisplayFont, Foreground = t.Text, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(Ui.At(tb, 1));
            if (chips != null)
            {
                var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 4, 0) };
                foreach (var c in chips)
                {
                    c.Margin = new Thickness(0, 2, 5, 2);
                    wrap.Children.Add(c);
                }
                g.Children.Add(Ui.At(wrap, 2));
            }
            var helpButton = Ui.IconButton("", help, t, tipStyle, 30, 14);
            System.Windows.Automation.AutomationProperties.SetName(helpButton, "Справка");
            ToolTipService.SetInitialShowDelay(helpButton, 150);
            g.Children.Add(Ui.At(helpButton, 3));
            var close = Ui.IconButton("", "Закрыть (Esc)", t, tipStyle, 30, 13);
            System.Windows.Automation.AutomationProperties.SetName(close, "Закрыть");
            close.Click += (s, e) => { if (Close != null) Close(); };
            g.Children.Add(Ui.At(close, 4));
            return g;
        }

        string Rate(double bytesPerSecond)
        {
            return Fmt.NetRate(bytesPerSecond, cfg.NetworkBits);
        }

        static string At(double value, DateTime at)
        {
            return value > 0 ? " в " + at.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
        }
    }
}
