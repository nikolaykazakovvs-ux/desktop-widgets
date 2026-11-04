using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace LimitsWidget
{
    // Окно карточки растёт по содержимому (SizeToContent), но не выше рабочей области своего монитора: при большом масштабе
    // или развёрнутой статистике появляется вертикальная прокрутка (обычный WPF ScrollViewer), содержимое не обрезается
    // и не теряется. Если всё помещается — полосы нет и вид прежний. Сохранённый масштаб не меняется. Headless-экспорт
    // (--render) прокрутку не использует и выводит карточку целиком.
    static class Viewport
    {
        public const double Margin = 8, MinHeight = 240;

        public static ScrollViewer Create()
        {
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,             // Tab идёт по кнопкам карточки; фокус сам прокручивает их в видимую часть
                PanningMode = PanningMode.VerticalOnly,
                Background = Brushes.Transparent
            };
            return sv;
        }

        // Высота от верха окна до низа рабочей области (DIP). Окно выше края — берём всю высоту области: окно потом
        // сдвигается внутрь (Fit).
        public static double MaxHeight(Rect work, double top)
        {
            double t = Math.Max(work.Top, Math.Min(top, work.Bottom));
            double below = work.Bottom - t - Margin;
            return below >= MinHeight ? below : Math.Max(MinHeight, work.Height - 2 * Margin);
        }

        // Новый верх окна, чтобы окно высотой height целиком было в рабочей области (по возможности не двигая его).
        public static double Fit(Rect work, double top, double height)
        {
            if (top + height > work.Bottom - Margin) top = work.Bottom - Margin - height;
            return Math.Max(work.Top + Margin, top);
        }

        // Рабочая область монитора, на котором окно, в DIP окна (учитывает DPI); без hwnd — основной монитор.
        public static Rect WorkArea(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                var src = h != IntPtr.Zero ? HwndSource.FromHwnd(h) : null;
                if (src != null && src.CompositionTarget != null)
                {
                    var r = Forms.Screen.FromHandle(h).WorkingArea;
                    var m = src.CompositionTarget.TransformFromDevice;
                    var tl = m.Transform(new Point(r.Left, r.Top));
                    var br = m.Transform(new Point(r.Right, r.Bottom));
                    return new Rect(tl, br);
                }
            }
            catch
            {
            }
            return SystemParameters.WorkArea;
        }
    }
}
