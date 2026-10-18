using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LimitsWidget
{
    // Карточка — обычное окно: не прячется само, сворачивается в трей кнопкой, Esc, значком или горячей клавишей.
    // Скрепка закрепляет её поверх всех окон; значок в трее, горячая клавиша и повторный запуск exe показывают её.
    sealed class WidgetWindow : Window
    {
        readonly CliOptions opts;
        readonly Config cfg;
        readonly State state;
        readonly Collector collector;
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly string exePath;
        readonly TrayIcon tray;
        readonly Hotkey hotkey;
        List<ServiceSnapshot> snaps = new List<ServiceSnapshot>();
        DateTime? updatedAt, lastStart;
        DateTime deactivatedAt;
        bool busy;
        IntPtr hwnd, previousForeground;
        Theme theme;
        readonly CardDrag cardDrag;

        public WidgetWindow(CliOptions opts)
        {
            this.opts = opts;
            exePath = Assembly.GetExecutingAssembly().Location;
            cfg = Config.Load(opts.ConfigPath ?? Path.Combine(Program.BaseDir, "config.json"));
            state = State.Load(opts.StatePath ?? Path.Combine(Program.BaseDir, "state.json"));
            collector = new Collector(cfg, state, opts.FixturesDir);
            cardDrag = new CardDrag(OnCardsReordered);

            Title = "Лимиты нейросетей";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Topmost = state.Topmost;
            UseLayoutRounding = true;
            FontFamily = CardView.UiFont;
            PlaceInitially();

            SourceInitialized += OnSourceInitialized;
            MouseLeftButtonDown += OnMouseDown;
            MouseRightButtonUp += (s, e) => { e.Handled = true; OpenMenu(null, false); };
            KeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape) return;
                if (cardDrag.Active) { cardDrag.Cancel(); e.Handled = true; return; }   // Esc отменяет перетаскивание, а не сворачивает
                if (IsActive) HideToTray();
            };
            // после ухода фокуса возвращать его некуда: Windows сама активирует окно, где пользователь работал
            Deactivated += (s, e) => { deactivatedAt = DateTime.UtcNow; previousForeground = IntPtr.Zero; };
            Closing += (s, e) => SavePosition();
            Closed += (s, e) =>
            {
                timer.Stop();
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                if (hotkey != null) hotkey.Dispose();
                if (tray != null) tray.Dispose();
            };
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            tray = new TrayIcon();
            tray.LeftClick += ToggleShown;
            tray.RightClick += () => OpenMenu(null, true);
            hotkey = new Hotkey(cfg.Hotkey, ToggleShown);
            if (hotkey.Error != null)
            {
                Log.Write("hotkey: " + hotkey.Error);
                tray.Notify("Лимиты нейросетей", "Горячая клавиша не работает: " + hotkey.Error + ". Карточка поднимается щелчком по значку в трее.");
            }
            ListenForRequests();

            timer.Interval = TimeSpan.FromSeconds(30);
            timer.Tick += (s, e) => OnTick();
            Render();
        }

        // Запуск из Program: окно создаётся, но показывается, только если карточка не была свёрнута в трей.
        public void Start()
        {
            new WindowInteropHelper(this).EnsureHandle();   // hwnd нужен и скрытому окну: иначе Close() не сработает
            Refresh();
            timer.Start();
            FirstRunSetup();
            if (!state.Hidden || opts.Show) Show();
            if (opts.Show) Raise();
        }

        // ---------- данные ----------

        void OnTick()
        {
            if (!busy && (!lastStart.HasValue || (DateTime.Now - lastStart.Value).TotalSeconds >= cfg.RefreshSeconds)) Refresh();
            else Render();   // обновить обратный отсчёт до сброса
        }

        void Refresh()
        {
            if (busy) return;
            busy = true;
            lastStart = DateTime.Now;
            Render();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<ServiceSnapshot> result = null;
                try { result = collector.Collect(); }
                catch (Exception ex) { Log.Write("collect: " + ex); }
                state.Save();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    busy = false;
                    if (result != null)
                    {
                        snaps = result;
                        updatedAt = DateTime.Now;
                    }
                    Render();
                }));
            });
        }

        void Render()
        {
            // перестройка дерева посреди перетаскивания уничтожила бы карточку в руке;
            // по окончании CardDrag всегда вызывает OnCardsReordered, и тот перерисует
            if (cardDrag.Busy) return;
            theme = Theme.Resolve(opts.Theme ?? state.Theme);
            ApplyLoginOverlay();
            var actions = new CardActions { Refresh = Refresh, Menu = a => OpenMenu(a, false), OpenUrl = OpenUrl, Run = RunAction, TogglePin = ToggleTopmost, Minimize = HideToTray, Pinned = state.Topmost, CycleTheme = CycleTheme, ThemeMode = state.Theme };
            actions.BeginDrag = (id, card, e) => cardDrag.Attach((StackPanel)card.Parent, card, e);
            actions.MoveCard = MoveCard;
            var shown = CardOrder.Apply(snaps, state.CardOrder);
            var view = CardView.Build(shown, theme, DateTime.Now, updatedAt, busy, actions, state.Compact, state.Opacity);
            if (Math.Abs(state.Scale - 1) > 0.01) view.LayoutTransform = new ScaleTransform(state.Scale, state.Scale);
            Content = view;
            if (tray != null && shown.Count > 0) tray.Update(shown, DateTime.Now);
        }

        // Перетаскивание закончилось: order — новый порядок id, null — без изменений (просто перерисовать).
        void OnCardsReordered(List<string> order)
        {
            if (order != null)
            {
                state.CardOrder = order;
                state.Save();
            }
            Render();
        }

        // Пункты «Переместить выше / ниже» в меню ручки: обмен с соседом в отображаемом порядке.
        void MoveCard(string id, int delta)
        {
            var ids = CardOrder.Apply(snaps, state.CardOrder).Select(s => s.Id).ToList();
            int i = ids.IndexOf(id), j = i + delta;
            if (i < 0 || j < 0 || j >= ids.Count) return;
            string tmp = ids[i];
            ids[i] = ids[j];
            ids[j] = tmp;
            state.CardOrder = ids;
            state.Save();
            Render();
        }

        // ---------- вход в сервисы из карточки (Claude Code) ----------
        // Кнопка «Войти» запускает вход CLI без окна (`claude auth login`): CLI сам открывает браузер
        // и ждёт подтверждения. Пользователю остаётся нажать «Разрешить» в браузере; терминал не нужен.

        enum LoginState { None, Waiting, Failed }

        // Состояние входа одного сервиса и подменённый им снимок карточки.
        sealed class LoginSlot
        {
            public LoginState State;
            public CliLogin Login;
            public bool Cancelled;
            public string Code;   // код подтверждения из вывода CLI (если он его печатает)
            public ServiceSnapshot Overlaid;
            public string SavedMessage, SavedActionId, SavedActionLabel;
        }

        static readonly string[] LoginServices = { "claude" };
        const string LoginCancelSuffix = "-login-cancel", LoginWindowSuffix = "-login-window";
        readonly Dictionary<string, LoginSlot> logins = new Dictionary<string, LoginSlot>();

        LoginSlot Slot(string svc)
        {
            LoginSlot s;
            if (!logins.TryGetValue(svc, out s))
            {
                s = new LoginSlot();
                logins[svc] = s;
            }
            return s;
        }

        static string LoginArgs(string svc)
        {
            return svc == "claude" ? "auth login" : "login";
        }

        static string LoginName(string svc)
        {
            return "Claude";
        }

        string LoginExe(string svc)
        {
            return Cli.Resolve(cfg.Svc(svc).Command, svc);
        }

        // "<svc><suffix>" -> svc, если svc — сервис со входом из карточки
        static bool SplitLoginId(string id, string suffix, out string svc)
        {
            svc = null;
            if (id == null || !id.EndsWith(suffix, StringComparison.Ordinal)) return false;
            string name = id.Substring(0, id.Length - suffix.Length);
            if (Array.IndexOf(LoginServices, name) < 0) return false;
            svc = name;
            return true;
        }

        void RunAction(string id)
        {
            string svc;
            if (id == ClaudeProvider.LoginAction) StartLogin("claude");
            else if (SplitLoginId(id, LoginCancelSuffix, out svc)) CancelLogin(svc);
            else if (SplitLoginId(id, LoginWindowSuffix, out svc)) LoginInWindow(svc);
        }

        // Пока идёт вход (или он не удался), подменить пояснение и кнопку в карточке сервиса.
        void ApplyLoginOverlay()
        {
            foreach (var svc in LoginServices) ApplyLoginOverlay(svc);
        }

        void ApplyLoginOverlay(string svc)
        {
            var slot = Slot(svc);
            var snap = snaps.Find(s => s.Id == svc);
            if (slot.State == LoginState.None || snap == null)
            {
                // вход завершён или отменён: вернуть снимку его собственные пояснение и кнопку
                if (slot.Overlaid != null)
                {
                    slot.Overlaid.Message = slot.SavedMessage;
                    slot.Overlaid.ActionId = slot.SavedActionId;
                    slot.Overlaid.ActionLabel = slot.SavedActionLabel;
                    slot.Overlaid = null;
                }
                return;
            }
            if (slot.Overlaid != snap)
            {
                slot.Overlaid = snap;
                slot.SavedMessage = snap.Message;
                slot.SavedActionId = snap.ActionId;
                slot.SavedActionLabel = snap.ActionLabel;
            }
            if (slot.State == LoginState.Failed && string.IsNullOrEmpty(slot.SavedActionId))
            {
                slot.State = LoginState.None;   // свежий опрос показал, что вход уже есть
                slot.Overlaid = null;
                return;
            }
            snap.Attention = true;
            if (slot.State == LoginState.Waiting)
            {
                snap.Message = "Открылся браузер — подтвердите вход в " + LoginName(svc)
                    + ". Виджет обновится сам.";
                snap.ActionId = svc + LoginCancelSuffix;
                snap.ActionLabel = "Отмена";
            }
            else
            {
                snap.Message = "Вход не завершён. Можно повторить в отдельном окне — там же видна ссылка для входа.";
                snap.ActionId = svc + LoginWindowSuffix;
                snap.ActionLabel = "Войти через окно";
            }
        }

        void StartLogin(string svc)
        {
            var slot = Slot(svc);
            if (slot.State == LoginState.Waiting) return;
            string exe = LoginExe(svc);
            if (exe == null) return;
            slot.State = LoginState.Waiting;
            slot.Cancelled = false;
            slot.Code = null;
            var login = new CliLogin(exe, LoginArgs(svc));
            slot.Login = login;
            // колбэки приходят из фонового потока — в UI через Dispatcher
            login.CodeFound = code => Dispatcher.BeginInvoke(new Action(() =>
            {
                slot.Code = code;
                Render();
            }));
            login.Done = ok => Dispatcher.BeginInvoke(new Action(() =>
            {
                slot.Login = null;
                slot.State = ok || slot.Cancelled ? LoginState.None : LoginState.Failed;
                if (ok || slot.Cancelled) RefreshNow();
                else Render();
            }));
            Render();
            login.Start();
        }

        void CancelLogin(string svc)
        {
            var slot = Slot(svc);
            slot.Cancelled = true;
            var login = slot.Login;
            if (login != null) login.Cancel();   // только сам CLI: браузер пользователя не трогаем
        }

        // Запасной путь: видимое окно с тем же входом (на случай, когда браузер просит вставить код).
        void LoginInWindow(string svc)
        {
            string exe = LoginExe(svc);
            if (exe == null) return;
            Slot(svc).State = LoginState.None;
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c \"\"" + exe + "\" " + LoginArgs(svc) + "\"");
                psi.UseShellExecute = true;
                var p = Process.Start(psi);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { p.WaitForExit(10 * 60 * 1000); p.Dispose(); }
                    catch { }
                    Dispatcher.BeginInvoke(new Action(RefreshNow));
                });
            }
            catch (Exception ex)
            {
                Log.Write(svc + " login window: " + ex.Message);
            }
            Render();
        }

        // Обновить сразу; если опрос уже идёт — повторить после него.
        void RefreshNow()
        {
            if (!busy) { Refresh(); return; }
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (s, e) => { if (busy) return; t.Stop(); Refresh(); };
            t.Start();
        }

        // ---------- трей, автозапуск, вызов поверх окон ----------

        // Один раз: включить автозапуск и вывести значок из скрытой области трея. Дальше решает пользователь (меню).
        void FirstRunSetup()
        {
            // Autostart is an explicit tray-menu choice in the public distribution.
        }

        // Повторный запуск exe (ярлык) показывает эту копию поверх окон; запуск с --exit просит её завершиться.
        void ListenForRequests()
        {
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            var exit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
            var listener = new Thread(() =>
            {
                var handles = new WaitHandle[] { show, exit };
                while (true)
                {
                    if (WaitHandle.WaitAny(handles) == 1)
                    {
                        Dispatcher.BeginInvoke(new Action(Close));
                        return;
                    }
                    Dispatcher.BeginInvoke(new Action(Raise));
                }
            });
            listener.IsBackground = true;
            listener.Name = "request-listener";
            listener.Start();
        }

        // Карточку видно, если окно показано и либо оно активно, либо закреплено поверх всех окон.
        // Щелчок по значку в трее (и правый щелчок тоже) сначала снимает фокус с карточки — учитываем эти 400 мс.
        bool IsShownNow()
        {
            bool wasActive = IsActive || (DateTime.UtcNow - deactivatedAt).TotalMilliseconds < 400;
            return IsVisible && (wasActive || state.Topmost);
        }

        void ToggleShown()
        {
            if (IsShownNow()) HideToTray();
            else Raise();
        }

        void Raise()
        {
            if (!OnScreen(Left, Top)) PlaceDefault();
            if (!IsVisible) Show();
            if (state.Hidden)
            {
                state.Hidden = false;
                state.Save();
            }
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

        // Свернуть в трей: вернуть фокус окну, которое было активно до показа карточки, и скрыть окно.
        void HideToTray()
        {
            if (IsActive && previousForeground != IntPtr.Zero && Native.IsWindow(previousForeground) && Native.IsWindowVisible(previousForeground))
                Native.SetForegroundWindow(previousForeground);
            previousForeground = IntPtr.Zero;
            SavePosition();
            Hide();
            state.Hidden = true;
            state.Save();
        }

        // ---------- окно ----------

        void PlaceInitially()
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (state.Left.HasValue && state.Top.HasValue && OnScreen(state.Left.Value, state.Top.Value))
            {
                Left = state.Left.Value;
                Top = state.Top.Value;
            }
            else PlaceDefault();
        }

        void PlaceDefault()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - (CardView.CardWidth + 28) * state.Scale - 8;
            Top = wa.Top + 8;
        }

        static bool OnScreen(double x, double y)
        {
            double l = SystemParameters.VirtualScreenLeft, t = SystemParameters.VirtualScreenTop;
            return x + 80 > l && x + 80 < l + SystemParameters.VirtualScreenWidth
                && y + 30 > t && y + 30 < t + SystemParameters.VirtualScreenHeight;
        }

        void SavePosition()
        {
            state.Left = Left;
            state.Top = Top;
            state.Save();
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);   // не показывать в Alt+Tab
        }

        void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || state.Locked) return;
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            SavePosition();
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (tray != null) tray.Invalidate();
                Render();
            }));
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (tray != null) tray.Invalidate();
                if (!OnScreen(Left, Top)) PlaceDefault();
                Render();
            }));
        }

        // ---------- меню: правый щелчок по карточке или значку, кнопка ⋯ ----------

        void OpenMenu(FrameworkElement anchor, bool fromTray)
        {
            var t = theme ?? Theme.Resolve(state.Theme);
            var m = new ContextMenu();
            m.Style = MenuStyles.Menu(t);
            m.FontFamily = CardView.UiFont;
            m.FontSize = 12.5;
            var item = MenuStyles.Item(t);
            var sep = MenuStyles.Separator(t);

            if (fromTray)
            {
                bool up = IsShownNow();
                string key = hotkey != null && hotkey.Text != null ? "  (" + hotkey.Text + ")" : "";
                Add(m, item, null, up ? "Свернуть в трей" : "Показать" + key, () => { if (up) HideToTray(); else Raise(); });
                m.Items.Add(new Separator { Style = sep });
            }
            Add(m, item, null, "Обновить сейчас", Refresh);
            m.Items.Add(new Separator { Style = sep });
            Add(m, item, state.Topmost, "Всегда поверх окон", ToggleTopmost);
            Add(m, item, state.Locked, "Зафиксировать положение", () => { state.Locked = !state.Locked; state.Save(); });
            Add(m, item, state.Compact, "Компактный вид (сбросы — в подсказке)", () => { state.Compact = !state.Compact; state.Save(); Render(); });
            if (state.CardOrder.Count > 0)
                Add(m, item, null, "Сбросить порядок карточек", () => { state.CardOrder = new List<string>(); state.Save(); Render(); });
            Add(m, item, null, "Тема: " + ThemeName(state.Theme), CycleTheme);
            m.Resources["LW.Hover"] = t.Hover;   // подсветка кнопок − / +
            AddStepper(m, item, t, "Непрозрачность фона", () => Pct(state.Opacity), step =>
            {
                state.Opacity = Clamp(state.Opacity + step * 0.05, State.OpacityMin, 1.0);
                state.Save();
                Render();
            });
            AddStepper(m, item, t, "Размер", () => Pct(state.Scale), step =>
            {
                state.Scale = Clamp(state.Scale + step * 0.05, State.ScaleMin, State.ScaleMax);
                state.Save();
                Render();
            });
            m.Items.Add(new Separator { Style = sep });
            Add(m, item, Autostart.IsEnabled(), "Запускать вместе с Windows", ToggleAutostart);
            Add(m, item, null, "Открыть config.json", OpenConfig);
            Add(m, item, null, "Открыть папку виджета", () => Shell(Program.BaseDir));
            m.Items.Add(new Separator { Style = sep });
            Add(m, item, null, "Выход", Close);

            if (anchor != null && IsVisible)
            {
                m.PlacementTarget = anchor;
                m.Placement = PlacementMode.Bottom;
            }
            else
            {
                if (IsVisible) m.PlacementTarget = this;   // скрытое окно не может быть опорой меню
                else m.PlacementTarget = null;
                m.Placement = PlacementMode.MousePoint;
                if (fromTray)
                    // меню из трея должно стать активным окном, иначе не закроется по щелчку мимо
                    m.Opened += (s, e) =>
                    {
                        var src = PresentationSource.FromVisual(m) as HwndSource;
                        if (src != null) Native.SetForegroundWindow(src.Handle);
                    };
            }
            m.IsOpen = true;
        }

        static void Add(ContextMenu m, Style style, bool? check, string text, Action action)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (check == true) g.Children.Add(new TextBlock { Text = "✓", FontWeight = FontWeights.SemiBold });
            var label = new TextBlock { Text = text };
            Grid.SetColumn(label, 1);
            g.Children.Add(label);
            var mi = new MenuItem { Header = g, Style = style };
            mi.Click += (s, e) => action();
            m.Items.Add(mi);
        }

        // Строка меню «Название   [−] 80 % [+]»: кнопки меняют значение шагом 5 %, меню при этом не закрывается.
        static void AddStepper(ContextMenu m, Style style, Theme t, string text, Func<string> value, Action<int> change)
        {
            var g = new Grid { MinWidth = 250 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(label, 1);
            g.Children.Add(label);

            var shown = new TextBlock { Text = value(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(shown, FontNumeralAlignment.Tabular);
            Grid.SetColumn(shown, 3);
            g.Children.Add(shown);

            var minus = CardView.IconButton("", "Меньше", t, 24, 11);
            minus.Click += (s, e) => { change(-1); shown.Text = value(); };
            Grid.SetColumn(minus, 2);
            g.Children.Add(minus);

            var plus = CardView.IconButton("", "Больше", t, 24, 11);
            plus.Click += (s, e) => { change(+1); shown.Text = value(); };
            Grid.SetColumn(plus, 4);
            g.Children.Add(plus);

            m.Items.Add(new MenuItem { Header = g, Style = style, StaysOpenOnClick = true });
        }

        static double Clamp(double v, double min, double max)
        {
            v = Math.Round(v * 20) / 20;   // шаг 5 %, без накопления ошибки округления
            return Math.Max(min, Math.Min(max, v));
        }

        static string Pct(double v)
        {
            return Math.Round(v * 100) + Fmt.Nbsp + "%";
        }

        static string ThemeName(string mode)
        {
            return mode == "dark" ? "тёмная" : mode == "light" ? "светлая" : "как в Windows";
        }

        void ToggleTopmost()
        {
            state.Topmost = !state.Topmost;
            state.Save();
            Topmost = state.Topmost;
            Render();
        }

        void CycleTheme()
        {
            state.Theme = state.Theme == "auto" ? "light" : state.Theme == "light" ? "dark" : "auto";
            state.Save();
            Render();
        }

        void ToggleAutostart()
        {
            try
            {
                if (Autostart.IsEnabled()) Autostart.Disable();
                else Autostart.Enable(exePath);
            }
            catch (Exception ex)
            {
                Log.Write("autostart: " + ex.Message);
                MessageBox.Show("Не удалось изменить автозапуск: " + ex.Message, "Лимиты нейросетей");
            }
        }

        void OpenConfig()
        {
            string path = cfg.FilePath;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { Shell("notepad.exe", "\"" + path + "\""); }
        }

        void OpenUrl(string url)
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                Shell(url);
        }

        static void Shell(string target, string args = null)
        {
            try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("open: " + ex.Message); }
        }
    }

    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
        public const int ASFW_ANY = -1;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
        // панель задач и панель скрытых значков получают фокус от щелчка по значку в трее: возвращать фокус им незачем
        public static bool IsTaskbar(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            string c = sb.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland";
        }
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
        [DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hWnd);

        public static void AddExStyle(IntPtr h, int add)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(h, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr64(h, GWL_EXSTYLE).ToInt64() | (uint)add));
            else
                SetWindowLong32(h, GWL_EXSTYLE, GetWindowLong32(h, GWL_EXSTYLE) | add);
        }

        // Сделать окно активным. Если Windows отказала (пользователь как раз работал в другом окне),
        // на миг подключиться к очереди ввода активного окна — так система разрешает смену фокуса.
        public static void ForceForeground(IntPtr h)
        {
            if (GetForegroundWindow() == h || SetForegroundWindow(h) && GetForegroundWindow() == h) return;
            uint other = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            if (other == 0 || other == me || !AttachThreadInput(me, other, true)) return;
            try
            {
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally
            {
                AttachThreadInput(me, other, false);
            }
        }
    }
}

