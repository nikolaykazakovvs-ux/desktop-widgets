using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace DesktopWidgets.Limits.App
{
    public sealed class App : Application
    {
        public Controller Controller { get; private set; }
        public CardWindow Card { get; private set; }
        public TrayIcon Tray { get; private set; }
        public StateStore State { get; private set; }
        NativeMenuItem topmostItem;

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Name = "Лимиты нейросетей";
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime;
            var o = Program.Options;
            Controller = new Controller(o);
            State = new StateStore(Controller.Paths.ConfigDirectory);
            Card = new CardWindow(State);
            Controller.Updated += s => Card.Render(s);
            Card.Opened += (_, __) => Dispatcher.UIThread.Post(() => Card.Place(), DispatcherPriority.Background);
            // Смена конфигурации мониторов (отключение, смена разрешения) — вернуть карточку в видимую область.
            Card.Screens.Changed += (_, __) => Dispatcher.UIThread.Post(() => Card.Place(), DispatcherPriority.Background);
            Controller.WokeUp += _ => Dispatcher.UIThread.Post(() => Card.Place());

            Tray = BuildTray(desktop);
            if (!State.Hidden || o.SelfTestDir != null) Card.Show();
            desktop.Exit += (_, __) => { State.Save(); Controller.Dispose(); Tray?.Dispose(); };
            if (o.SelfTestDir != null) _ = SelfTest.RunAsync(this, o, desktop);
            base.OnFrameworkInitializationCompleted();
        }

        TrayIcon BuildTray(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var menu = new NativeMenu();
            var toggle = new NativeMenuItem("Показать / скрыть карточку");
            toggle.Click += (_, __) => ToggleCard();
            var refresh = new NativeMenuItem("Обновить сейчас");
            refresh.Click += (_, __) => _ = Controller.RefreshAsync();
            topmostItem = new NativeMenuItem("Поверх окон") { ToggleType = MenuItemToggleType.CheckBox, IsChecked = State.Topmost };
            topmostItem.Click += (_, __) => { Card.SetTopmost(!Card.Topmost); topmostItem.IsChecked = Card.Topmost; };
            var quit = new NativeMenuItem("Выход");
            quit.Click += (_, __) => desktop.Shutdown();
            menu.Items.Add(toggle);
            menu.Items.Add(refresh);
            menu.Items.Add(topmostItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            var tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LimitsXplat/Assets/LimitsWidget.ico"))),
                ToolTipText = "Лимиты нейросетей",
                Menu = menu,
                IsVisible = true
            };
            tray.Clicked += (_, __) => ToggleCard();   // Windows/Linux: щелчок по значку; на macOS открывается меню
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            return tray;
        }

        public void ToggleCard()
        {
            if (Card.IsVisible) Card.Hide();
            else { Card.Show(); Card.Place(); Card.Activate(); }
            State.Hidden = !Card.IsVisible;
            State.Save();
        }
    }
}
