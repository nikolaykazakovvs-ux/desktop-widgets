using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace LimitsWidget
{
    sealed class CardActions
    {
        public Action Refresh;
        public Action<FrameworkElement> Menu;
        public Action<string> OpenUrl;
        public Action<string> Run;   // действие из карточки по его id (например, вход в Claude Code)
        public Action TogglePin;     // скрепка: закрепить / открепить поверх всех окон
        public Action Minimize;      // свернуть в трей
        public Action CycleTheme;    // кнопка темы в шапке; null — кнопки нет
        public string ThemeMode = "auto";   // "auto" | "light" | "dark"
        public bool Pinned;
        public Action<string, Border, MouseButtonEventArgs> BeginDrag;   // ручка карточки нажата: id, карточка; null — ручек нет (headless)
        public Action<string, int> MoveCard;                             // id, −1 выше / +1 ниже (меню ручки)
    }

    // Строит визуальное дерево карточки из снимков; перестраивается целиком при каждом обновлении.
    static class CardView
    {
        public static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily DisplayFont = new FontFamily("Segoe UI Variable Display, Segoe UI");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        static readonly FontFamily SymbolFont = new FontFamily("Segoe UI Symbol, Segoe UI");
        static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas");
        public const double CardWidth = 348;
        static Style iconButtonStyle, pillButtonStyle;

        public static FrameworkElement Build(IList<ServiceSnapshot> snaps, Theme t, DateTime now, DateTime? updatedAt, bool busy, CardActions act, bool compact, double bgOpacity)
        {
            // корень — столбик отдельных карточек: шапка и по карточке на каждую нейросеть
            var root = new StackPanel();
            root.Width = CardWidth;
            // почти прозрачный фон: щели между карточками ловят перетаскивание и правый клик
            root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            root.Margin = new Thickness(14, 10, 14, 18);   // запас под тени карточек
            root.Resources["LW.Hover"] = t.Hover;
            TextElement.SetForeground(root, t.Text);
            TextElement.SetFontFamily(root, UiFont);
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);

            var head = Panel(t, bgOpacity, new Thickness(14, 8, 8, 8));
            head.Child = Header(t, updatedAt, busy, act);
            root.Children.Add(head);

            double gap = compact ? 6 : 8;
            if (snaps.Count == 0)
            {
                var wait = Panel(t, bgOpacity, new Thickness(14, 10, 14, 11));
                wait.Margin = new Thickness(0, gap, 0, 0);
                wait.Child = Small("опрашиваю CLI…", t.Muted, new Thickness(0));
                root.Children.Add(wait);
            }
            for (int i = 0; i < snaps.Count; i++)
            {
                double padLeft = compact ? 12 : 14;
                var card = Panel(t, bgOpacity, new Thickness(padLeft, compact ? 8 : 10, padLeft, compact ? 9 : 11));
                card.Margin = new Thickness(0, gap, 0, 0);
                card.Tag = snaps[i].Id;   // по Tag CardDrag отличает карточки нейросетей от шапки
                var content = Service(snaps[i], t, now, act, compact);
                if (act.BeginDrag != null)
                {
                    var host = new Grid();
                    host.Children.Add(content);
                    host.Children.Add(Grip(snaps[i], card, i, snaps.Count, padLeft, t, act));
                    card.Child = host;
                }
                else card.Child = content;
                root.Children.Add(card);
            }
            return root;
        }

        // Ручка перетаскивания: лежит в левом поле карточки (раскладку не сдвигает), видна при наведении на карточку.
        static FrameworkElement Grip(ServiceSnapshot s, Border card, int index, int count, double padLeft, Theme t, CardActions act)
        {
            string id = s.Id;
            var grip = new Border
            {
                Background = Brushes.Transparent,   // ловит мышь
                Width = padLeft,
                Height = 22,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-padLeft, 0, 0, 0),
                Opacity = 0,
                Cursor = Cursors.SizeNS,
                ToolTip = "Перетащите, чтобы изменить порядок карточек"
            };
            System.Windows.Automation.AutomationProperties.SetName(grip, "Порядок: " + (s.Title ?? s.Id));
            // шесть точек: 2 колонки × 3 строки, шаг 4.6
            var dots = new Canvas { Width = 7.2, Height = 11.8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 2; c++)
                {
                    var dot = new Ellipse { Width = 2.6, Height = 2.6, Fill = t.Muted };
                    Canvas.SetLeft(dot, c * 4.6);
                    Canvas.SetTop(dot, r * 4.6);
                    dots.Children.Add(dot);
                }
            grip.Child = dots;

            card.MouseEnter += (o, e) => { if (!grip.IsMouseOver) grip.Opacity = 0.55; };
            card.MouseLeave += (o, e) => grip.Opacity = 0;
            grip.MouseEnter += (o, e) => grip.Opacity = 1;
            grip.MouseLeave += (o, e) => grip.Opacity = card.IsMouseOver ? 0.55 : 0;
            grip.MouseLeftButtonDown += (o, e) =>
            {
                e.Handled = true;   // окно не должно начать DragMove
                act.BeginDrag(id, card, e);
            };
            grip.MouseRightButtonUp += (o, e) =>
            {
                e.Handled = true;   // общее меню окна не открываем
                var m = new ContextMenu();
                m.Style = MenuStyles.Menu(t);
                m.FontFamily = UiFont;
                m.FontSize = 12.5;
                var item = MenuStyles.Item(t);
                var up = new MenuItem { Header = MenuLabel("Переместить выше"), Style = item, IsEnabled = index > 0 };
                up.Click += (a, b) => { if (act.MoveCard != null) act.MoveCard(id, -1); };
                var down = new MenuItem { Header = MenuLabel("Переместить ниже"), Style = item, IsEnabled = index < count - 1 };
                down.Click += (a, b) => { if (act.MoveCard != null) act.MoveCard(id, 1); };
                m.Items.Add(up);
                m.Items.Add(down);
                m.PlacementTarget = grip;
                m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                m.IsOpen = true;
            };
            return grip;
        }

        // Подпись пункта меню с отступом под галочку — как у пунктов главного меню.
        static FrameworkElement MenuLabel(string text)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = text };
            Grid.SetColumn(label, 1);
            g.Children.Add(label);
            return g;
        }

        // Одна карточка: скруглённая подложка с рамкой и тенью.
        static Border Panel(Theme t, double bgOpacity, Thickness padding)
        {
            var p = new Border();
            p.CornerRadius = new CornerRadius(12);
            // прозрачным становится только фон: текст и полосы остаются чёткими при любой настройке
            Color bg = t.CardBg.Color;
            p.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Max(0, Math.Min(1, bgOpacity))), bg.R, bg.G, bg.B));
            p.BorderBrush = t.CardBorder;
            p.BorderThickness = new Thickness(1);
            p.Padding = padding;
            p.Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = t.Dark ? 0.45 : 0.14, Color = Colors.Black };
            return p;
        }

        static FrameworkElement Header(Theme t, DateTime? updatedAt, bool busy, CardActions act)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = "Лимиты нейросетей", FontFamily = DisplayFont, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = t.Text });
            string sub = busy ? "обновление…" : updatedAt.HasValue ? "обновлено " + Fmt.Clock(updatedAt.Value) : "загрузка…";
            titles.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = t.Muted, Margin = new Thickness(0, 1, 0, 0) });
            g.Children.Add(titles);

            var refresh = IconButton("", "Обновить сейчас", t, 28, 13);
            refresh.IsEnabled = !busy;
            if (act.Refresh != null) refresh.Click += (s, e) => act.Refresh();
            Grid.SetColumn(refresh, 1);
            g.Children.Add(refresh);

            if (act.CycleTheme != null)
            {
                string tg = act.ThemeMode == "light" ? "\uE706" : act.ThemeMode == "dark" ? "\uE708" : "\uE793";
                string tt = act.ThemeMode == "light" ? "Тема: светлая — нажмите для тёмной"
                    : act.ThemeMode == "dark" ? "Тема: тёмная — нажмите, чтобы как в Windows"
                    : "Тема: как в Windows — нажмите для светлой";
                var themeBtn = IconButton(tg, tt, t, 28, 13);
                themeBtn.Click += (s, e) => act.CycleTheme();
                Grid.SetColumn(themeBtn, 2);
                g.Children.Add(themeBtn);
            }

            if (act.TogglePin != null)
            {
                // в шрифте Pin и Pinned нарисованы одинаково, поэтому закреплённая скрепка — тот же глиф, повёрнутый и синий
                var pin = IconButton("", act.Pinned ? "Открепить (сейчас поверх всех окон)" : "Закрепить поверх всех окон", t, 28, 13);
                if (act.Pinned)
                {
                    var pg = (TextBlock)pin.Content;
                    pg.Foreground = new SolidColorBrush(t.Info);
                    pg.RenderTransformOrigin = new Point(0.5, 0.5);
                    pg.RenderTransform = new RotateTransform(-45);
                }
                pin.Click += (s, e) => act.TogglePin();
                Grid.SetColumn(pin, 3);
                g.Children.Add(pin);
            }

            var more = IconButton("\uE712", "Меню", t, 28, 13);
            if (act.Menu != null) more.Click += (s, e) => act.Menu(more);
            Grid.SetColumn(more, 4);
            g.Children.Add(more);

            if (act.Minimize != null)
            {
                var min = IconButton("\uE921", "Свернуть в трей", t, 28, 13);
                min.Click += (s, e) => act.Minimize();
                Grid.SetColumn(min, 5);
                g.Children.Add(min);
            }
            return g;
        }

        static FrameworkElement Service(ServiceSnapshot s, Theme t, DateTime now, CardActions act, bool compact)
        {
            var panel = new StackPanel();
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(Badge(s.Id, t));

            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 8, 0) };
            title.Children.Add(new TextBlock { Text = s.Title ?? s.Id, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = t.Text, VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrEmpty(s.Plan))
            {
                var chip = new Border { Background = t.ChipBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                chip.Child = new TextBlock { Text = s.Plan, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = t.ChipText };
                title.Children.Add(chip);
            }
            if (!string.IsNullOrEmpty(s.Url) && act.OpenUrl != null)
            {
                string url = s.Url;
                var open = IconButton("", "Открыть страницу лимитов", t, 20, 9.5);
                open.Margin = new Thickness(3, 1, 0, 0);
                open.Click += (o, e) => act.OpenUrl(url);
                title.Children.Add(open);
            }
            Grid.SetColumn(title, 1);
            head.Children.Add(title);

            var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            status.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(StatusColor(s.Status, t)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
            status.Children.Add(new TextBlock { Text = s.StatusText ?? "", FontSize = 10.5, Foreground = t.Muted, VerticalAlignment = VerticalAlignment.Center });
            status.ToolTip = StatusTip(s, now);
            Grid.SetColumn(status, 2);
            head.Children.Add(status);
            panel.Children.Add(head);

            for (int i = 0; i < s.Bars.Count; i++) panel.Children.Add(Bar(s.Bars[i], t, now, i == 0 ? 9 : compact ? 7 : 10, compact));
            var extras = VisibleExtras(s, now);
            for (int i = 0; i < extras.Count; i++)
            {
                var tile = ExtraTile(extras[i], t, compact);
                tile.Margin = new Thickness(0, i == 0 ? (compact ? 8 : 11) : (compact ? 5 : 6), 0, 0);
                panel.Children.Add(tile);
            }
            if (!compact)
                foreach (var note in s.Notes) panel.Children.Add(Small(note, t.Text2, new Thickness(0, 7, 0, 0)));
            if (!string.IsNullOrEmpty(s.Message))
            {
                Brush brush = s.Attention || s.Status == SvcStatus.Stale ? new SolidColorBrush(t.Warn)
                    : s.Status == SvcStatus.Error ? new SolidColorBrush(t.Danger) : (Brush)t.Muted;
                panel.Children.Add(Small(s.Message, brush, new Thickness(0, 7, 0, 0)));
            }
            if (!string.IsNullOrEmpty(s.ActionLabel) && !string.IsNullOrEmpty(s.ActionId) && act.Run != null)
            {
                string id = s.ActionId;
                var b = new Button();
                b.Style = PillButtonStyle();
                b.Background = new SolidColorBrush(t.Info);
                b.Padding = new Thickness(12, 4, 12, 5);
                b.Margin = new Thickness(0, 8, 0, 0);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Content = new TextBlock { Text = s.ActionLabel, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
                System.Windows.Automation.AutomationProperties.SetName(b, s.ActionLabel);
                b.Click += (o, e) => act.Run(id);
                panel.Children.Add(b);
            }
            return panel;
        }

        static Color AccentColor(string accent, Theme t)
        {
            switch (accent)
            {
                case "warn": return t.Warn;
                case "ok": return t.Ok;
                case "danger": return t.Danger;
                case "neutral": return ((SolidColorBrush)t.Muted).Color;
                default: return t.Info;
            }
        }

        // Плашка эпизодических данных: тонированная плитка; заголовок, значение справа, при необходимости полоска и разбивка.
        // Плашки, которые ещё действуют: без срока годности или со сроком в будущем (в исходном порядке).
        // Так истёкшая плашка пропадает и из кэша, когда сервис не ответил.
        public static List<ExtraItem> VisibleExtras(ServiceSnapshot s, DateTime now)
        {
            var list = new List<ExtraItem>();
            foreach (var x in s.Extras)
                if (!x.ExpiresAt.HasValue || x.ExpiresAt.Value > now) list.Add(x);
            return list;
        }

        static Border ExtraTile(ExtraItem e, Theme t, bool compact)
        {
            bool neutral = e.Accent == "neutral";
            Color accent = AccentColor(e.Accent, t);
            var tile = new Border();
            tile.CornerRadius = new CornerRadius(8);
            tile.Padding = compact ? new Thickness(9, 5, 9, 6) : new Thickness(10, 7, 10, 8);
            tile.Background = new SolidColorBrush(Color.FromArgb(neutral ? (t.Dark ? (byte)0x1A : (byte)0x10) : (t.Dark ? (byte)0x26 : (byte)0x17), accent.R, accent.G, accent.B));
            tile.BorderBrush = new SolidColorBrush(Color.FromArgb(neutral ? (t.Dark ? (byte)0x40 : (byte)0x30) : (t.Dark ? (byte)0x55 : (byte)0x40), accent.R, accent.G, accent.B));
            tile.BorderThickness = new Thickness(1);

            double indent = e.Icon != null ? 22 : 0;
            var stack = new StackPanel();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (e.Icon != null)
                row.Children.Add(new TextBlock { Text = e.Icon, FontFamily = IconFont, FontSize = 14, Foreground = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var label = new TextBlock { Text = e.Label ?? "", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = t.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            if (e.Value != null)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                bool loud = e.Accent == "warn" || e.Accent == "danger";
                var value = new TextBlock { Text = e.Value, FontFamily = DisplayFont, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = loud ? (Brush)new SolidColorBrush(accent) : t.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
                Grid.SetColumn(value, 2);
                row.Children.Add(value);
            }
            stack.Children.Add(row);

            if (e.Fill.HasValue)
            {
                double v = Math.Min(1, Math.Max(0, e.Fill.Value));
                if (v > 0 && v < 0.015) v = 0.015;
                var track = new Grid { Height = 4, Margin = new Thickness(indent, 6, 0, 0) };
                track.Children.Add(new Border { Background = t.Track, CornerRadius = new CornerRadius(2) });
                if (v > 0)
                {
                    var fill = new Grid();
                    fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(v, GridUnitType.Star) });
                    fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - v, GridUnitType.Star) });
                    fill.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(accent) });
                    track.Children.Add(fill);
                }
                stack.Children.Add(track);
            }

            string legend = null;
            if (e.Parts != null)
            {
                var shown = new List<ExtraPart>();
                foreach (var p in e.Parts) if (p.Percent > 0) shown.Add(p);
                if (shown.Count > 0)
                {
                    var colors = PartColors(shown, t);
                    // сегменты подряд пропорционально доле, между ними зазор 1.5
                    var seg = new Grid { Height = 6, Margin = new Thickness(indent, 6, 0, 0) };
                    for (int k = 0; k < shown.Count; k++)
                    {
                        if (k > 0) seg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5) });
                        seg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(shown[k].Percent, GridUnitType.Star) });
                        var piece = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(colors[k]) };
                        Grid.SetColumn(piece, seg.ColumnDefinitions.Count - 1);
                        seg.Children.Add(piece);
                    }
                    stack.Children.Add(seg);

                    var tb = new TextBlock { FontSize = 10.5, Foreground = t.Text2, Margin = new Thickness(indent, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                    var text = new System.Text.StringBuilder();
                    for (int k = 0; k < shown.Count; k++)
                    {
                        if (k > 0) { tb.Inlines.Add(new Run("   ")); text.Append("   "); }
                        string item = Fmt.Nbsp + (shown[k].Name ?? shown[k].Key).Replace(" ", Fmt.Nbsp) + Fmt.Nbsp + Fmt.Percent(shown[k].Percent);
                        tb.Inlines.Add(new Run("\u25CF") { Foreground = new SolidColorBrush(colors[k]) });
                        tb.Inlines.Add(new Run(item));
                        text.Append("\u25CF" + item);
                    }
                    legend = text.ToString();
                    if (!compact) stack.Children.Add(tb);
                }
            }

            bool hasDetail = !string.IsNullOrEmpty(e.Detail);
            if (!compact && hasDetail)
                stack.Children.Add(new TextBlock { Text = e.Detail, FontSize = 10.5, Foreground = t.Text2, Margin = new Thickness(indent, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
            tile.Child = stack;

            string tip = e.Tip ?? "";
            if (compact && hasDetail) tip = (tip.Length > 0 ? tip + "\n" : "") + e.Detail;
            if (compact && legend != null) tip = (tip.Length > 0 ? tip + "\n" : "") + legend;
            if (tip.Length > 0) tile.ToolTip = tip;
            return tile;
        }

        static bool IsNamedPart(string key)
        {
            switch (key)
            {
                case "claude_code": case "cowork": case "chat": case "other": case "kimi_code": case "kimi_other": return true;
                default: return false;
            }
        }

        // Цвет части разбивки по ключу; index — порядковый номер среди частей без своего ключа (они идут по кругу).
        static Color PartColor(string key, int index, Theme t)
        {
            switch (key)
            {
                case "claude_code": return Theme.C("#FFD97757");
                case "cowork": return t.Info;
                case "chat": return t.Ok;
                case "other": return ((SolidColorBrush)t.Muted).Color;
                case "kimi_code": return Theme.C("#FF2F6BFF");
                case "kimi_other": return Theme.C("#FF8E5CF7");
                default:
                    Color[] pool = { t.Warn, t.Danger, t.Info, t.Ok };
                    return pool[index % pool.Length];
            }
        }

        static Color[] PartColors(IList<ExtraPart> parts, Theme t)
        {
            var colors = new Color[parts.Count];
            int cycle = 0;
            for (int k = 0; k < parts.Count; k++)
            {
                colors[k] = PartColor(parts[k].Key, cycle, t);
                if (!IsNamedPart(parts[k].Key)) cycle++;
            }
            return colors;
        }

        static FrameworkElement Bar(LimitBar b, Theme t, DateTime now, double top, bool compact)
        {
            var panel = new StackPanel { Margin = new Thickness(0, top, 0, 0), Background = Brushes.Transparent };
            string cap = Caption(b, now);
            string tip = compact && !string.IsNullOrEmpty(cap) ? cap + (string.IsNullOrEmpty(b.Tip) ? "" : "\n" + b.Tip) : b.Tip;
            if (!string.IsNullOrEmpty(tip)) panel.ToolTip = tip;
            double? used = b.Used;
            bool reset = b.ResetsAt.HasValue && b.ResetsAt.Value <= now;
            if (used.HasValue && reset) used = 0;   // окно уже сброшено, свежего опроса ещё не было

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chip = new Border { Background = t.ChipBg, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 7, 0), MinWidth = 28, VerticalAlignment = VerticalAlignment.Center };
            chip.Child = new TextBlock { Text = b.Chip ?? "", FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = t.ChipText, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(chip);

            var label = new TextBlock { Text = b.Label ?? "", FontSize = 12, Foreground = b.Placeholder ? t.Muted : t.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var pct = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Typography.SetNumeralAlignment(pct, FontNumeralAlignment.Tabular);
            if (used.HasValue)
            {
                pct.Text = Fmt.Percent(used.Value);
                pct.Foreground = used.Value >= 85 ? new SolidColorBrush(t.Danger) : t.Text;
            }
            else
            {
                pct.Text = "—";
                pct.Foreground = t.Muted;
            }
            Grid.SetColumn(pct, 2);
            row.Children.Add(pct);
            panel.Children.Add(row);

            var track = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 0) };
            track.Children.Add(new Border { Background = t.Track, CornerRadius = new CornerRadius(3) });
            Color[] partColors = null;
            bool segmented = false;
            if (b.Parts != null && used.HasValue && used.Value > 0)
                foreach (var p in b.Parts) if (p.Percent > 0) segmented = true;
            if (segmented)
            {
                // заливка сегментами подряд: ширина каждого — его доля от всего лимита, остаток — трек
                partColors = PartColors(b.Parts, t);
                var seg = new Grid();
                double sum = 0;
                bool first = true;
                // первый и последний ненулевые сегменты (после ограничения суммой 100): скругляются только крайние углы заливки
                int firstIdx = -1, lastIdx = -1;
                double pre = 0;
                for (int k = 0; k < b.Parts.Count; k++)
                {
                    double pp = Math.Min(100 - pre, Math.Max(0, b.Parts[k].Percent));
                    if (pp <= 0) continue;
                    pre += pp;
                    if (firstIdx < 0) firstIdx = k;
                    lastIdx = k;
                }
                for (int k = 0; k < b.Parts.Count; k++)
                {
                    double pc = Math.Min(100 - sum, Math.Max(0, b.Parts[k].Percent));
                    if (pc <= 0) continue;
                    if (!first) seg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5) });
                    first = false;
                    sum += pc;
                    seg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(pc, GridUnitType.Star) });
                    var piece = new Border { CornerRadius = new CornerRadius(k == firstIdx ? 3 : 0, k == lastIdx ? 3 : 0, k == lastIdx ? 3 : 0, k == firstIdx ? 3 : 0), Background = new SolidColorBrush(partColors[k]) };
                    Grid.SetColumn(piece, seg.ColumnDefinitions.Count - 1);
                    seg.Children.Add(piece);
                }
                if (sum < 100) seg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - sum, GridUnitType.Star) });
                track.Children.Add(seg);
            }
            else if (used.HasValue && used.Value > 0)
            {
                double v = Math.Min(100, Math.Max(1.5, used.Value));
                var fill = new Grid();
                fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(v, GridUnitType.Star) });
                fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - v, GridUnitType.Star) });
                Color c = Severity(used.Value, t);
                fill.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new LinearGradientBrush(Theme.Lighten(c, 0.25), c, 0) });
                track.Children.Add(fill);
            }
            panel.Children.Add(track);

            if (!compact && !string.IsNullOrEmpty(cap)) panel.Children.Add(Small(cap, t.Muted, new Thickness(0, 4, 0, 0)));

            if (segmented)
            {
                // легенда: все части, включая нулевые; в компактном виде уходит в подсказку полосы
                var tb = new TextBlock { FontSize = 10.5, Foreground = t.Text2, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
                var text = new System.Text.StringBuilder();
                for (int k = 0; k < b.Parts.Count; k++)
                {
                    if (k > 0) { tb.Inlines.Add(new Run("   ")); text.Append("   "); }
                    string item = Fmt.Nbsp + (b.Parts[k].Name ?? b.Parts[k].Key).Replace(" ", Fmt.Nbsp) + Fmt.Nbsp + Fmt.Percent(b.Parts[k].Percent);
                    tb.Inlines.Add(new Run("\u25CF") { Foreground = new SolidColorBrush(partColors[k]) });
                    tb.Inlines.Add(new Run(item));
                    text.Append("\u25CF" + item);
                }
                if (compact) panel.ToolTip = (string.IsNullOrEmpty(tip) ? "" : tip + "\n") + text;
                else panel.Children.Add(tb);
            }
            return panel;
        }

        static string Caption(LimitBar b, DateTime now)
        {
            string text = null;
            if (!b.Placeholder && b.ResetsAt.HasValue)
            {
                DateTime at = b.ResetsAt.Value;
                text = at <= now ? "сброшено " + Fmt.When(at, now) : "сброс " + Fmt.When(at, now) + " · через " + Fmt.Span(at - now);
            }
            if (!string.IsNullOrEmpty(b.Note)) text = string.IsNullOrEmpty(text) ? b.Note : text + " · " + b.Note;
            return text;
        }

        static Color Severity(double used, Theme t)
        {
            return used >= 85 ? t.Danger : used >= 50 ? t.Warn : t.Ok;
        }

        static Color StatusColor(SvcStatus s, Theme t)
        {
            switch (s)
            {
                case SvcStatus.Ok: return t.Ok;
                case SvcStatus.Stale: return t.Warn;
                case SvcStatus.Error: return t.Danger;
                case SvcStatus.Fixture: return t.Info;
                default: return ((SolidColorBrush)t.Muted).Color;
            }
        }

        static string StatusTip(ServiceSnapshot s, DateTime now)
        {
            string tip = "Источник: " + (s.Source ?? "—");
            if (s.DataTime.HasValue) tip += "\nДанные на " + Fmt.When(s.DataTime.Value, now);
            if (s.Status == SvcStatus.Stale) tip += "\nПоследний опрос не удался — показан прошлый снимок";
            return tip;
        }

        static FrameworkElement Badge(string id, Theme t)
        {
            Brush bg;
            Brush fg = Brushes.White;
            string glyph;
            FontFamily ff = UiFont;
            double size = 11.5;
            switch (id)
            {
                case "claude": bg = Theme.B("#FFD97757"); glyph = "✳"; ff = SymbolFont; size = 12; break;
                case "kimi": bg = Theme.B("#FF2F6BFF"); glyph = "K"; break;
                case "antigravity": bg = new LinearGradientBrush(Theme.C("#FF4285F4"), Theme.C("#FF9B51E0"), 45); glyph = "A"; break;
                case "codex": return GptBadge();
                default: bg = Theme.B("#FF888888"); glyph = "?"; break;
            }
            var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = bg, VerticalAlignment = VerticalAlignment.Center };
            badge.Child = new TextBlock { Text = glyph, FontFamily = ff, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return badge;
        }

        // Узел в духе знака OpenAI: шесть переплетённых капсул, повёрнутых через 60°, на зелёном фоне ChatGPT.
        static FrameworkElement GptBadge()
        {
            var knot = new GeometryGroup { FillRule = FillRule.Nonzero };
            for (int k = 0; k < 6; k++)
            {
                // лепесток вынесен от центра (в середине остаётся окно) и сдвинут вбок — отсюда «переплетение»
                var petal = new RectangleGeometry(new Rect(11 - 2.3 + 1.35, 11 - 8.9, 4.6, 7.0), 2.3, 2.3);
                petal.Transform = new RotateTransform(k * 60, 11, 11);
                knot.Children.Add(petal);
            }
            knot.Freeze();
            var canvas = new Canvas { Width = 22, Height = 22 };
            canvas.Children.Add(new Path { Data = knot, Stroke = Brushes.White, StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round });
            return new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = Theme.B("#FF10A37F"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = canvas,
                ClipToBounds = true
            };
        }

        static TextBlock Small(string text, Brush brush, Thickness margin)
        {
            return new TextBlock { Text = text, FontSize = 10.5, Foreground = brush, Margin = margin, TextWrapping = TextWrapping.Wrap };
        }

        public static Button IconButton(string glyph, string tip, Theme t, double box, double fontSize)
        {
            var b = new Button();
            b.Style = IconButtonStyle();
            b.Width = box;
            b.Height = box;
            b.Padding = new Thickness(0);
            b.ToolTip = tip;
            System.Windows.Automation.AutomationProperties.SetName(b, tip);   // имя для экранного диктора и UI Automation
            b.Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = fontSize, Foreground = t.Text2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return b;
        }

        // Кнопка-действие с цветным фоном (например, «Войти»).
        static Style PillButtonStyle()
        {
            if (pillButtonStyle != null) return pillButtonStyle;
            const string xaml =
@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='BorderThickness' Value='0'/>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='7' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='bd' Property='Opacity' Value='0.88'/>
          </Trigger>
          <Trigger Property='IsPressed' Value='True'>
            <Setter TargetName='bd' Property='Opacity' Value='0.7'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            pillButtonStyle = (Style)XamlReader.Parse(xaml);
            return pillButtonStyle;
        }

        static Style IconButtonStyle()
        {
            if (iconButtonStyle != null) return iconButtonStyle;
            const string xaml =
@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='BorderThickness' Value='0'/>
  <Setter Property='Focusable' Value='False'/>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='bd' Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'>
            <Setter TargetName='bd' Property='Background' Value='{DynamicResource LW.Hover}'/>
          </Trigger>
          <Trigger Property='IsPressed' Value='True'>
            <Setter TargetName='bd' Property='Opacity' Value='0.6'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter TargetName='bd' Property='Opacity' Value='0.4'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            iconButtonStyle = (Style)XamlReader.Parse(xaml);
            return iconButtonStyle;
        }
    }
}
