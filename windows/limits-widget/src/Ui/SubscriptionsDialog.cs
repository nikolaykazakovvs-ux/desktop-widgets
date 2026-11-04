using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LimitsWidget
{
    // Настройка «Подписки…»: имя тарифа и ссылка профиля/подписки по каждому сервису.
    // Ручное имя главнее подтверждённого поля провайдера; пустое поле — вернуться к автоматическому значению.
    // Пишутся только services.<id>.plan и services.<id>.profileUrl; остальные поля config.json сохраняются как есть.
    sealed class SubscriptionsDialog : Window
    {
        sealed class Row
        {
            public string Id;
            public TextBox Plan, Profile;
            public TextBlock Error;
        }

        readonly string configPath;
        readonly List<Row> rows = new List<Row>();

        public SubscriptionsDialog(Config cfg, IList<ServiceSnapshot> snaps, Theme t)
        {
            configPath = cfg.FilePath;
            Title = "Подписки — Лимиты нейросетей";
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontFamily = CardView.UiFont;
            FontSize = 12.5;
            Background = new SolidColorBrush(t.MenuBg.Color);
            Foreground = t.Text;
            UseLayoutRounding = true;

            var root = new StackPanel { Margin = new Thickness(18, 14, 18, 14), Width = 460 };
            root.Children.Add(new TextBlock
            {
                Text = "Название тарифа: введённое вручную главнее определённого автоматически. Пустое поле — использовать автоматическое значение (если провайдер его сообщает).",
                TextWrapping = TextWrapping.Wrap, Foreground = t.Text2, Margin = new Thickness(0, 0, 0, 10)
            });

            int tab = 0;
            foreach (var id in cfg.KnownIds)
            {
                var svc = cfg.Svc(id);
                var snap = snaps != null ? snaps.FirstOrDefault(s => s.Id == id) : null;
                var row = new Row { Id = id };
                var box = new Border { BorderBrush = t.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 10), Margin = new Thickness(0, 0, 0, 8) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < 5; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                string head = svc.Title ?? id;
                if (!svc.Enabled) head += " (выключен в config.json)";
                var title = new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumnSpan(title, 2);
                g.Children.Add(title);

                string auto = snap != null && !string.IsNullOrEmpty(snap.AutoPlan)
                    ? snap.AutoPlan + " — " + (snap.AutoPlanSource ?? "поле провайдера")
                    : AutoHint(id);
                AddPair(g, 1, "Автоматически:", new TextBlock { Text = auto, TextWrapping = TextWrapping.Wrap, Foreground = t.Text2 }, t, null);

                row.Plan = new TextBox { Text = svc.Plan ?? "", MaxLength = 40, TabIndex = tab++, Margin = new Thickness(0, 2, 0, 2) };
                System.Windows.Automation.AutomationProperties.SetName(row.Plan, "Название тарифа " + (svc.Title ?? id) + " вручную");
                AddPair(g, 2, "Тариф _вручную:", row.Plan, t, row.Plan);

                string def = DefaultProfile(id);
                bool custom = !string.IsNullOrEmpty(svc.ProfileUrl) && svc.ProfileUrl != def;
                row.Profile = new TextBox { Text = custom ? svc.ProfileUrl : "", TabIndex = tab++, Margin = new Thickness(0, 2, 0, 2) };
                System.Windows.Automation.AutomationProperties.SetName(row.Profile, "Ссылка на профиль и подписку " + (svc.Title ?? id));
                AddPair(g, 3, "_Ссылка профиля:", row.Profile, t, row.Profile);

                row.Error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = t.Muted,
                    Text = def != null ? "Пусто — " + def : "Пусто — ссылки нет: страница профиля этого сервиса не подтверждена." };
                Grid.SetRow(row.Error, 4);
                Grid.SetColumn(row.Error, 1);
                g.Children.Add(row.Error);

                box.Child = g;
                root.Children.Add(box);
                rows.Add(row);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            var save = new Button { Content = "Сохранить", IsDefault = true, MinWidth = 96, Padding = new Thickness(10, 3, 10, 3), TabIndex = tab++ };
            var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 96, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0), TabIndex = tab++ };
            save.Click += (s, e) => Save(t);
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            Content = root;
            Loaded += (s, e) => { if (rows.Count > 0) Keyboard.Focus(rows[0].Plan); };
        }

        static void AddPair(Grid g, int row, string label, FrameworkElement value, Theme t, UIElement target)
        {
            var l = new Label { Content = label, Padding = new Thickness(0, 3, 8, 3), Foreground = t.Text2, VerticalAlignment = VerticalAlignment.Center, Target = target };
            Grid.SetRow(l, row);
            g.Children.Add(l);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            value.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(value);
        }

        // Что умеет определять сам виджет (только подтверждённые поля).
        internal static string AutoHint(string id)
        {
            switch (id)
            {
                case "codex": return "из поля planType ответа Codex (появится после опроса)";
                case "kimi": return "уровень аккаунта из /oauth/userinfo (появится после опроса); это не подтверждение оплаченного тарифа";
                default: return "не определяется: провайдер не сообщает тариф подтверждённым полем";
            }
        }

        internal static string DefaultProfile(string id)
        {
            var c = new Config();
            return PlanResolver.SafeUrl(c.Svc(id).ProfileUrl);
        }

        void Save(Theme t)
        {
            bool ok = true;
            foreach (var r in rows)
            {
                string url = r.Profile.Text.Trim();
                if (url.Length > 0 && PlanResolver.SafeUrl(url) == null)
                {
                    ok = false;
                    r.Error.Text = "Нужен адрес https://…";
                    r.Error.Foreground = new SolidColorBrush(t.Danger);
                    Keyboard.Focus(r.Profile);
                }
            }
            if (!ok) return;
            bool written;
            try
            {
                written = ConfigFile.Update(configPath, root =>
                {
                    foreach (var r in rows)
                    {
                        var o = ConfigFile.Service(root, r.Id);
                        ConfigFile.Set(o, "plan", r.Plan.Text);
                        ConfigFile.Set(o, "profileUrl", r.Profile.Text.Trim().Length > 0 ? PlanResolver.SafeUrl(r.Profile.Text) : null);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Write("subscriptions: " + ex.GetType().Name);
                written = false;
            }
            if (!written)
            {
                MessageBox.Show(this, "Не удалось сохранить config.json (файл не разобран или недоступен). Исправьте его вручную.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        }
    }
}
