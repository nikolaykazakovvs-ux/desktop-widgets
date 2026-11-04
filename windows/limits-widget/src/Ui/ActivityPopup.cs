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

        // Рядом с главным окном: слева от него, а если места нет — справа; по вертикали — в пределах экрана.
        public void ShowNear(Window owner)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = owner.Top;
            Show();
            UpdateLayout();
            var wa = SystemParameters.VirtualScreenLeft;
            double left = owner.Left - ActualWidth + 6;
            if (left < SystemParameters.VirtualScreenLeft) left = owner.Left + owner.ActualWidth - 6;
            Left = left;
            double bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (Top + ActualHeight > bottom) Top = Math.Max(SystemParameters.VirtualScreenTop, bottom - ActualHeight);
            Activate();
            if (Content is FrameworkElement) ((FrameworkElement)Content).MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }
}
