using System;
using System.Windows;
using System.Windows.Media;

namespace ResourcesWidget
{
    // График истории: заливка под линией. Вторая серия рисуется только линией.
    // MarkPeaks — точка на наибольшем значении каждой серии (пик сети за время графика).
    sealed class Sparkline : FrameworkElement
    {
        public bool MarkPeaks;
        readonly Brush plate, grid, ring;
        readonly Pen penA, penB;
        readonly Brush fillA;
        readonly int capacity;
        double[] a, b;
        double max = 100;

        public Sparkline(Theme t, Color colorA, Color? colorB, int capacity)
        {
            this.capacity = Math.Max(2, capacity);
            plate = Theme.B(Theme.Alpha(t.Track.Color, 0.45));
            grid = t.Separator;
            ring = t.CardBg;
            penA = MakePen(colorA, 1.5);
            var fill = new LinearGradientBrush(Theme.Alpha(colorA, 0.42), Theme.Alpha(colorA, 0.04), 90);
            fill.Freeze();
            fillA = fill;
            if (colorB.HasValue) penB = MakePen(colorB.Value, 1.3);
            SnapsToDevicePixels = true;
        }

        static Pen MakePen(Color c, double width)
        {
            var p = new Pen(Theme.B(c), width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            p.Freeze();
            return p;
        }

        public void Set(double[] seriesA, double[] seriesB, double maximum)
        {
            a = seriesA;
            b = seriesB;
            max = maximum > 0 ? maximum : 1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var bounds = new Rect(0, 0, w, h);
            dc.DrawRoundedRectangle(plate, null, bounds, 5, 5);
            dc.DrawRectangle(grid, null, new Rect(0, Math.Round(h / 2), w, 1));
            dc.PushClip(new RectangleGeometry(bounds, 5, 5));
            Draw(dc, a, penA, fillA, w, h);
            Draw(dc, b, penB, null, w, h);
            dc.Pop();
            if (MarkPeaks)
            {
                Peak(dc, a, penA, w, h);
                Peak(dc, b, penB, w, h);
            }
        }

        // Точка на самом свежем из наибольших значений: цвет серии с ободком цвета карточки.
        void Peak(DrawingContext dc, double[] v, Pen pen, double w, double h)
        {
            if (v == null || v.Length < 2 || pen == null) return;
            int n = Math.Min(v.Length, capacity), first = v.Length - n, at = -1;
            double best = 0;
            for (int i = 0; i < n; i++)
                if (v[first + i] > 0 && v[first + i] >= best) { best = v[first + i]; at = i; }
            if (at < 0) return;
            double step = w / (capacity - 1);
            var pt = new Point(w - (n - 1 - at) * step, Y(best, h, 3));
            pt.X = Math.Max(3, Math.Min(w - 3, pt.X));
            dc.DrawEllipse(pen.Brush, new Pen(ring, 1.2), pt, 2.6, 2.6);
        }

        void Draw(DrawingContext dc, double[] v, Pen pen, Brush fill, double w, double h)
        {
            if (v == null || v.Length < 2 || pen == null) return;
            double step = w / (capacity - 1);
            int n = Math.Min(v.Length, capacity);
            int first = v.Length - n;
            const double pad = 1.5;   // чтобы линия у краёв не обрезалась
            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    var pt = new Point(w - (n - 1 - i) * step, Y(v[first + i], h, pad));
                    if (i == 0) g.BeginFigure(pt, false, false);
                    else g.LineTo(pt, true, true);
                }
            }
            line.Freeze();
            if (fill != null)
            {
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    double x0 = w - (n - 1) * step;
                    g.BeginFigure(new Point(x0, h), true, true);
                    for (int i = 0; i < n; i++)
                        g.LineTo(new Point(w - (n - 1 - i) * step, Y(v[first + i], h, pad)), false, false);
                    g.LineTo(new Point(w, h), false, false);
                }
                area.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, pen, line);
        }

        double Y(double value, double h, double pad)
        {
            double k = value / max;
            if (double.IsNaN(k) || k < 0) k = 0;
            if (k > 1) k = 1;
            return h - pad - k * (h - 2 * pad);
        }
    }

    // Столбики загрузки логических процессоров.
    sealed class CoreBars : FrameworkElement
    {
        readonly Brush track, normal, hot;
        double[] values;

        public CoreBars(Theme t, Color color)
        {
            track = t.Track;
            normal = Theme.B(color);
            hot = Theme.B(t.Danger);
            SnapsToDevicePixels = true;
        }

        public void Set(double[] v)
        {
            values = v;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (values == null || values.Length == 0 || w <= 0 || h <= 0) return;
            int n = values.Length;
            double gap = n > 32 ? 1 : 3;
            double bar = (w - gap * (n - 1)) / n;
            if (bar < 1) { gap = 0; bar = w / n; }
            for (int i = 0; i < n; i++)
            {
                double x = i * (bar + gap);
                dc.DrawRoundedRectangle(track, null, new Rect(x, 0, bar, h), 2, 2);
                double v = Math.Max(0, Math.Min(100, values[i]));
                if (v <= 0) continue;
                double fh = Math.Max(1.5, h * v / 100);
                dc.DrawRoundedRectangle(v >= 90 ? hot : normal, null, new Rect(x, h - fh, bar, fh), 2, 2);
            }
        }
    }

    // Горизонтальная полоса заполнения.
    sealed class Meter : FrameworkElement
    {
        readonly Brush track;
        double value;
        Color color;
        Brush fill;

        public Meter(Theme t)
        {
            track = t.Track;
            Height = 6;
            SnapsToDevicePixels = true;
        }

        public void Set(double percent, Color c)
        {
            if (fill == null || c != color)
            {
                color = c;
                var b = new LinearGradientBrush(Theme.Lighten(c, 0.25), c, 0);
                b.Freeze();
                fill = b;
            }
            value = Math.Max(0, Math.Min(100, percent));
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double r = h / 2;
            dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);
            if (value <= 0 || fill == null) return;
            double fw = Math.Max(h, w * value / 100);
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, fw, h), r, r);
        }
    }
}
