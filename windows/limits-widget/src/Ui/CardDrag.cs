using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace LimitsWidget
{
    // Ручной порядок карточек: сначала сервисы из order в его порядке, затем остальные как были.
    static class CardOrder
    {
        public static List<ServiceSnapshot> Apply(IList<ServiceSnapshot> snaps, IList<string> order)
        {
            var res = new List<ServiceSnapshot>();
            if (snaps == null) return res;
            if (order == null || order.Count == 0)
            {
                res.AddRange(snaps);
                return res;
            }
            var used = new HashSet<ServiceSnapshot>();
            foreach (var id in order)
                foreach (var s in snaps)
                    if (!used.Contains(s) && s.Id == id)
                    {
                        res.Add(s);
                        used.Add(s);
                        break;
                    }
            foreach (var s in snaps) if (!used.Contains(s)) res.Add(s);
            return res;
        }
    }

    // Перетаскивание карточки нейросети по вертикали: остальные расступаются, по отпусканию порядок уходит в commit.
    sealed class CardDrag
    {
        const double Threshold = 4;
        readonly Action<List<string>> commit;
        StackPanel root;
        Border dragged;
        List<Border> cards;
        double[] tops, heights, margins, shifts;
        double slot, startY, offset;
        bool started;
        Effect savedEffect;
        double savedOpacity;
        int savedZ;
        Action cleanup;
        bool settling;   // идёт анимация приземления/возврата: дерево перестраивать и начинать новое перетаскивание рано

        public CardDrag(Action<List<string>> commit)
        {
            this.commit = commit;
        }

        // идёт перетаскивание или ожидание порога
        public bool Active { get; private set; }
        // перетаскивание или доводка после отпускания: Render откладывается
        public bool Busy { get { return Active || settling; } }
        public int FromIndex { get; private set; }
        public int TargetIndex { get; private set; }
        // смещение перетаскиваемой карточки и целевой сдвиг i-й карточки (для проверки без анимаций)
        public double DragOffset { get { return offset; } }
        public double ShiftOf(int i) { return shifts != null && i >= 0 && i < shifts.Length ? shifts[i] : 0; }

        // start — в координатах root
        public void Begin(StackPanel root, Border card, Point start)
        {
            if (settling) return;
            if (Active) Cancel();
            this.root = root;
            cards = root.Children.OfType<Border>().Where(b => b.Tag is string).ToList();
            int from = cards.IndexOf(card);
            if (from < 0) return;
            int n = cards.Count;
            tops = new double[n];
            heights = new double[n];
            margins = new double[n];
            shifts = new double[n];
            for (int i = 0; i < n; i++)
            {
                tops[i] = VisualTreeHelper.GetOffset(cards[i]).Y;   // раскладочный верх: без RenderTransform, оставшегося от прошлого перетаскивания
                heights[i] = cards[i].ActualHeight;
                margins[i] = cards[i].Margin.Top;
            }
            dragged = card;
            FromIndex = TargetIndex = from;
            slot = heights[from] + margins[from];
            startY = start.Y;
            offset = 0;
            started = false;
            Active = true;
        }

        public void Move(Point p)
        {
            if (!Active) return;
            double dy = p.Y - startY;
            if (!started)
            {
                if (Math.Abs(dy) < Threshold) return;
                StartDrag();
            }
            int from = FromIndex, n = cards.Count;
            // не выше верха первой и не ниже низа последней карточки нейросети
            double min = tops[0] - tops[from];
            double max = tops[n - 1] + heights[n - 1] - (tops[from] + heights[from]);
            offset = Math.Max(min, Math.Min(max, dy));
            ((TranslateTransform)dragged.RenderTransform).Y = offset;

            double center = tops[from] + heights[from] / 2 + offset;
            int target = 0;
            for (int i = 0; i < n; i++)
                if (i != from && tops[i] + heights[i] / 2 < center) target++;
            // у упора — крайнее место: иначе высокая карточка не дотянулась бы до края мимо центра низкой
            if (offset <= min) target = 0;
            else if (offset >= max) target = n - 1;
            TargetIndex = target;
            for (int i = 0; i < n; i++)
            {
                if (i == from) continue;
                double to = 0;
                if (from < target && i > from && i <= target) to = -slot;
                else if (from > target && i >= target && i < from) to = slot;
                if (to != shifts[i])
                {
                    shifts[i] = to;
                    Animate(cards[i], to, 120);
                }
            }
        }

        void StartDrag()
        {
            started = true;
            foreach (var c in cards) if (c.RenderTransform as TranslateTransform == null) c.RenderTransform = new TranslateTransform();
            savedZ = Panel.GetZIndex(dragged);
            savedEffect = dragged.Effect;
            savedOpacity = dragged.Opacity;
            Panel.SetZIndex(dragged, 10);
            dragged.Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.5, Color = Colors.Black };
            dragged.Opacity = 0.96;
        }

        static void Animate(Border c, double to, int ms)
        {
            var tt = (TranslateTransform)c.RenderTransform;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        public void Cancel()
        {
            End(false);
        }

        public void End(bool doCommit)
        {
            if (!Active) return;
            Active = false;
            var done = cleanup;
            cleanup = null;
            if (done != null) done();

            if (!started)
            {
                commit(null);
                return;
            }
            started = false;
            int from = FromIndex, target = TargetIndex;
            if (doCommit && target != from)
            {
                var ids = cards.Select(c => (string)c.Tag).ToList();
                string id = ids[from];
                ids.RemoveAt(from);
                ids.Insert(target, id);
                // конечное смещение — сумма слотов карточек, через которые перешагнули
                double final = 0;
                if (target > from) for (int j = from + 1; j <= target; j++) final += heights[j] + margins[j];
                else for (int j = target; j < from; j++) final -= heights[j] + margins[j];
                var d = dragged;
                var tt = (TranslateTransform)d.RenderTransform;
                var anim = new DoubleAnimation(final, TimeSpan.FromMilliseconds(100)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                settling = true;
                anim.Completed += (s, e) => { settling = false; commit(ids); };
                tt.BeginAnimation(TranslateTransform.YProperty, anim);
                return;
            }
            // отмена или карточка осталась на месте: все назад в ноль
            var back = dragged;
            foreach (var c in cards) if (c != back) Animate(c, 0, 100);
            back.Effect = savedEffect;
            back.Opacity = savedOpacity;
            Panel.SetZIndex(back, savedZ);
            // перерисовка только после возврата, иначе новое дерево скрывает анимацию
            var backAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            settling = true;
            backAnim.Completed += (s, e) => { settling = false; commit(null); };
            ((TranslateTransform)back.RenderTransform).BeginAnimation(TranslateTransform.YProperty, backAnim);
        }

        // Привязка к мыши: захват на карточке, движение и отпускание идут через неё.
        public void Attach(StackPanel root, Border card, MouseButtonEventArgs e)
        {
            Begin(root, card, e.GetPosition(root));
            if (!Active) return;
            MouseEventHandler move = (s, a) => Move(a.GetPosition(root));
            MouseButtonEventHandler up = (s, a) => End(true);
            MouseEventHandler lost = (s, a) => { if (Active) End(false); };
            card.MouseMove += move;
            card.MouseLeftButtonUp += up;
            card.LostMouseCapture += lost;
            cleanup = () =>
            {
                card.MouseMove -= move;
                card.MouseLeftButtonUp -= up;
                card.LostMouseCapture -= lost;
                card.ReleaseMouseCapture();
                Mouse.OverrideCursor = null;
            };
            card.CaptureMouse();
            Mouse.OverrideCursor = Cursors.SizeNS;   // при захвате курсор берётся у карточки, а не у ручки
        }
    }
}
