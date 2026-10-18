using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace LimitsWidget
{
    // Значок в трее: четыре столбика — наибольший расход по каждому сервису (Claude, Kimi, Antigravity, Codex),
    // цвет по тем же порогам, что и в карточке; в подсказке — проценты.
    sealed class TrayIcon : IDisposable
    {
        readonly Forms.NotifyIcon icon = new Forms.NotifyIcon();
        Icon current;
        IntPtr handle;
        string drawnKey, tip;

        public event Action LeftClick, RightClick;

        public TrayIcon()
        {
            icon.Text = "Лимиты нейросетей";
            icon.MouseClick += (s, e) =>
            {
                Action h = e.Button == Forms.MouseButtons.Left ? LeftClick : e.Button == Forms.MouseButtons.Right ? RightClick : null;
                if (h != null) h();
            };
            Draw(new double?[] { null, null, null, null });
            icon.Visible = true;
        }

        public void Update(IList<ServiceSnapshot> snaps, DateTime now)
        {
            var values = snaps.Select(s => MaxUsed(s, now)).ToArray();
            Draw(values.Length > 0 ? values : new double?[] { null });

            var parts = new List<string>();
            for (int i = 0; i < snaps.Count; i++)
                parts.Add(ShortName(snaps[i].Id) + " " + (values[i].HasValue ? Math.Round(values[i].Value) + "%" : "—"));
            var sb = new StringBuilder("Лимиты нейросетей");
            for (int i = 0; i < parts.Count; i += 2)
                sb.Append('\n').Append(parts[i]).Append(i + 1 < parts.Count ? " · " + parts[i + 1] : "");
            string text = sb.ToString();
            if (text.Length > 63) text = text.Substring(0, 63);   // предел подсказки значка
            if (text != tip)
            {
                tip = text;
                icon.Text = text;
            }
        }

        public void Notify(string title, string text)
        {
            icon.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Warning);
        }

        // Смена темы панели задач: перерисовать при следующем обновлении.
        public void Invalidate()
        {
            drawnKey = null;
        }

        static double? MaxUsed(ServiceSnapshot s, DateTime now)
        {
            double? max = null;
            foreach (var b in s.Bars)
            {
                if (b.Placeholder || !b.Used.HasValue) continue;
                double v = b.ResetsAt.HasValue && b.ResetsAt.Value <= now ? 0 : b.Used.Value;
                if (!max.HasValue || v > max.Value) max = v;
            }
            return max;
        }

        static string ShortName(string id)
        {
            switch (id)
            {
                case "claude": return "Claude";
                case "kimi": return "Kimi";
                case "antigravity": return "AG";
                case "codex": return "Codex";
                default: return id;
            }
        }

        void Draw(double?[] v)
        {
            int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
            bool light = TaskbarIsLight();
            int n = v.Length;
            int gap = Math.Max(1, size / 10);
            int bar = Math.Max(2, (size - gap * (n - 1)) / n);
            int x0 = (size - (bar * n + gap * (n - 1))) / 2;
            var heights = new int[n];
            var colors = new Color[n];
            var key = new StringBuilder().Append(size).Append(light ? 'l' : 'd');
            for (int i = 0; i < n; i++)
            {
                double p = v[i].HasValue ? Math.Max(0, Math.Min(100, v[i].Value)) : 0;
                // даже при 0 % оставляем полоску-основание, чтобы значок не выглядел пустым
                heights[i] = !v[i].HasValue ? 0 : Math.Max(Math.Max(1, size / 8), (int)Math.Round(size * p / 100));
                colors[i] = ColorFor(p, light);
                key.Append(',').Append(heights[i]).Append(':').Append(colors[i].ToArgb());
            }
            if (key.ToString() == drawnKey) return;
            drawnKey = key.ToString();

            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var track = new SolidBrush(light ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(76, 255, 255, 255)))
                {
                    g.Clear(Color.Transparent);
                    for (int i = 0; i < n; i++)
                    {
                        int x = x0 + i * (bar + gap);
                        g.FillRectangle(track, x, 0, bar, size);
                        if (heights[i] == 0) continue;
                        using (var fill = new SolidBrush(colors[i])) g.FillRectangle(fill, x, size - heights[i], bar, heights[i]);
                    }
                }
                IntPtr h = bmp.GetHicon();
                var ico = Icon.FromHandle(h);
                icon.Icon = ico;   // оболочка копирует значок, прежний можно освобождать
                if (current != null) current.Dispose();
                if (handle != IntPtr.Zero) Native.DestroyIcon(handle);
                current = ico;
                handle = h;
            }
        }

        // Пороги как у полос карточки: до 50 % — зелёный, до 85 % — жёлтый, дальше — красный.
        static Color ColorFor(double p, bool light)
        {
            if (p >= 85) return light ? Color.FromArgb(209, 36, 47) : Color.FromArgb(248, 81, 73);
            if (p >= 50) return light ? Color.FromArgb(201, 133, 0) : Color.FromArgb(227, 179, 65);
            return light ? Color.FromArgb(31, 157, 58) : Color.FromArgb(63, 185, 80);
        }

        static bool TaskbarIsLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            if (current != null) current.Dispose();
            if (handle != IntPtr.Zero) Native.DestroyIcon(handle);
            handle = IntPtr.Zero;
        }
    }

    static class TrayPromotion
    {
        // Windows 11 прячет новые значки в скрытую область трея. Один раз выводим значок на панель задач;
        // true — выведен (или в этой версии Windows делать нечего).
        public static bool Promote(string exe)
        {
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
            {
                if (root == null) return true;
                foreach (string name in root.GetSubKeyNames())
                {
                    using (var k = root.OpenSubKey(name, true))
                    {
                        var path = k == null ? null : k.GetValue("ExecutablePath") as string;
                        if (path == null || !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                        var promoted = k.GetValue("IsPromoted");
                        if (!(promoted is int && (int)promoted == 1)) k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                        return true;
                    }
                }
            }
            return false;
        }
    }

    static class Autostart
    {
        static string LinkPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Лимиты нейросетей.lnk"); }
        }

        public static bool IsEnabled()
        {
            return File.Exists(LinkPath);
        }

        public static void Disable()
        {
            if (File.Exists(LinkPath)) File.Delete(LinkPath);
        }

        public static void Enable(string exe)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(type);
            try
            {
                object link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { LinkPath });
                var lt = link.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exe });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(exe) });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Лимиты нейросетей — виджет на рабочем столе и в трее" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
