using System;
using System.Globalization;
using System.Windows.Media;
using Microsoft.Win32;

namespace LimitsWidget
{
    sealed class Theme
    {
        public bool Dark;
        public SolidColorBrush CardBg, CardBorder, Text, Text2, Muted, Track, Separator, ChipBg, ChipText, Hover, MenuBg;
        public Color Ok, Warn, Danger, Info;

        public static Theme Resolve(string mode)
        {
            bool dark = mode == "dark" || (mode != "light" && SystemIsDark());
            return dark ? CreateDark() : CreateLight();
        }

        public static bool SystemIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        static Theme CreateDark()
        {
            var t = new Theme { Dark = true };
            t.CardBg = B("#FF17191E");   // фон непрозрачный; прозрачность задаёт пользователь (меню → Непрозрачность фона)
            t.CardBorder = B("#30FFFFFF");
            t.Text = B("#FFF3F4F6");
            t.Text2 = B("#FFC4C8CF");
            t.Muted = B("#FF8E949E");
            t.Track = B("#26FFFFFF");
            t.Separator = B("#1CFFFFFF");
            t.ChipBg = B("#24FFFFFF");
            t.ChipText = B("#FFCDD1D8");
            t.Hover = B("#24FFFFFF");
            t.MenuBg = B("#FF202329");
            t.Ok = C("#FF3FB950");
            t.Warn = C("#FFE3B341");
            t.Danger = C("#FFF85149");
            t.Info = C("#FF7C8CFF");
            return t;
        }

        static Theme CreateLight()
        {
            var t = new Theme { Dark = false };
            t.CardBg = B("#FFFBFBFC");
            t.CardBorder = B("#22000000");
            t.Text = B("#FF16181D");
            t.Text2 = B("#FF3E434B");
            t.Muted = B("#FF6B717B");
            t.Track = B("#16000000");
            t.Separator = B("#12000000");
            t.ChipBg = B("#10000000");
            t.ChipText = B("#FF4B515B");
            t.Hover = B("#14000000");
            t.MenuBg = B("#FFFBFBFC");
            t.Ok = C("#FF1F9D3A");
            t.Warn = C("#FFC98500");
            t.Danger = C("#FFD1242F");
            t.Info = C("#FF4F5BD5");
            return t;
        }

        public static Color C(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        public static SolidColorBrush B(string hex)
        {
            var b = new SolidColorBrush(C(hex));
            b.Freeze();
            return b;
        }

        public static string Hex(Color c)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
        }

        public static Color Lighten(Color c, double k)
        {
            return Color.FromArgb(c.A, (byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));
        }
    }
}
