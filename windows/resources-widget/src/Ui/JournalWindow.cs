using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ResourcesWidget
{
    // Где стоит карточка (в независимых единицах экрана) — для первого открытия журнала рядом с ней.
    sealed class JournalAnchor
    {
        public double CardLeft, CardRight, SectionTop, SectionBottom;
        public Rect Area;   // рабочая область монитора с карточкой
    }

    // Окно «Сеть по дням»: отдельное, плавающее, перетаскивается за шапку и свободное место фона.
    // Первый раз открывается вплотную слева от карточки на уровне раздела «Сеть» (справа или поверх карточки, если сбоку мало места);
    // после перетаскивания место запоминается в state.json и окно открывается там же. Данные обновляются раз в минуту.
    sealed class JournalWindow : Window
    {
        static JournalWindow current;

        readonly State state;
        readonly PeakJournal view;
        readonly FrameworkElement content;
        readonly Func<JournalAnchor> anchor;
        readonly DispatcherTimer refresh;
        bool userPlaced;
        bool dragging, moved;
        Point dragFrom;
        double leftFrom, topFrom;

        public static void Open(Theme t, Config cfg, State state, NetJournal journal, Func<JournalAnchor> anchor, Window owner)
        {
            if (current != null)
            {
                current.view.Refresh();
                current.Activate();
                return;
            }
            current = new JournalWindow(t, cfg, state, journal, anchor, owner);
            current.Show();
        }

        public static void CloseIfOpen()
        {
            if (current != null) current.Close();
        }

        JournalWindow(Theme t, Config cfg, State state, NetJournal journal, Func<JournalAnchor> anchor, Window owner)
        {
            this.state = state;
            this.anchor = anchor;
            Title = "Сеть по дням";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (owner != null && owner.IsVisible) Owner = owner;   // поверх карточки и вместе с ней закрывается при выходе
            else Topmost = state.Topmost;

            view = new PeakJournal(t, cfg, MenuStyles.ToolTip(t), journal);
            view.Close = Close;
            content = new System.Windows.Controls.Border { Child = view.Root, LayoutTransform = new ScaleTransform(state.Scale, state.Scale) };
            Content = content;

            // Перетаскивание своё, с захватом мыши: нажатия, которые забрали строки и кнопки, сюда не доходят.
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.Handled) return;
                dragging = true;
                moved = false;
                dragFrom = PointToScreen(e.GetPosition(this));
                leftFrom = Left;
                topFrom = Top;
                CaptureMouse();
                e.Handled = true;
            };
            MouseMove += (s, e) =>
            {
                if (!dragging) return;
                Point p = PointToScreen(e.GetPosition(this));
                var dpi = VisualTreeHelper.GetDpi(this);
                double dx = (p.X - dragFrom.X) / dpi.DpiScaleX, dy = (p.Y - dragFrom.Y) / dpi.DpiScaleY;
                if (!moved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;
                moved = true;
                Left = leftFrom + dx;
                Top = topFrom + dy;
            };
            MouseLeftButtonUp += (s, e) => EndDrag();
            LostMouseCapture += (s, e) => EndDrag();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            // список и вид дня разной высоты: окно, которое не двигали, ставится заново; передвинутое — только не уходит за экран
            SizeChanged += (s, e) =>
            {
                if (!IsLoaded) return;
                if (userPlaced) KeepOnScreen();
                else PlaceNearCard();
            };
            refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            refresh.Tick += (s, e) => view.Refresh();
            refresh.Start();
            Closed += (s, e) =>
            {
                refresh.Stop();
                if (current == this) current = null;
            };

            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (state.JournalLeft.HasValue && state.JournalTop.HasValue && OnSomeScreen(state.JournalLeft.Value, state.JournalTop.Value, content.DesiredSize))
            {
                userPlaced = true;
                Left = state.JournalLeft.Value;
                Top = state.JournalTop.Value;
            }
            else PlaceNearCard();
        }

        void EndDrag()
        {
            if (!dragging) return;
            dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            if (!moved) return;
            userPlaced = true;
            state.JournalLeft = Math.Round(Left);
            state.JournalTop = Math.Round(Top);
            state.Save();
        }

        Thickness VisibleMargin
        {
            get
            {
                var m = view.Root.Margin;
                double k = state.Scale;
                return new Thickness(m.Left * k, m.Top * k, m.Right * k, m.Bottom * k);
            }
        }

        // Вплотную слева от карточки, верх — по разделу «Сеть»; не помещается по высоте — низом к низу раздела.
        // Слева нет места — справа; и там нет — поверх карточки.
        void PlaceNearCard()
        {
            var a = anchor != null ? anchor() : null;
            Size size = IsLoaded ? new Size(ActualWidth, ActualHeight) : content.DesiredSize;
            var m = VisibleMargin;
            double visW = size.Width - m.Left - m.Right, visH = size.Height - m.Top - m.Bottom;
            if (a == null)
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - size.Width - 20;
                Top = wa.Top + 40;
                return;
            }
            const double gap = 6;
            double left = a.CardLeft - gap - visW;
            if (left < a.Area.Left)
            {
                left = a.CardRight + gap;
                if (left + visW > a.Area.Right) left = Math.Max(a.Area.Left, Math.Min(a.CardLeft, a.Area.Right - visW));   // поверх карточки
            }
            double top = a.SectionTop;
            if (top + visH > a.Area.Bottom) top = a.SectionBottom - visH;
            if (top < a.Area.Top) top = a.Area.Top;
            Left = left - m.Left;
            Top = top - m.Top;
        }

        void KeepOnScreen()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left * dpi.DpiScaleX), (int)(Top * dpi.DpiScaleY)));
            var wa = screen.WorkingArea;
            double right = wa.Right / dpi.DpiScaleX, bottom = wa.Bottom / dpi.DpiScaleY;
            if (Top + ActualHeight > bottom + VisibleMargin.Bottom) Top = Math.Max(wa.Top / dpi.DpiScaleY - VisibleMargin.Top, bottom + VisibleMargin.Bottom - ActualHeight);
            if (Left + ActualWidth > right + VisibleMargin.Right) Left = Math.Max(wa.Left / dpi.DpiScaleX - VisibleMargin.Left, right + VisibleMargin.Right - ActualWidth);
        }

        // Запомненное место ещё на каком-нибудь мониторе (мониторы могли отключить или переставить).
        static bool OnSomeScreen(double left, double top, Size size)
        {
            double k;
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) k = 96.0 / Math.Max(1, g.DpiX);
            var r = new Rect(left, top, Math.Max(50, size.Width), 60);
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                var wa = s.WorkingArea;
                var area = new Rect(wa.Left * k, wa.Top * k, wa.Width * k, wa.Height * k);
                if (area.IntersectsWith(r)) return true;
            }
            return false;
        }
    }
}
