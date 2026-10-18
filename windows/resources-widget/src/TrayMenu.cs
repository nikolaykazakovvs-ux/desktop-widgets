using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;

namespace ResourcesWidget
{
    // Меню: правый щелчок по значку в трее или кнопка ⋯ на карточке.
    sealed partial class TrayApp
    {
        bool cardShownForMenu;   // карточку показали только на время настройки из меню трея

        void OpenMenu(FrameworkElement anchor, bool fromTray)
        {
            var t = window.CurrentTheme;
            var m = new ContextMenu();
            m.Style = MenuStyles.Menu(t);
            m.FontFamily = Ui.Font;
            m.FontSize = 12.5;
            var item = MenuStyles.Item(t);
            var sep = MenuStyles.Separator(t);

            if (fromTray)
            {
                bool up = window.IsShownNow();
                string key = hotkey != null && hotkey.Text != null ? "  (" + hotkey.Text + ")" : "";
                Add(m, item, null, up ? "Свернуть в трей" : "Показать" + key, () =>
                {
                    cardShownForMenu = false;
                    if (up) window.HideToTray();
                    else window.Raise();
                });
                m.Items.Add(new Separator { Style = sep });
            }
            Add(m, item, state.Topmost, "Всегда поверх окон", TogglePin);
            Add(m, item, state.Locked, "Зафиксировать положение", () => { state.Locked = !state.Locked; state.Save(); });
            if (!state.Docked) Add(m, item, null, "Вернуть к трею", window.Redock);

            var sections = Submenu(m, item, "Разделы");
            foreach (string id in State.SectionIds)
            {
                string sid = id;
                Add(sections, item, state.Section(sid), SectionName(sid), () =>
                {
                    state.SetSection(sid, !state.Section(sid));
                    state.Save();
                    window.Rebuild();
                });
            }
            Add(m, item, state.Compact, "Компактный вид (без графиков)", () => { state.Compact = !state.Compact; SaveAndRebuild(); });
            Add(m, item, state.Columns == 2, "В две колонки", () => { state.Columns = state.Columns == 2 ? 1 : 2; SaveAndRebuild(); });
            Add(m, item, null, "Тема: " + ThemeName(state.Theme), CycleTheme);
            m.Resources["RW.Hover"] = t.Hover;   // подсветка кнопок − / +
            var tipStyle = MenuStyles.ToolTip(t);
            AddStepper(m, item, t, tipStyle, "Непрозрачность фона", () => Pct(state.Opacity), step =>
            {
                state.Opacity = Step(state.Opacity, step, State.OpacityMin, 1.0);
                PreviewLook(m);
            });
            AddStepper(m, item, t, tipStyle, "Размер", () => Pct(state.Scale), step =>
            {
                state.Scale = Step(state.Scale, step, State.ScaleMin, State.ScaleMax);
                PreviewLook(m);
            });
            m.Closed += (s, e) =>
            {
                // карточку показывали только на время настройки — убрать обратно
                if (!cardShownForMenu) return;
                cardShownForMenu = false;
                window.HideToTray();
            };

            m.Items.Add(new Separator { Style = sep });
            Add(m, item, null, "Диспетчер задач", () => Shell("taskmgr.exe"));
            Add(m, item, null, "Монитор ресурсов", () => Shell("resmon.exe"));
            var last = latest;
            m.Items.Add(new Separator { Style = sep });
            Add(m, item, Autostart.IsEnabled(), "Запускать вместе с Windows", ToggleAutostart);
            if (Program.SetupPath != null)
            {
                Add(m, item, null, "Установить или обновить локальный датчик температуры…", () => LocalSensorAction(false));
                Add(m, item, null, "Удалить локальный датчик температуры…", () => LocalSensorAction(true));
            }
            Add(m, item, null, "Открыть config.json", OpenConfig);
            Add(m, item, null, "Перезапустить (применить config.json)", Restart);
            m.Items.Add(new Separator { Style = sep });
            Add(m, item, null, "Выход", Exit);

            if (anchor != null)
            {
                m.PlacementTarget = anchor;
                m.Placement = PlacementMode.Bottom;
            }
            else
            {
                m.Placement = PlacementMode.MousePoint;
                if (!fromTray && window.IsVisible) m.PlacementTarget = window;   // правый щелчок по карточке
                else
                    // меню из трея должно стать активным окном, иначе не закроется по щелчку мимо
                    m.Opened += (s, e) =>
                    {
                        var src = PresentationSource.FromVisual(m) as HwndSource;
                        if (src != null) Native.SetForegroundWindow(src.Handle);
                    };
            }
            m.IsOpen = true;
        }

