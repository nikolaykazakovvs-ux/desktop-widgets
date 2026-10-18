using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ResourcesWidgetSetup
{
    // Установка виджета для текущего пользователя: без прав администратора, в %LOCALAPPDATA%\Programs\ResourcesWidget.
    // Повторный запуск обновляет виджет и сохраняет config.json и state.json.
    static class Installer
    {
        public sealed class Settings
        {
            public string Dir;
            public bool Desktop = true, StartMenu = true, Autostart, Register = true, Launch = true;
        }

        public static string DefaultDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ResourcesWidget"); }
        }

        // Папка прежней установки, если виджет уже стоит.
        public static string ExistingDir()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Program.UninstallKey))
            {
                string dir = k != null ? k.GetValue("InstallLocation") as string : null;
                return dir != null && File.Exists(Path.Combine(dir, "ResourcesWidget.exe")) ? dir : null;
            }
        }

        public static int Start(Options o)
        {
            if (o.Quiet)
            {
                var s = new Settings
                {
                    Dir = o.Dir ?? ExistingDir() ?? DefaultDir,
                    Desktop = !o.NoDesktop,
                    StartMenu = !o.NoStartMenu,
                    Autostart = !o.NoAutostart && (o.Autostart || string.Equals(Shortcut.Target(Shortcut.Startup), Path.Combine(o.Dir ?? ExistingDir() ?? DefaultDir, "ResourcesWidget.exe"), StringComparison.OrdinalIgnoreCase)),
                    Register = !o.NoRegister,
                    Launch = !o.NoLaunch,
                };
                Install(s);
                return 0;
            }
            if (!Program.HasNet48() && MessageBox.Show("Не найдена .NET Framework 4.8, без неё виджет не запустится.\nОна входит в Windows 10 начиная с версии 1903 и в Windows 11; для старых версий — dotnet.microsoft.com/download/dotnet-framework/net48.\n\nВсё равно установить?",
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return 2;
            var form = new InstallForm(o);
            Application.Run(form);
            return form.ExitCode;
        }

        public static void Install(Settings s)
        {
            string dir = InstallDirectory.ValidateInstall(s.Dir);
            string exe = Path.Combine(dir, "ResourcesWidget.exe");
            Log.Write("Папка: " + dir);
            Widget.Close(dir);
            InstallFiles(dir);

            string setup = Path.Combine(dir, "Setup.exe");
            Log.Write("Ярлыки");
            const string desc = "Системные ресурсы — виджет в трее";
            if (s.StartMenu) Shortcut.Create(Shortcut.StartMenu, exe, "--show", desc);
            if (s.Desktop) Shortcut.Create(Shortcut.Desktop, exe, "--show", desc);
            if (s.Autostart) Shortcut.Create(Shortcut.Startup, exe, null, desc);
            else if (string.Equals(Shortcut.Target(Shortcut.Startup), exe, StringComparison.OrdinalIgnoreCase) && File.Exists(Shortcut.Startup)) File.Delete(Shortcut.Startup);
            if (s.Register) Register(dir, exe, setup);
            if (s.Launch)
            {
                Log.Write("Запускаю виджет");
                Process.Start(new ProcessStartInfo(exe, "--show") { UseShellExecute = false, WorkingDirectory = dir }).Dispose();
            }
            Log.Write("Виджет установлен");
        }

        // Same filesystem operations used in real install and isolated regression tests.
        public static void InstallFiles(string path)
        {
            string dir = InstallDirectory.ValidateInstall(path);
            string exe = Path.Combine(dir, "ResourcesWidget.exe");
            Directory.CreateDirectory(dir);
            InstallDirectory.MarkOwned(dir);

            Log.Write("Копирую виджет");
            Payload.Extract(Payload.Widget, exe);
            Payload.Extract(Payload.Notice, Path.Combine(dir, "THIRD-PARTY.txt"));
            string config = Path.Combine(dir, "config.json");
            if (File.Exists(config)) Log.Write("config.json оставлен прежний");
            else Payload.Extract(Payload.Config, config);
            // Пустой state.json: автозапуском управляет установщик, виджет при первом старте его не включает.
            string state = Path.Combine(dir, "state.json");
            if (!File.Exists(state)) File.WriteAllText(state, "{}", new UTF8Encoding(false));
            string setup = Path.Combine(dir, "Setup.exe");
            if (!string.Equals(Path.GetFullPath(Program.SelfPath), setup, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(Program.SelfPath, setup + ".new", true);
                Payload.Replace(setup + ".new", setup);
            }

        }

        // Запись в «Установленных приложениях» (для текущего пользователя).
        static void Register(string dir, string exe, string setup)
        {
            long bytes = 0;
            foreach (var f in new DirectoryInfo(dir).GetFiles()) bytes += f.Length;
            using (var k = Registry.CurrentUser.CreateSubKey(Program.UninstallKey))
            {
                k.SetValue("DisplayName", Program.AppName);
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", Options.Quote(setup) + " --uninstall");
                k.SetValue("QuietUninstallString", Options.Quote(setup) + " --uninstall --quiet");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

    }

    sealed class InstallForm : Form
    {
        readonly Options opts;
        readonly TextBox dirBox = new TextBox { ReadOnly = true, Width = 380 };
        readonly Button browse = new Button { Text = "Изменить…", AutoSize = true };
        readonly CheckBox desktop = new CheckBox { Text = "Ярлык на рабочем столе", Checked = true, AutoSize = true };
        readonly CheckBox autostart = new CheckBox { Text = "Запускать вместе с Windows", Checked = false, AutoSize = true };
        readonly Label sensorNote = new Label { AutoSize = true, MaximumSize = new Size(470, 0), Padding = new Padding(18, 0, 0, 4) };
        readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(470, 0), Padding = new Padding(0, 10, 0, 6) };
        readonly Button go = new Button { AutoSize = true, MinimumSize = new Size(100, 0) };
        readonly Button cancel = new Button { Text = "Отмена", AutoSize = true, MinimumSize = new Size(90, 0) };
        bool done;
        public int ExitCode = 2;

        public InstallForm(Options o)
        {
            opts = o;
            string existing = Installer.ExistingDir();
            Text = Program.AppName + " " + Program.Version + (existing != null ? " — обновление" : " — установка");
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);
            try { Icon = Icon.ExtractAssociatedIcon(Program.SelfPath); }
            catch { }

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            var title = new Label { Text = Program.AppName, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold) };
            root.Controls.Add(title);
            root.Controls.Add(new Label
            {
                Text = "Виджет в трее: процессор, память, видеокарта, диски, сеть и процессы. Карточка открывается сочетанием Ctrl+Alt+R или щелчком по значку. Права администратора для виджета не нужны.",
                AutoSize = true, MaximumSize = new Size(470, 0), Padding = new Padding(0, 4, 0, 12),
            });
            root.Controls.Add(new Label { Text = "Папка:", AutoSize = true });
            var dirRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 10) };
            dirBox.Text = o.Dir ?? existing ?? Installer.DefaultDir;
            autostart.Checked = !o.NoAutostart && (o.Autostart || string.Equals(Shortcut.Target(Shortcut.Startup), Path.Combine(dirBox.Text, "ResourcesWidget.exe"), StringComparison.OrdinalIgnoreCase));
            dirRow.Controls.Add(dirBox);
            dirRow.Controls.Add(browse);
            root.Controls.Add(dirRow);
            root.Controls.Add(desktop);
            root.Controls.Add(autostart);

            sensorNote.Text = "Температура процессора доступна от уже настроенных HWiNFO, AIDA64 или Core Temp. Этот выпуск не устанавливает датчики или драйверы.";
            sensorNote.ForeColor = SystemColors.GrayText;
            root.Controls.Add(sensorNote);
            root.Controls.Add(status);
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
            go.Text = existing != null ? "Обновить" : "Установить";
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(go);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = go;
            CancelButton = cancel;
            ActiveControl = go;
            dirBox.TabStop = false;

            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Папка для виджета", SelectedPath = dirBox.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) dirBox.Text = d.SelectedPath;
            };
            cancel.Click += (s, e) => Close();
            go.Click += (s, e) =>
            {
                if (done) Close();
                else Start();
            };
            Log.Line += line => BeginInvokeSafe(() => status.Text = line);

        }

        void Start()
        {
            var settings = new Installer.Settings
            {
                Dir = dirBox.Text,
                Desktop = desktop.Checked,
                Autostart = autostart.Checked,
                Register = !opts.NoRegister,
                Launch = !opts.NoLaunch,
                StartMenu = !opts.NoStartMenu,
            };
            foreach (Control c in new Control[] { go, cancel, browse, desktop, autostart }) c.Enabled = false;
            var t = new Thread(() =>
            {
                string result;
                try
                {
                    Installer.Install(settings);
                    result = "Готово: виджет запущен — значок в трее, карточка по Ctrl+Alt+R.";

                    ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Log.Write("ошибка: " + ex);
                    result = "Не удалось установить: " + ex.Message;
                    ExitCode = 1;
                }
                BeginInvokeSafe(() =>
                {
                    status.Text = result;
                    done = ExitCode == 0;
                    go.Text = done ? "Закрыть" : "Повторить";
                    go.Enabled = true;
                    cancel.Enabled = !done;
                    if (!done) foreach (Control c in new Control[] { browse, desktop, autostart }) c.Enabled = true;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        void BeginInvokeSafe(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
