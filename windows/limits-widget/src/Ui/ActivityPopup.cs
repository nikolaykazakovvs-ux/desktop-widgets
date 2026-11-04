using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LimitsWidget
{
    // Окно подробной статистики Codex: принадлежит главному окну (закрывается вместе с ним), без кнопки на панели задач,
    // закрывается по Esc, щелчку вне окна (потеря активности) или кнопке «×». Содержимое — независимая копия данных,
    // при обновлении сборщика пересобирается (Update); привязки к элементам главной карточки нет, поэтому перерисовка
    // карточки окно не отрывает.
    sealed class ActivityPopup : Window
    {
        readonly double scale;

        public ActivityPopup(Window owner, Theme t, double scale)
        {
            this.scale = scale;
            Owner = owner;
            Title = "Активность Codex";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Topmost = owner != null && owner.Topmost;
            UseLayoutRounding = true;
            FontFamily = CardView.UiFont;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
            Deactivated += (s, e) => { if (IsVisible) Close(); };
            SourceInitialized += (s, e) => Native.AddExStyle(new WindowInteropHelper(this).Handle, Native.WS_EX_TOOLWINDOW);
        }

        public void Update(TokenActivity a, Theme t, DateTime now)
        {
            var panel = CardView.ActivityPanel(a, t ?? Theme.Resolve("auto"), now, Close);
            if (Math.Abs(scale - 1) > 0.01) panel.LayoutTransform = new ScaleTransform(scale, scale);
            Content = panel;
        }

        // Screen возвращает пиксели; Left/Top и размеры WPF заданы в DIP владельца.
        internal static Rect WorkAreaToDip(Rect pixels, Matrix fromDevice)
        {
            return new Rect(fromDevice.Transform(pixels.TopLeft), fromDevice.Transform(pixels.BottomRight));
        }

        internal static Point Placement(Rect owner, Size popup, Rect workArea)
        {
            double left = owner.Left - popup.Width + 6;
            if (left < workArea.Left) left = owner.Right - 6;
            // Если сбоку места нет, допускаем перекрытие владельца, но не уход в промежуток между мониторами.
            left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - popup.Width));
            double top = Math.Max(workArea.Top, Math.Min(owner.Top, workArea.Bottom - popup.Height));
            return new Point(left, top);
        }

        // Рядом с главным окном, строго в рабочей области его реального монитора.
        public void ShowNear(Window owner)
        {
            var handle = new WindowInteropHelper(owner).EnsureHandle();
            var screen = System.Windows.Forms.Screen.FromHandle(handle);
            var source = HwndSource.FromHwnd(handle);
            var pixels = screen.WorkingArea;
            var workArea = WorkAreaToDip(new Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height), source.CompositionTarget.TransformFromDevice);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left;
            Top = owner.Top;
            Show();
            UpdateLayout();
            var position = Placement(new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight), new Size(ActualWidth, ActualHeight), workArea);
            Left = position.X;
            Top = position.Y;
            Activate();
            if (Content is FrameworkElement) ((FrameworkElement)Content).MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }
}
