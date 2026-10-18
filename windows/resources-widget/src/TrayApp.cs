using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ResourcesWidget
{
    // Связывает опрос датчиков, значок в трее, карточку и горячую клавишу.
    sealed partial class TrayApp : IDisposable
    {
        readonly Config cfg;
        readonly State state;
        readonly Sampler sampler;
        readonly CardWindow window;
        readonly TrayIcon tray;
        readonly Hotkey hotkey;
        readonly Dispatcher dispatcher;
        readonly string exePath;
        volatile Sample latest;
        int pending;

        public TrayApp(CliOptions opts)
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            exePath = Assembly.GetExecutingAssembly().Location;
            string statePath = opts.StatePath ?? Path.Combine(Program.BaseDir, "state.json");
            bool firstRun = !File.Exists(statePath);
            cfg = Config.Load(opts.ConfigPath ?? Path.Combine(Program.BaseDir, "config.json"));
            state = State.Load(statePath);

            sampler = new Sampler(cfg, Program.NetLogDir, false);
            sampler.ProcessSort = state.ProcessSort;
            var actions = new CardActions
            {
                Minimize = () => window.HideToTray(),
                TogglePin = TogglePin,
                CycleTheme = CycleTheme,
                Menu = anchor => OpenMenu(anchor, false),
                Sort = SetSort,
                Journal = () => sampler.Journal,
            };
            window = new CardWindow(state, cfg, actions, () => hotkey != null ? hotkey.Text : null);
            window.ShownChanged += shown => sampler.Detailed = shown;

            tray = new TrayIcon(cfg.NetworkBits);
            tray.LeftClick += () => window.ToggleShown();
            tray.RightClick += () => OpenMenu(null, true);

            hotkey = new Hotkey(cfg.Hotkey, () => window.ToggleShown());
            if (hotkey.Error != null)
            {
                Log.Write("hotkey: " + hotkey.Error);
                tray.Notify("Системные ресурсы", "Горячая клавиша не работает: " + hotkey.Error + ". Карточка открывается щелчком по значку в трее.");
            }

            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            ListenForShowRequests();

            sampler.Updated += OnSample;
            sampler.Start();

            if (firstRun)
            {
                // Первый запуск: включить автозапуск, чтобы значок был в трее после перезагрузки, и показать карточку.
                // Autostart is opt-in through the tray menu.

                state.Save();
            }
            // Как у «Лимитов нейросетей»: карточка стартует в том виде, в каком её оставили, — на экране или свёрнутой в трей.
            if (firstRun || opts.Show) window.Raise();
            else if (!state.Hidden) window.ShowCard(false);
            // Notification-area visibility is controlled by the user.
        }

        // Показания приходят из потока опроса; в интерфейс уходит только самое свежее.
        void OnSample(Sample s)
        {
            latest = s;
            if (Interlocked.Exchange(ref pending, 1) == 0)
                dispatcher.BeginInvoke(new Action(ApplyLatest));
        }

        void ApplyLatest()
        {
            Interlocked.Exchange(ref pending, 0);
            var s = latest;
            if (s == null) return;
            tray.Update(s);
            window.Apply(s);
        }

        // Повторный запуск exe (ярлык, автозагрузка) не открывает вторую копию, а показывает карточку этой.
        // Запуск с --exit просит эту копию завершиться (так делает build.ps1 перед сборкой).
        void ListenForShowRequests()
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
                        dispatcher.BeginInvoke(new Action(Exit));
                        return;
                    }
                    dispatcher.BeginInvoke(new Action(() => window.Raise()));
                }
            });
            listener.IsBackground = true;
            listener.Name = "show-listener";
            listener.Start();
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                tray.Invalidate();
                window.Rebuild();
            }));
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                tray.Invalidate();
                window.Rebuild();
            }));
        }

        void TogglePin()
        {
            window.SetTopmost(!state.Topmost);
        }

        // Кнопка темы в шапке и пункт меню: как в Windows → светлая → тёмная → как в Windows.
        void CycleTheme()
        {
            state.Theme = state.Theme == "auto" ? "light" : state.Theme == "light" ? "dark" : "auto";
            state.Save();
            window.Rebuild();
        }

        void SetSort(string sort)
        {
            state.ProcessSort = sort;
            sampler.ProcessSort = sort;
            state.Save();
            if (latest != null) window.Apply(latest);
        }

        // Windows 11 прячет новые значки в скрытую область трея. Один раз, при первом появлении значка,
        // выводим его на панель задач; дальше решение за пользователем (Параметры → Панель задач).
        void PromoteTrayIconOnce()
        {
            if (state.TrayPromoted) return;
            int attempts = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                attempts++;
                bool done = false;
                try { done = TrayPromotion.Promote(exePath); }
                catch (Exception ex) { Log.Write("tray promote: " + ex.Message); }
                if (!done && attempts < 15) return;
                timer.Stop();
                if (done)
                {
                    state.TrayPromoted = true;
                    state.Save();
                }
            };
            timer.Start();
        }

        public void Exit()
        {
            state.Hidden = !window.IsVisible;
            state.Save();
            window.Exiting = true;
            Application.Current.Shutdown();
        }

        void Restart()
        {
            try
            {
                Process.Start(new ProcessStartInfo(exePath, "--restart --show") { UseShellExecute = false, WorkingDirectory = Program.BaseDir });
            }
            catch (Exception ex)
            {
                Log.Write("restart: " + ex.Message);
                return;
            }
            Exit();
        }

        public void Dispose()
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            sampler.Updated -= OnSample;
            sampler.Dispose();
            hotkey.Dispose();
            tray.Dispose();
            window.Exiting = true;
            window.Close();
        }
    }

    static class TrayPromotion
    {
        // true — значок выведен на панель задач (или в этой версии Windows делать нечего).
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
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Системные ресурсы.lnk"); }
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
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "Системные ресурсы — виджет в трее" });
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

