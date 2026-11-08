using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DesktopWidgets.Limits.Core;
using CoreRect = DesktopWidgets.Limits.Core.Rect;

namespace DesktopWidgets.Limits.App
{
    // Компактная карточка Claude Code: без рамки, перетаскивается мышью, по выбору поверх окон.
    // Позиция сохраняется и при запуске/смене мониторов приводится к видимой рабочей области (Placement.Fit).
    public sealed class CardWindow : Window
    {
        public const int CardWidth = 340;
        static readonly IBrush Bg = new SolidColorBrush(Color.FromArgb(0xF2, 0x1E, 0x1F, 0x24));
        static readonly IBrush Fg = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF1));
        static readonly IBrush Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0x9C, 0xA8));
        static readonly IBrush Track = new SolidColorBrush(Color.FromRgb(0x34, 0x36, 0x3F));
        static readonly IBrush Ok = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0x8A));
        static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x3C));
        static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(0xEF, 0x5B, 0x5B));
        static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57));

        readonly StackPanel body = new StackPanel { Spacing = 10 };
        readonly TextBlock title = new TextBlock { Text = "Claude Code", FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Fg };
        readonly Border badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), Background = Accent, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock status = new TextBlock { FontSize = 12, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock footer = new TextBlock { FontSize = 11, Foreground = Dim, TextWrapping = TextWrapping.Wrap };
        readonly StateStore state;

        public CardWindow(StateStore state)
        {
            this.state = state;
            Title = "Лимиты нейросетей";
            Width = CardWidth;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = state.Topmost;
            WindowStartupLocation = WindowStartupLocation.Manual;

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
            badge.Child = new TextBlock { FontSize = 11, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold };
            badge.Margin = new Thickness(8, 0, 0, 0);
            header.Children.Add(title);
            Grid.SetColumn(badge, 1); header.Children.Add(badge);
            Grid.SetColumn(status, 2); header.Children.Add(status);

            var root = new StackPanel { Spacing = 12 };
            root.Children.Add(header);
            root.Children.Add(body);
            root.Children.Add(footer);
            Content = new Border { Background = Bg, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14), Child = root,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1) };

            PointerPressed += (_, e) =>
            {
                if (!state.Locked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
            };
            PositionChanged += (_, __) => state.SavePosition(Position.X, Position.Y);
            ShowPlaceholder("Опрос Claude Code…");
        }

        // Рабочие области всех мониторов в физических пикселях.
        public (List<CoreRect> Areas, int Primary) WorkAreas()
        {
            var screens = Screens.All;
            var areas = screens.Select(s => new CoreRect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height)).ToList();
            int primary = Math.Max(0, screens.ToList().FindIndex(s => s.IsPrimary));
            return (areas, primary);
        }

        // Размер в координатах Position/Screens: на Windows это физические пиксели (масштаб монитора),
        // на macOS — точки (DesktopScaling = 1 даже на Retina). RenderScaling для этого не подходит.
        public CoreRect PixelBounds()
        {
            double scale = DesktopScaling;
            return new CoreRect(Position.X, Position.Y, (int)Math.Ceiling(Bounds.Width * scale), (int)Math.Ceiling(Math.Max(Bounds.Height, 120) * scale));
        }

        // Восстановить сохранённую позицию либо поставить в правый верхний угол основного монитора.
        public CoreRect Place()
        {
            var (areas, primary) = WorkAreas();
            var size = PixelBounds();
            CoreRect target = state.Left.HasValue && state.Top.HasValue
                ? Placement.Fit(new CoreRect(state.Left.Value, state.Top.Value, size.W, size.H), areas, primary)
                : Placement.Default(size.W, size.H, areas, primary);
            Position = new PixelPoint(target.X, target.Y);
            return target;
        }

        public void SetTopmost(bool on) { Topmost = on; state.Topmost = on; state.Save(); }

        void ShowPlaceholder(string text)
        {
            body.Children.Clear();
            body.Children.Add(new TextBlock { Text = text, Foreground = Dim, FontSize = 12 });
        }

        public void Render(ServiceSnapshot s)
        {
            var now = DateTime.Now;
            ((TextBlock)badge.Child).Text = s.Plan ?? "";
            badge.IsVisible = !string.IsNullOrEmpty(s.Plan);
            ToolTip.SetTip(badge, s.PlanSource == "provider" ? "Тариф: " + s.AutoPlanSource : s.PlanSource == "manual" ? "Тариф указан вручную" : null);
            status.Text = s.StatusText;
            status.Foreground = s.Status == SvcStatus.Ok || s.Status == SvcStatus.Fixture ? Dim : Bad;

            body.Children.Clear();
            foreach (var bar in s.Bars) body.Children.Add(BarRow(bar, now));
            foreach (var e in s.Extras.Where(x => !x.ExpiresAt.HasValue || x.ExpiresAt > now)) body.Children.Add(ExtraRow(e));
            if (!string.IsNullOrEmpty(s.Message))
                body.Children.Add(new TextBlock { Text = s.Message, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = s.Attention ? Warn : Dim });
            footer.Text = (s.Status == SvcStatus.Fixture ? "Тестовые данные · " : "") + "Опрос " + now.ToString("HH:mm") + " · " + Platform.Os.Current;
            ToolTip.SetTip(footer, s.Source);
        }

        Control BarRow(LimitBar bar, DateTime now)
        {
            var row = new StackPanel { Spacing = 4 };
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var chip = new Border { Background = Track, CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0), Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = bar.Chip, FontSize = 11, Foreground = Dim } };
            top.Children.Add(chip);
            var label = new TextBlock { Text = bar.Label, FontSize = 13, Foreground = Fg, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1); top.Children.Add(label);
            var pct = new TextBlock { Text = bar.Placeholder ? "—" : bar.Used.HasValue ? Fmt.Percent(bar.Used.Value) : "нет данных", FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Fg };
            Grid.SetColumn(pct, 2); top.Children.Add(pct);
            row.Children.Add(top);

            double used = bar.Used ?? 0;
            var fill = used >= 90 ? Bad : used >= 70 ? Warn : Ok;
            var track = new Grid { Height = 6 };
            track.Children.Add(new Border { Background = Track, CornerRadius = new CornerRadius(3) });
            if (bar.Used.HasValue && !bar.Placeholder)
                track.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(3, (CardWidth - 34) * Math.Min(100, used) / 100.0) });
            row.Children.Add(track);

            string sub = bar.Note ?? (bar.ResetsAt.HasValue ? "сброс " + Fmt.When(bar.ResetsAt.Value, now) + " · через " + Fmt.Span(Fmt.Utc(bar.ResetsAt.Value) - DateTime.UtcNow) : null);
            if (sub != null) row.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Dim });
            if (bar.Tip != null) ToolTip.SetTip(row, bar.Tip);
            return row;
        }

        Control ExtraRow(ExtraItem e)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            g.Children.Add(new TextBlock { Text = e.Label + (e.Detail != null ? "\n" + e.Detail : ""), FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap });
            var v = new TextBlock { Text = e.Value ?? (e.Parts != null ? string.Join(" · ", e.Parts.Where(p => p.Percent > 0).Select(p => p.Name + " " + Fmt.Percent(p.Percent))) : ""),
                FontSize = 12, Foreground = e.Accent == "danger" ? Bad : e.Accent == "warn" ? Warn : Fg, TextWrapping = TextWrapping.Wrap, MaxWidth = 200 };
            Grid.SetColumn(v, 1); g.Children.Add(v);
            if (e.Tip != null) ToolTip.SetTip(g, e.Tip);
            return g;
        }
    }
}