        void SaveAndRebuild()
        {
            state.Save();
            window.Rebuild();
        }

        // Прозрачность и размер видно сразу. Если карточка скрыта (меню открыто из трея), она показывается
        // под меню без перехвата фокуса, а меню поднимается обратно наверх.
        void PreviewLook(ContextMenu m)
        {
            state.Save();
            if (window.IsVisible)
            {
                window.Rebuild();
                return;
            }
            window.ShowCard(false);
            cardShownForMenu = true;
            var src = PresentationSource.FromVisual(m) as HwndSource;
            if (src != null) Native.BringToTop(src.Handle);
        }

        // Строка меню «Название   [−] 80 % [+]»: кнопки меняют значение шагом 5 %, меню при этом не закрывается.
        static void AddStepper(ContextMenu m, Style style, Theme t, Style tipStyle, string text, Func<string> value, Action<int> change)
        {
            var g = Ui.Columns(Ui.Px(18), Ui.Star, Ui.Auto, Ui.Px(48), Ui.Auto);
            g.MinWidth = 250;
            g.Children.Add(Ui.At(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }, 1));

            var shown = new TextBlock { Text = value(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(shown, FontNumeralAlignment.Tabular);
            g.Children.Add(Ui.At(shown, 3));

            var minus = Ui.IconButton("", "Меньше", t, tipStyle, 24, 11);
            AutomationProperties.SetName(minus, text + ": меньше");
            minus.Click += (s, e) => { change(-1); shown.Text = value(); };
            g.Children.Add(Ui.At(minus, 2));

            var plus = Ui.IconButton("", "Больше", t, tipStyle, 24, 11);
            AutomationProperties.SetName(plus, text + ": больше");
            plus.Click += (s, e) => { change(+1); shown.Text = value(); };
            g.Children.Add(Ui.At(plus, 4));

            m.Items.Add(new MenuItem { Header = g, Style = style, StaysOpenOnClick = true });
        }

        static double Step(double v, int step, double min, double max)
        {
            v = Math.Round((v + step * 0.05) * 20) / 20;   // шаг 5 %, без накопления ошибки округления
            return Math.Max(min, Math.Min(max, v));
        }

        static MenuItem Submenu(ItemsControl parent, Style style, string text)
        {
            var mi = new MenuItem { Header = Label(null, text), Style = style };
            parent.Items.Add(mi);
            return mi;
        }

        static void Add(ItemsControl parent, Style style, bool? check, string text, Action action)
        {
            var mi = new MenuItem { Header = Label(check, text), Style = style };
            mi.Click += (s, e) => action();
            parent.Items.Add(mi);
        }

        static Grid Label(bool? check, string text)
        {
            var g = Ui.Columns(Ui.Px(18), Ui.Auto);
            if (check == true) g.Children.Add(new TextBlock { Text = "✓", FontWeight = FontWeights.SemiBold });
            g.Children.Add(Ui.At(new TextBlock { Text = text }, 1));
            return g;
        }

        static string SectionName(string id)
        {
            switch (id)
            {
                case "cpu": return "Процессор";
                case "memory": return "Память";
                case "gpu": return "Видеокарта";
                case "disks": return "Диски";
                case "network": return "Сеть";
                default: return "Процессы";
            }
        }

        static string ThemeName(string mode)
        {
            return mode == "dark" ? "тёмная" : mode == "light" ? "светлая" : "как в Windows";
        }

        static string Pct(double v)
        {
            return Math.Round(v * 100) + Fmt.Nbsp + "%";
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
                MessageBox.Show("Не удалось изменить автозапуск: " + ex.Message, "Системные ресурсы");
            }
        }

        void OpenConfig()
        {
            string path = cfg.FilePath;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { Shell("notepad.exe", "\"" + path + "\""); }
        }

        static void LocalSensorAction(bool remove)
        {
            try { Process.Start(new ProcessStartInfo(Program.SetupPath, remove ? "--sensor-remove" : "--sensor") { UseShellExecute = true, WorkingDirectory = Program.BaseDir }); }
            catch (Exception ex) { MessageBox.Show("Не удалось открыть установку локального датчика: " + ex.Message, "Системные ресурсы"); }
        }

        static void Shell(string target, string args = null)
        {
            try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("open: " + ex.Message); }
        }
    }
}
