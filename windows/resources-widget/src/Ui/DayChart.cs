using System;
using System.Windows;
using System.Windows.Media;

namespace ResourcesWidget
{
    // График суток по минутам (1440 точек): заливка (Area), линия (Line), бледная линия (Faint) и красные отметки (Marks > 0).
    // NaN — минут без данных (компьютер выключен или виджет не работал): там разрыв.
    sealed class DayChart : FrameworkElement
    {
        public double[] Area, Line, Faint;
        public int[] Marks;
        public double Max = 1;

        readonly Brush plate, grid, areaFill, mark;
        readonly Pen areaPen, linePen, faintPen;

        public DayChart(Theme t, Color area, Color? line, Color? faint)
        {
            plate = Theme.B(Theme.Alpha(t.Track.Color, 0.45));
            grid = t.Separator;
            var fill = new LinearGradientBrush(Theme.Alpha(area, 0.45), Theme.Alpha(area, 0.08), 90);
            fill.Freeze();
            areaFill = fill;
            areaPen = MakePen(area, 1.2);
            if (line.HasValue) linePen = MakePen(line.Value, 1.1);
            if (faint.HasValue) faintPen = MakePen(Theme.Alpha(faint.Value, 0.45), 0.8);
            mark = Theme.B(t.Danger);
            SnapsToDevicePixels = true;
        }

        static Pen MakePen(Color c, double width)
        {
            var p = new Pen(Theme.B(c), width) { LineJoin = PenLineJoin.Round };
            p.Freeze();
            return p;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var bounds = new Rect(0, 0, w, h);
            dc.DrawRoundedRectangle(plate, null, bounds, 5, 5);
            for (int hour = 6; hour < 24; hour += 6)
                dc.DrawRectangle(grid, null, new Rect(Math.Round(w * hour / 24), 0, 1, h));
            dc.DrawRectangle(grid, null, new Rect(0, Math.Round(h / 2), w, 1));
            dc.PushClip(new RectangleGeometry(bounds, 5, 5));
            Draw(dc, Faint, faintPen, null, w, h);
            Draw(dc, Area, areaPen, areaFill, w, h);
            Draw(dc, Line, linePen, null, w, h);
            if (Marks != null)
            {
                double mw = Math.Max(1.5, w / DayDetail.Minutes);
                for (int i = 0; i < Marks.Length; i++)
                    if (Marks[i] > 0) dc.DrawRectangle(mark, null, new Rect(X(i, w) - mw / 2, h - 5, mw, 5));
            }
            dc.Pop();
        }

        static double X(int i, double w)
        {
            return (i + 0.5) * w / DayDetail.Minutes;
        }

        double Y(double v, double h)
        {
            double k = Max > 0 ? v / Max : 0;
            if (double.IsNaN(k) || k < 0) k = 0;
            if (k > 1) k = 1;
            return h - 1.5 - k * (h - 3);
        }

        // Отрезки между пропусками: у каждого своя фигура; у заливки фигура замыкается по низу графика.
        void Draw(DrawingContext dc, double[] v, Pen pen, Brush fill, double w, double h)
        {
            if (v == null || pen == null) return;
            var line = new StreamGeometry();
            var area = fill != null ? new StreamGeometry() : null;
            using (var gl = line.Open())
            using (var ga = area != null ? area.Open() : null)
            {
                int i = 0;
                while (i < v.Length)
                {
                    while (i < v.Length && double.IsNaN(v[i])) i++;
                    if (i >= v.Length) break;
                    int start = i;
                    while (i < v.Length && !double.IsNaN(v[i])) i++;
                    int end = i - 1;
                    // одиночная минута — короткий штрих, чтобы её было видно
                    double x0 = X(start, w), x1 = end > start ? X(end, w) : x0 + Math.Max(1, w / DayDetail.Minutes);
                    gl.BeginFigure(new Point(x0, Y(v[start], h)), false, false);
                    for (int k = start + 1; k <= end; k++) gl.LineTo(new Point(X(k, w), Y(v[k], h)), true, true);
                    if (end == start) gl.LineTo(new Point(x1, Y(v[start], h)), true, true);
                    if (ga != null)
                    {
                        ga.BeginFigure(new Point(x0, h), true, true);
                        for (int k = start; k <= end; k++) ga.LineTo(new Point(X(k, w), Y(v[k], h)), false, false);
                        if (end == start) ga.LineTo(new Point(x1, Y(v[start], h)), false, false);
                        ga.LineTo(new Point(x1, h), false, false);
                    }
                }
            }
            line.Freeze();
            if (area != null)
            {
                area.Freeze();
                dc.DrawGeometry(fill, null, area);
            }
            dc.DrawGeometry(null, pen, line);
        }
    }
}
