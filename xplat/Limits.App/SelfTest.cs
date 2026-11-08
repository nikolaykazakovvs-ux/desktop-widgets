using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DesktopWidgets.Limits.Core;
using DesktopWidgets.Limits.Platform;
using CoreRect = DesktopWidgets.Limits.Core.Rect;

namespace DesktopWidgets.Limits.App
{
    // Самопроверка в реальном окне: CLI, окно, трей, мониторы, телеметрия, память, сон/пробуждение.
    // Результат — report.json и card.png в каталоге --selftest. Сон проверяется снаружи (SIGSTOP/SIGCONT, см. xplat/scripts).
    static class SelfTest
    {
        public static int ExitCode;

        static Dictionary<string, object> Mem(string stage)
        {
            var m = ProcessMemory.Sample();
            return new Dictionary<string, object> { { "stage", stage }, { "working_set_mb", Math.Round(m.WorkingSetMb, 1) },
                { "private_mb", Math.Round(m.PrivateMb, 1) }, { "managed_heap_mb", Math.Round(m.ManagedMb, 1) } };
        }

        public static async Task RunAsync(App app, Options o, IClassicDesktopStyleApplicationLifetime desktop)
        {
            var report = new Dictionary<string, object>();
            var checks = new List<object>();
            var memory = new List<object>();
            void Check(string name, bool pass, object detail) => checks.Add(new Dictionary<string, object> { { "name", name }, { "pass", pass }, { "detail", detail } });
            var total = Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(o.SelfTestDir);
                report["os"] = Os.Current.ToString();
                report["os_description"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                report["arch"] = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
                report["dotnet"] = Environment.Version.ToString();
                report["pid"] = Environment.ProcessId;
                File.WriteAllText(Path.Combine(o.SelfTestDir, "pid"), Environment.ProcessId.ToString());
                await Task.Delay(1500);
                memory.Add(Mem("started"));

                // 1. Первый опрос CLI
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (app.Controller.Last == null && DateTime.UtcNow < deadline) await Task.Delay(250);
                var s = app.Controller.Last;
                Check("cli_collect", s != null && (s.Status == SvcStatus.Ok || s.Status == SvcStatus.Fixture) && s.Bars.Any(b => !b.Placeholder),
                    s == null ? "нет результата" : new Dictionary<string, object> { { "status", s.Status.ToString() }, { "status_text", s.StatusText },
                        { "source", s.Source }, { "bars", s.Bars.Select(b => b.Label + " = " + (b.Used.HasValue ? b.Used.Value + "%" : "нет данных")).ToArray() },
                        { "plan", s.Plan }, { "duration_ms", (int)app.Controller.LastDuration.TotalMilliseconds }, { "message", s.Message },
                        { "resolved_cli", o.FixtureUsage != null ? "fixture" : app.Controller.Runner.Resolve(o.ClaudeCommand, "claude") } });
                await Task.Delay(500);
                memory.Add(Mem("after_first_collect"));

                // 2. Окно
                var card = app.Card;
                Check("window_visible", card.IsVisible && card.Bounds.Height > 50, new Dictionary<string, object> { { "position", card.Position.ToString() },
                    { "size_dip", card.Bounds.Size.ToString() }, { "render_scaling", card.RenderScaling }, { "desktop_scaling", card.DesktopScaling }, { "topmost", card.Topmost },
                    { "decorations", card.WindowDecorations.ToString() } });
                card.SetTopmost(false); bool off = !card.Topmost; card.SetTopmost(true);
                Check("topmost_toggle", off && card.Topmost, null);

                // 3. Мониторы
                var (areas, primary) = card.WorkAreas();
                report["screens"] = card.Screens.All.Select(sc => new Dictionary<string, object> { { "bounds", sc.Bounds.ToString() },
                    { "working_area", sc.WorkingArea.ToString() }, { "scaling", sc.Scaling }, { "primary", sc.IsPrimary }, { "name", sc.DisplayName } }).ToArray();
                var placed = card.Place();
                bool InsideAny(CoreRect r) => areas.Any(a => r.X >= a.X && r.Y >= a.Y && r.Right <= a.Right && r.Bottom <= a.Bottom);
                Check("placement_initial", InsideAny(placed), placed.ToString());
                // Сохранённая позиция на отключённом мониторе: далеко справа от всех экранов.
                int farX = areas.Max(a => a.Right) + 2000, farY = areas.Max(a => a.Bottom) + 500;
                app.State.SavePosition(farX, farY);
                var restored = card.Place();
                await Task.Delay(300);
                var actual = new CoreRect(card.Position.X, card.Position.Y, placed.W, placed.H);
                Check("placement_disconnected_monitor", InsideAny(restored) && InsideAny(actual),
                    new Dictionary<string, object> { { "saved", farX + "," + farY }, { "restored", restored.ToString() }, { "actual", actual.ToString() } });
                // Позиция на «втором мониторе» слева от основного (отрицательные координаты) — сохраняется, пока он подключён.
                var virtualAreas = new List<CoreRect>(areas) { new CoreRect(areas[primary].X - 1920, areas[primary].Y, 1920, 1080) };
                var onSecond = Placement.Fit(new CoreRect(areas[primary].X - 1500, areas[primary].Y + 100, placed.W, placed.H), virtualAreas, primary);
                Check("placement_virtual_second_monitor", onSecond.X == areas[primary].X - 1500, onSecond.ToString());

                // 4. Трей / строка меню
                var tray = app.Tray;
                Check("tray_created", tray != null && tray.IsVisible && tray.Menu != null && tray.Menu.Items.Count >= 4,
                    new Dictionary<string, object> { { "menu_items", tray?.Menu?.Items.Count }, { "tooltip", tray?.ToolTipText } });
                app.ToggleCard(); bool hidden = !card.IsVisible; app.ToggleCard();
                Check("tray_toggle_card", hidden && card.IsVisible, null);

                // 5. Снимок карточки
                await Task.Delay(500);
                string png = Path.Combine(o.SelfTestDir, "card.png");
                var size = new PixelSize((int)Math.Ceiling(card.Bounds.Width * 2), (int)Math.Ceiling(card.Bounds.Height * 2));
                using (var bmp = new RenderTargetBitmap(size, new Vector(192, 192)))
                {
                    bmp.Render(card);
                    bmp.Save(png);
                }
                Check("card_screenshot", File.Exists(png) && new FileInfo(png).Length > 1000, png);

                // 6. Телеметрия по контракту
                var sink = app.Controller.Publisher.Sink;
                deadline = DateTime.UtcNow.AddSeconds(15);
                while (sink.PublishedCount < 2 && DateTime.UtcNow < deadline) await Task.Delay(200);
                var tele = new Dictionary<string, object> { { "path", sink.TargetPath }, { "published", sink.PublishedCount }, { "last_error", sink.LastError } };
                if (File.Exists(sink.TargetPath))
                {
                    File.Copy(sink.TargetPath, Path.Combine(o.SelfTestDir, "quotas-snapshot.json"), true);
                    if (!OperatingSystem.IsWindows())
                    {
                        tele["file_mode"] = Convert.ToString((int)File.GetUnixFileMode(sink.TargetPath), 8);
                        tele["dir_mode"] = Convert.ToString((int)File.GetUnixFileMode(Path.GetDirectoryName(sink.TargetPath)), 8);
                    }
                }
                Check("telemetry_published", sink.PublishedCount >= 1 && sink.LastError == null && File.Exists(sink.TargetPath), tele);

                // 7. Ожидание: память в простое и окно для внешней проверки сна (SIGSTOP/SIGCONT)
                File.WriteAllText(Path.Combine(o.SelfTestDir, "ready"), "");
                int idle = Math.Max(5, o.SelfTestSeconds);
                var idleWatch = Stopwatch.StartNew();
                while (idleWatch.Elapsed.TotalSeconds < idle) await Task.Delay(1000);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(1000);
                memory.Add(Mem("idle_after_gc"));
                report["wake"] = new Dictionary<string, object> { { "wake_events", app.Controller.Wake.WakeCount }, { "collections", app.Controller.Collections } };
                report["memory"] = memory;
                ExitCode = checks.All(c => (bool)((Dictionary<string, object>)c)["pass"]) ? 0 : 1;
            }
            catch (Exception ex)
            {
                report["exception"] = ex.ToString();
                ExitCode = 3;
            }
            report["checks"] = checks;
            report["elapsed_s"] = Math.Round(total.Elapsed.TotalSeconds, 1);
            File.WriteAllText(Path.Combine(o.SelfTestDir, "report.json"), Json.Serialize(report, true));
            Dispatcher.UIThread.Post(() => desktop.Shutdown(ExitCode));
        }
    }
}
