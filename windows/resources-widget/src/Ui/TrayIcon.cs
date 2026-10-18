using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ResourcesWidget
{
    // Значок в трее: столбики загрузки процессора, памяти и видеокарты; в подсказке — цифры.
    // Значок перерисовывается, только когда меняется высота столбика или цвет.
    sealed class TrayIcon : IDisposable
    {
        readonly Forms.NotifyIcon icon = new Forms.NotifyIcon();
        readonly bool bits;
        Icon current;
        IntPtr handle;
        string drawnKey, tip;

        public event Action LeftClick, RightClick;

        public TrayIcon(bool networkBits)
        {
            bits = networkBits;
            icon.Text = "Системные ресурсы";
            icon.MouseClick += (s, e) =>
            {
                Action h = e.Button == Forms.MouseButtons.Left ? LeftClick : e.Button == Forms.MouseButtons.Right ? RightClick : null;
                if (h != null) h();
            };
            Draw(new double[] { 0, 0 });
            icon.Visible = true;
        }

        public void Update(Sample s)
        {
            double cpu = s.Cpu.Total, mem = s.Mem != null ? s.Mem.Percent : 0;
            Draw(s.Gpu != null ? new[] { cpu, mem, s.Gpu.Load } : new[] { cpu, mem });

            string text = "ЦП " + Fmt.Percent(cpu) + " · ОЗУ " + Fmt.Percent(mem) + (s.Gpu != null ? " · ГП " + Fmt.Percent(s.Gpu.Load) : "");
            if (s.Net != null) text += "\n↓ " + Fmt.NetRate(s.Net.DownBps, bits) + "  ↑ " + Fmt.NetRate(s.Net.UpBps, bits);
            if (text.Length > 63) text = text.Substring(0, 63);   // предел подсказки значка
            if (text != tip)
            {
                tip = text;
                icon.Text = text;
            }
        }

        // Смена темы панели задач: перерисовать при следующем обновлении.
        public void Invalidate()
        {
            drawnKey = null;
        }

        public void Notify(string title, string text)
        {
            icon.ShowBalloonTip(8000, title, text, Forms.ToolTipIcon.Warning);
        }

        void Draw(double[] v)
        {
            int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
            bool light = TaskbarIsLight();
            int n = v.Length;
            int gap = Math.Max(1, size / 8);
            int bar = (size - gap * (n - 1)) / n;
            int x0 = (size - (bar * n + gap * (n - 1))) / 2;
            var heights = new int[n];
            var colors = new Color[n];
            var key = new StringBuilder().Append(size).Append(light ? 'l' : 'd');
            for (int i = 0; i < n; i++)
            {
                double p = Math.Max(0, Math.Min(100, v[i]));
                heights[i] = p <= 0 ? 0 : Math.Max(1, (int)Math.Round(size * p / 100));
                colors[i] = ColorFor(i, p, light);
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

        // Столбики: процессор — синий, память — фиолетовая, видеокарта — зелёная; от 75 % — жёлтый, от 90 % — красный.
        static Color ColorFor(int index, double p, bool light)
        {
            if (p >= 90) return light ? Color.FromArgb(209, 36, 47) : Color.FromArgb(248, 81, 73);
            if (p >= 75) return light ? Color.FromArgb(201, 133, 0) : Color.FromArgb(227, 179, 65);
            switch (index)
            {
                case 0: return light ? Color.FromArgb(47, 111, 235) : Color.FromArgb(76, 154, 255);
                case 1: return light ? Color.FromArgb(130, 80, 223) : Color.FromArgb(179, 136, 255);
                default: return light ? Color.FromArgb(78, 154, 6) : Color.FromArgb(118, 192, 67);
            }
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
}
