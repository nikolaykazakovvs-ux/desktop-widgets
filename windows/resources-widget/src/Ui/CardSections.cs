using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ResourcesWidget
{
    // Процессор, память, видеокарта.
    sealed partial class Card
    {
        FrameworkElement Cpu(Sample s)
        {
            var c = s.Cpu;
            TextBlock value;
            var panel = new StackPanel { Background = Brushes.Transparent };
            panel.Children.Add(TitleRow(Glyph.Cpu, t.Cpu, "Процессор", c.Name, out value));
            string baseTip = (c.Name ?? "Процессор") + "\n" + c.Logical + " " + Fmt.Plural(c.Logical, "логический процессор", "логических процессора", "логических процессоров")
                + (c.BaseMhz > 0 ? "\nбазовая частота " + Fmt.Ghz(c.BaseMhz) : "");
            var tip = Ui.Tip(panel, baseTip, tipStyle);
            string shownTip = baseTip;

            Meter meter = null;
            Sparkline spark = null;
            CoreBars cores = null;
            TextBlock details = null;
            if (state.Compact)
            {
                meter = new Meter(t) { Margin = new Thickness(0, 8, 0, 0) };
                panel.Children.Add(meter);
            }
            else
            {
                spark = new Sparkline(t, t.Cpu, null, cfg.HistoryPoints) { Height = 36, Margin = new Thickness(0, 9, 0, 0) };
                cores = new CoreBars(t, t.Cpu) { Height = 16, Margin = new Thickness(0, 5, 0, 0) };
                details = Ui.Small(t.Text2, new Thickness(0, 7, 0, 0));
                panel.Children.Add(spark);
                panel.Children.Add(cores);
                panel.Children.Add(details);
            }
            updaters.Add(x =>
            {
                var v = x.Cpu;
                value.Text = Fmt.Percent(v.Total);
                value.Foreground = Level(v.Total, 75, 90);
                if (meter != null) meter.Set(v.Total, t.Level(v.Total, 75, 90, t.Cpu));
                if (spark != null) spark.Set(x.CpuHistory, null, 100);
                if (cores != null) cores.Set(v.Cores);
                if (details != null) CpuDetails(details, v, x.Mem);
                string tipText = baseTip + "\n" + TempTip(v);
                if (tipText != shownTip)
                {
                    shownTip = tipText;
                    tip.Content = tipText;
                }
            });
            return panel;
        }

        // "3,81 ГГц · 52 °C · 1 034 процесса · 14 795 потоков"; температура подсвечивается от 80 °C.
        void CpuDetails(TextBlock tb, CpuInfo c, MemInfo m)
        {
            tb.Inlines.Clear();
            if (c.Mhz.HasValue)
            {
                string f = Fmt.Ghz(c.Mhz.Value);
                if (c.MaxMhz.HasValue && c.MaxMhz.Value - c.Mhz.Value >= 50) f += ", пик " + Fmt.Num(c.MaxMhz.Value / 1000, 2);
                tb.Inlines.Add(new Run(f + " · "));
            }
            if (c.Temp.HasValue)
            {
                var run = new Run(Fmt.Num(c.Temp.Value, 0) + Fmt.Nbsp + "°C");
                if (c.Temp.Value >= 80) run.Foreground = c.Temp.Value >= 90 ? dangerBrush : warnBrush;
                tb.Inlines.Add(run);
            }
            else tb.Inlines.Add(new Run("—" + Fmt.Nbsp + "°C") { Foreground = t.Muted });
            if (m != null)
                tb.Inlines.Add(new Run(" · " + Fmt.Int(m.Processes) + Fmt.Nbsp + Fmt.Plural(m.Processes, "процесс", "процесса", "процессов")
                    + " · " + Fmt.Int(m.Threads) + Fmt.Nbsp + Fmt.Plural(m.Threads, "поток", "потока", "потоков")));
        }

        static string TempTip(CpuInfo c)
        {
            if (c.Temp.HasValue) return "температура " + Fmt.Num(c.Temp.Value, 0) + Fmt.Nbsp + "°C — из " + c.TempSource;
            return "Температура: нет данных. Windows не отдаёт её обычным программам — виджет берёт её\nу HWiNFO, AIDA64, Core Temp или LibreHardwareMonitor, пока одна из них запущена.\nУ LibreHardwareMonitor должен быть включён веб-сервер: Options → Remote Web Server → Run."
                + (Program.SetupPath != null ? "\nУстановить или запустить LibreHardwareMonitor: меню ⋯ → «Датчик температуры процессора…»." : "");
        }

        FrameworkElement Memory(Sample s)
        {
            TextBlock value;
            var panel = new StackPanel();
            panel.Children.Add(TitleRow(Glyph.Memory, t.Memory, "Память", Fmt.Size(s.Mem.Total), out value));
            var meter = new Meter(t) { Margin = new Thickness(0, 9, 0, 0) };
            panel.Children.Add(meter);
            TextBlock used = null, commit = null;
            if (!state.Compact)
            {
                used = Ui.Small(t.Text2, new Thickness(0, 7, 0, 0));
                commit = Ui.Small(t.Muted, new Thickness(0, 2, 0, 0));
                panel.Children.Add(used);
                panel.Children.Add(commit);
                Ui.Tip(commit, "Выделено — память, которую программы запросили у системы (ОЗУ + файл подкачки).\nКэш — файлы в памяти; система отдаёт его программам по первому требованию.", tipStyle);
            }
            updaters.Add(x =>
            {
                var m = x.Mem;
                if (m == null) return;
                value.Text = Fmt.Percent(m.Percent);
                value.Foreground = Level(m.Percent, 80, 90);
                meter.Set(m.Percent, t.Level(m.Percent, 80, 90, t.Memory));
                if (used == null) return;
                used.Text = "занято " + Fmt.SizePair(m.Used, m.Total) + " · доступно " + Fmt.Size(m.Available);
                commit.Text = "выделено " + Fmt.SizePair(m.CommitTotal, m.CommitLimit) + " · кэш " + Fmt.Size(m.Cache);
            });
            return panel;
        }

        FrameworkElement Gpu(Sample s)
        {
            TextBlock value;
            var panel = new StackPanel { Background = Brushes.Transparent };
            panel.Children.Add(TitleRow(Glyph.Gpu, t.Gpu, "Видеокарта", s.Gpu.Name, out value));
            var tip = Ui.Tip(panel, GpuTip(s.Gpu), tipStyle);
            string shownTip = null;

            Meter meter = null, vram = null;
            Sparkline spark = null;
            TextBlock vramText = null, details = null;
            if (state.Compact)
            {
                meter = new Meter(t) { Margin = new Thickness(0, 8, 0, 0) };
                panel.Children.Add(meter);
            }
            else
            {
                spark = new Sparkline(t, t.Gpu, null, cfg.HistoryPoints) { Height = 36, Margin = new Thickness(0, 9, 0, 0) };
                panel.Children.Add(spark);
                var row = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);
                row.Margin = new Thickness(0, 8, 0, 0);
                row.Children.Add(Ui.Text(s.Gpu.MemShared ? "общая память" : "видеопамять", 10.5, t.Text2));
                vram = new Meter(t) { Margin = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(Ui.At(vram, 1));
                vramText = Ui.Number(10.5, t.Text2, false);
                row.Children.Add(Ui.At(vramText, 2));
                panel.Children.Add(row);
                details = Ui.Small(t.Muted, new Thickness(0, 6, 0, 0));
                panel.Children.Add(details);
            }
            updaters.Add(x =>
            {
                var g = x.Gpu;
                if (g == null) return;
                value.Text = Fmt.Percent(g.Load);
                value.Foreground = Level(g.Load, 80, 95);
                if (meter != null) meter.Set(g.Load, t.Level(g.Load, 80, 95, t.Gpu));
                if (spark != null) spark.Set(x.GpuHistory, null, 100);
                if (vram != null)
                {
                    vram.Set(g.MemPercent, t.Level(g.MemPercent, 85, 95, t.Gpu));
                    vramText.Text = Fmt.SizePair(g.MemUsed, g.MemTotal);
                }
                if (details != null) GpuDetails(details, g);
                string tipText = GpuTip(g);
                if (tipText != shownTip)
                {
                    shownTip = tipText;
                    tip.Content = tipText;
                }
            });
            return panel;
        }

        static string GpuTip(GpuInfo g)
        {
            string text = (g.FullName ?? g.Name) + (g.Integrated ? " (встроенная)" : "");
            if (g.MemShared) text += "\nзанимает общей памяти " + Fmt.SizePair(g.MemUsed, g.MemTotal);
            else
            {
                text += "\nвидеопамять " + Fmt.SizePair(g.MemUsed, g.MemTotal);
                if (g.SharedUsed > 0) text += "\nобщая память " + Fmt.Size(g.SharedUsed);
            }
            if (g.ClockMhz.HasValue) text += "\nчастота " + Fmt.Int((long)Math.Round(g.ClockMhz.Value)) + Fmt.Nbsp + "МГц";
            return text + "\nисточник: " + g.Source;
        }

        // "49 °C · 41 Вт · 1 905 МГц · вентилятор 30 %"; температура подсвечивается от 80 °C.
        void GpuDetails(TextBlock tb, GpuInfo g)
        {
            tb.Inlines.Clear();
            bool first = true;
            if (g.Temp.HasValue)
            {
                var run = new Run(Fmt.Num(g.Temp.Value, 0) + Fmt.Nbsp + "°C");
                if (g.Temp.Value >= 80) run.Foreground = g.Temp.Value >= 88 ? dangerBrush : warnBrush;
                tb.Inlines.Add(run);
                first = false;
            }
            if (g.Watts.HasValue)
            {
                tb.Inlines.Add(new Run((first ? "" : " · ") + Fmt.Num(g.Watts.Value, 0) + Fmt.Nbsp + "Вт"));
                first = false;
            }
            if (g.ClockMhz.HasValue)
            {
                tb.Inlines.Add(new Run((first ? "" : " · ") + Fmt.Int((long)Math.Round(g.ClockMhz.Value)) + Fmt.Nbsp + "МГц"));
                first = false;
            }
            if (g.Fan.HasValue)
                tb.Inlines.Add(new Run((first ? "" : " · ") + "вентилятор " + Fmt.Num(g.Fan.Value, 0) + Fmt.Nbsp + "%"));
            else if (g.FanRpm.HasValue)
                tb.Inlines.Add(new Run((first ? "" : " · ") + "вентилятор " + Fmt.Int((long)Math.Round(g.FanRpm.Value)) + Fmt.Nbsp + "об/мин"));
        }
    }
}
