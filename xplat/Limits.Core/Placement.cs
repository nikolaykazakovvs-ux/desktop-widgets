using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopWidgets.Limits.Core
{
    public readonly struct Rect
    {
        public readonly int X, Y, W, H;
        public Rect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public int Right => X + W;
        public int Bottom => Y + H;
        public long Overlap(Rect o)
        {
            long w = Math.Min(Right, o.Right) - Math.Max(X, o.X), h = Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }
        public override string ToString() => $"{X},{Y} {W}x{H}";
    }

    // Размещение карточки на нескольких мониторах (физические пиксели, рабочие области без панели задач/строки меню/Dock).
    // Правила: окно остаётся на мониторе, с которым больше всего пересекается; если монитор отключён и пересечения нет —
    // переносится на основной; всегда целиком внутри рабочей области (если помещается).
    public static class Placement
    {
        public static Rect Fit(Rect window, IReadOnlyList<Rect> workAreas, int primaryIndex, int margin = 8)
        {
            if (workAreas == null || workAreas.Count == 0) return window;
            Rect area = workAreas.OrderByDescending(a => a.Overlap(window)).First();
            if (area.Overlap(window) == 0) area = workAreas[Math.Clamp(primaryIndex, 0, workAreas.Count - 1)];
            int w = Math.Min(window.W, Math.Max(1, area.W - 2 * margin)), h = Math.Min(window.H, Math.Max(1, area.H - 2 * margin));
            int x = Math.Clamp(window.X, area.X + margin, Math.Max(area.X + margin, area.Right - margin - w));
            int y = Math.Clamp(window.Y, area.Y + margin, Math.Max(area.Y + margin, area.Bottom - margin - h));
            return new Rect(x, y, w, h);
        }

        // Позиция по умолчанию: правый верхний угол основного монитора.
        public static Rect Default(int width, int height, IReadOnlyList<Rect> workAreas, int primaryIndex, int margin = 16)
        {
            if (workAreas == null || workAreas.Count == 0) return new Rect(100, 100, width, height);
            var a = workAreas[Math.Clamp(primaryIndex, 0, workAreas.Count - 1)];
            return Fit(new Rect(a.Right - width - margin, a.Y + margin, width, height), workAreas, primaryIndex);
        }
    }
}
