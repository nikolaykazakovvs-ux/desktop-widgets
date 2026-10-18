using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ResourcesWidget
{
    // Карточка — обычное окно, как у «Лимитов нейросетей»: сама не прячется, сворачивается в трей кнопкой,
    // Esc, значком в трее или горячей клавишей. Скрепка закрепляет её поверх всех окон.
    // Без кнопки на панели задач и в Alt+Tab. Обычно стоит в углу у трея; перетащенная мышью запоминает место.
    sealed class CardWindow : Window
    {
        readonly State state;
        readonly Config cfg;
        readonly CardActions act;
        readonly Func<string> hotkeyText;
        Card card;
        string cardKey;
        Sample last;
        Theme theme;
        IntPtr hwnd, previousForeground;
        DateTime deactivatedAt = DateTime.MinValue;
        bool showPending, activatePending;

        public bool Exiting;
        public event Action<bool> ShownChanged;   // true — карточка на экране

        public CardWindow(State state, Config cfg, CardActions act, Func<string> hotkeyText)
        {
            this.state = state;
            this.cfg = cfg;
            this.act = act;
            this.hotkeyText = hotkeyText;

            Title = "Системные ресурсы";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = state.Topmost;
            SizeToContent = SizeToContent.WidthAndHeight;
            UseLayoutRounding = true;
            FontFamily = Ui.Font;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000;
            Top = -10000;

            SourceInitialized += (s, e) =>
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);
            };
            // после ухода фокуса возвращать его некуда: Windows сама активирует окно, где пользователь работал
            Deactivated += (s, e) =>
            {
                deactivatedAt = DateTime.UtcNow;
                previousForeground = IntPtr.Zero;
            };
            MouseLeftButtonDown += OnMouseDown;
            MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (act.Menu != null) act.Menu(null);
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape && IsActive) HideToTray(); };
            SizeChanged += (s, e) => { if (IsVisible) Place(); };
            Closing += OnClosing;
        }

        public Theme CurrentTheme
        {
            get { return theme ?? (theme = Theme.Resolve(state.Theme)); }
        }

        // Карточку видно, если окно показано и либо оно активно, либо закреплено поверх всех окон.
        // Щелчок по значку в трее сначала снимает фокус с карточки — учитываем эти 400 мс.
        public bool IsShownNow()
        {
            bool wasActive = IsActive || (DateTime.UtcNow - deactivatedAt).TotalMilliseconds < 400;
            return IsVisible && (wasActive || state.Topmost);
        }

        // Значок в трее и горячая клавиша: видна — свернуть, иначе показать и поднять над окнами.
        public void ToggleShown()
        {
            if (IsShownNow()) HideToTray();
            else Raise();
        }

        // Показать карточку поверх обычных окон и сделать её активной.
        public void Raise()
        {
            ShowCard(true);
        }

        // activate = false: показать, не отнимая фокус (восстановление при запуске, просмотр настройки из меню трея).
        public void ShowCard(bool activate)
        {
            if (last == null)
            {
                // первых показаний ещё нет (самая первая секунда) — показать, когда придут
                showPending = true;
                activatePending |= activate;
                return;
            }
            EnsureCard(last);
            bool wasVisible = IsVisible;
            if (!wasVisible) Show();
            UpdateLayout();
            Place();
            if (state.Hidden)
            {
                state.Hidden = false;
                state.Save();
            }
            if (activate)
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != hwnd && !Native.IsTaskbar(fg)) previousForeground = fg;
                if (!state.Topmost)
                {
                    // поднять на верх обычных окон, не закрепляя
                    Topmost = true;
                    Topmost = false;
                }
                Activate();
                if (hwnd != IntPtr.Zero) Native.ForceForeground(hwnd);
            }
            if (!wasVisible)
            {
                var h = ShownChanged;
                if (h != null) h(true);
            }
        }

        // Свернуть в трей: вернуть фокус окну, которое было активно до показа карточки, и скрыть окно.
        public void HideToTray()
        {
            showPending = false;
            if (!IsVisible) return;
            if (IsActive && previousForeground != IntPtr.Zero && Native.IsWindow(previousForeground) && Native.IsWindowVisible(previousForeground))
                Native.SetForegroundWindow(previousForeground);
            previousForeground = IntPtr.Zero;
            Hide();
            state.Hidden = true;
            state.Save();
            var h = ShownChanged;
            if (h != null) h(false);
        }

        // Скрепка: поверх всех окон или обычное окно.
        public void SetTopmost(bool on)
        {
            state.Topmost = on;
            state.Save();
            Topmost = on;
            Rebuild();
        }

        public void Apply(Sample s)
        {
            last = s;
            if (showPending)
            {
                showPending = false;
                ShowCard(activatePending);
                activatePending = false;
                return;
            }
            if (IsVisible) EnsureCard(s);
        }

        // Тема, вид или набор разделов изменились — построить карточку заново.
        public void Rebuild()
        {
            theme = null;
            cardKey = null;
            if (IsVisible && last != null)
            {
                EnsureCard(last);
                UpdateLayout();
                Place();
            }
        }

        public void Redock()
        {
            state.Docked = true;
            state.Left = state.Top = null;
            state.Save();
            if (IsVisible) Place();
        }

        void EnsureCard(Sample s)
        {
            var t = CurrentTheme;
            string key = Card.SignatureOf(s, state, t) + "|" + state.Scale + "|" + state.Opacity;
            if (card != null && key == cardKey)
            {
                card.Update(s);
                return;
            }
            card = new Card(s, t, state, cfg, act, hotkeyText());
            cardKey = key;
            var root = card.Root;
            if (Math.Abs(state.Scale - 1) > 0.01) root.LayoutTransform = new ScaleTransform(state.Scale, state.Scale);
            Content = root;
        }

        // ---------- положение ----------

        void Place()
        {
            if (!state.Docked && state.Left.HasValue && state.Top.HasValue && OnScreen(state.Left.Value, state.Top.Value))
            {
                Left = state.Left.Value;
                Top = state.Top.Value;
                return;
            }
            Dock();
        }

        // У трея: угол рабочей области со стороны панели задач. Внутри окна вокруг плашек поля под тень.
        void Dock()
        {
            var wa = SystemParameters.WorkArea;
            double k = state.Scale, gap = 12;
            bool top = wa.Top > 0.5, left = wa.Left > 0.5;
            Left = left ? wa.Left + gap - 14 * k : wa.Right - ActualWidth + 14 * k - gap;
            Top = top ? wa.Top + gap - 10 * k : wa.Bottom - ActualHeight + 18 * k - gap;
            // крупный размер не помещается по высоте — шапка с кнопками всё равно остаётся на экране
            if (Top < wa.Top - 10 * k) Top = wa.Top - 10 * k;
        }

        static bool OnScreen(double x, double y)
        {
            double l = SystemParameters.VirtualScreenLeft, t = SystemParameters.VirtualScreenTop;
            return x + 80 > l && x + 80 < l + SystemParameters.VirtualScreenWidth
                && y + 30 > t && y + 30 < t + SystemParameters.VirtualScreenHeight;
        }

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || state.Locked) return;
            double x = Left, y = Top;
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            if (Math.Abs(Left - x) < 2 && Math.Abs(Top - y) < 2) return;
            state.Docked = false;
            state.Left = Left;
            state.Top = Top;
            state.Save();
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            // Alt+F4 на карточке сворачивает её в трей; закрывается окно только при выходе из программы.
            if (Exiting) return;
            e.Cancel = true;
            HideToTray();
        }
    }
}
