using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ResourcesWidget
{
    // Диски, сеть, процессы.
    sealed partial class Card
    {
        const double LetterW = 28, FreeW = 50, SizeW = 52, RateW = 60, ProcCpuW = 50, ProcMemW = 64;

        TextBlock Caption(string text)
        {
            var tb = Ui.Text(text, 9.5, t.Muted);
            tb.HorizontalAlignment = HorizontalAlignment.Right;
            tb.VerticalAlignment = VerticalAlignment.Bottom;
            return tb;
        }

        TextBlock SectionTitle(string text)
        {
            var tb = Ui.Text(text, 13, t.Text);
            tb.FontWeight = FontWeights.SemiBold;
            tb.Margin = new Thickness(9, 0, 0, 0);
            return tb;
        }

        FrameworkElement Disks(Sample s)
        {
            var panel = new StackPanel();
            bool full = !state.Compact;
            var head = DiskColumns(full, Ui.Auto);
            head.Children.Add(Ui.Badge(Glyph.Disk, t.Disk));
            head.Children.Add(Ui.At(SectionTitle("Диски"), 1));
            head.Children.Add(Ui.At(Caption("свободно"), 2));
            head.Children.Add(Ui.At(Caption("объём"), 3));
            if (full)
            {
                head.Children.Add(Ui.At(Caption("чтение"), 4));
                head.Children.Add(Ui.At(Caption("запись"), 5));
            }
            panel.Children.Add(head);

            foreach (var d in s.Disks)
            {
                string letter = d.Letter;
                var row = DiskColumns(full, Ui.Px(LetterW));
                row.Margin = new Thickness(0, 8, 0, 0);
                row.Background = Brushes.Transparent;   // подсказка по всей строке
                var chip = Ui.Chip(letter, t);
                chip.MinWidth = 24;
                row.Children.Add(chip);
                var meter = new Meter(t) { Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(Ui.At(meter, 1));
                var free = Ui.Number(11.5, t.Text, false);
                free.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(Ui.At(free, 2));
                var size = Ui.Number(11.5, t.Text2, false);
                size.HorizontalAlignment = HorizontalAlignment.Right;
                row.Children.Add(Ui.At(size, 3));
                TextBlock read = null, write = null;
                if (full)
                {
                    read = Ui.Number(11, t.Muted, false);
                    read.HorizontalAlignment = HorizontalAlignment.Right;
                    write = Ui.Number(11, t.Muted, false);
                    write.HorizontalAlignment = HorizontalAlignment.Right;
                    row.Children.Add(Ui.At(read, 4));
                    row.Children.Add(Ui.At(write, 5));
                }
                var tip = Ui.Tip(row, null, tipStyle);
                panel.Children.Add(row);

                updaters.Add(x =>
                {
                    DiskInfo di = null;
                    foreach (var k in x.Disks) if (k.Letter == letter) { di = k; break; }
                    if (di == null) return;
                    meter.Set(di.Percent, t.Level(di.Percent, 85, 95, t.Disk));
                    free.Text = Fmt.Size(di.Free);
                    free.Foreground = Level(di.Percent, 85, 95);
                    size.Text = Fmt.Size(di.Total);
                    if (read != null)
                    {
                        read.Text = Fmt.Rate(di.ReadBps);
                        read.Foreground = di.ReadBps >= 1024 ? t.Text : t.Muted;
                        write.Text = Fmt.Rate(di.WriteBps);
                        write.Foreground = di.WriteBps >= 1024 ? t.Text : t.Muted;
                    }
                    tip.Content = (di.Letter + " " + (di.Label ?? "")).Trim()
                        + "\nзанято " + Fmt.SizePair(di.Used, di.Total) + " (" + Fmt.Percent(di.Percent) + ")"
                        + "\nчтение " + Fmt.Rate(di.ReadBps) + " · запись " + Fmt.Rate(di.WriteBps)
                        + "\nактивность " + Fmt.Percent(di.Active);
                });
            }

            // итог по всем показанным дискам: общая полоса занятого места и итоговая строка
            var totalRow = Ui.Columns(Ui.Px(LetterW), Ui.Star, Ui.Auto);
            totalRow.Margin = new Thickness(0, 10, 0, 0);
            var allChip = Ui.Chip("все", t);
            allChip.MinWidth = 24;
            totalRow.Children.Add(allChip);
            var totalMeter = new Meter(t) { Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            totalRow.Children.Add(Ui.At(totalMeter, 1));
            var totalPct = Ui.Number(11.5, t.Text, false);
            totalRow.Children.Add(Ui.At(totalPct, 2));
            panel.Children.Add(totalRow);
            var sum = Ui.Small(t.Muted, new Thickness(0, 6, 0, 0));
            panel.Children.Add(sum);
            updaters.Add(x =>
            {
                long total = 0, freeSum = 0;
                foreach (var k in x.Disks)
                {
                    total += k.Total;
                    freeSum += k.Free;
                }
                if (total <= 0) return;
                double used = 100.0 * (total - freeSum) / total;
                totalMeter.Set(used, t.Level(used, 85, 95, t.Disk));
                totalPct.Text = "занято " + Fmt.Percent(used);
                totalPct.Foreground = Level(used, 85, 95);
                sum.Text ="всего " + Fmt.Size(total) + " · занято " + Fmt.Size(total - freeSum)
                    + " · свободно " + Fmt.Size(freeSum) + " (" + Fmt.Percent(100.0 * freeSum / total) + ")";
            });
            return panel;
        }

        // Колонки таблицы дисков: буква (или значок в заголовке), полоса, свободно, объём, чтение, запись.
        static Grid DiskColumns(bool full, GridLength first)
        {
            return full
                ? Ui.Columns(first, Ui.Star, Ui.Px(FreeW), Ui.Px(SizeW), Ui.Px(RateW), Ui.Px(RateW))
                : Ui.Columns(first, Ui.Star, Ui.Px(FreeW), Ui.Px(SizeW));
        }

        FrameworkElement networkPanel;   // раздел «Сеть»: к нему привязано окно журнала

        FrameworkElement Network(Sample s)
        {
            var panel = new StackPanel();
            networkPanel = panel;
            string note = s.Net.Name + (s.Net.LinkBps > 0 ? " · " + Fmt.LinkSpeed(s.Net.LinkBps) : "");
            TextBlock ping;
            panel.Children.Add(TitleRow(Glyph.Network, t.NetDown, "Сеть", note, out ping));
            ping.FontSize = 13;
            ping.Background = Brushes.Transparent;
            var pingTip = Ui.Tip(ping, null, tipStyle);

            // Один ряд: слева скорости сейчас, справа пики за сегодня — получение и отдача в одном стиле.
            // Единица у пары общая (по большему значению): четыре «940 Мбит/с» в 316 px не помещаются.
            // Щелчок по пикам открывает журнал сети по дням.
            var speeds = Ui.Columns(Ui.Star, Ui.Star);
            speeds.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            speeds.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            speeds.Margin = new Thickness(0, 6, 0, 0);
            string window = cfg.HistorySeconds + Fmt.Nbsp + "с";
            speeds.Children.Add(SpeedCaption("сейчас", 0));
            speeds.Children.Add(SpeedCaption("пик сегодня  ›", 1));
            var now = SpeedPair(0, false);
            var peaks = SpeedPair(1, true);
            ((Button)peaks.Root).Click += (o, e) => OpenJournal();
            speeds.Children.Add(now.Root);
            speeds.Children.Add(peaks.Root);
            panel.Children.Add(speeds);

            // скорость соединения ниже, чем бывала у этого адаптера: кабель, разъём или порт роутера
            var linkWarn = Ui.Small(warnBrush, new Thickness(0, 6, 0, 0));
            linkWarn.TextTrimming = TextTrimming.None;
            linkWarn.TextWrapping = TextWrapping.Wrap;
            linkWarn.Visibility = Visibility.Collapsed;
            panel.Children.Add(linkWarn);

            Sparkline spark = null;
            TextBlock totals = null, scale = null, day = null;
            ToolTip sparkTip = null;
            if (!state.Compact)
            {
                spark = new Sparkline(t, t.NetDown, t.NetUp, cfg.HistoryPoints) { Height = 36, Margin = new Thickness(0, 8, 0, 0), MarkPeaks = true };
                panel.Children.Add(spark);
                sparkTip = Ui.Tip(spark, null, tipStyle);
                var foot = Ui.Columns(Ui.Star, Ui.Auto);
                foot.Margin = new Thickness(0, 6, 0, 0);
                totals = Ui.Small(t.Muted, new Thickness(0));
                scale = Ui.Small(t.Muted, new Thickness(8, 0, 0, 0));
                foot.Children.Add(totals);
                foot.Children.Add(Ui.At(scale, 1));
                panel.Children.Add(foot);
                day = Ui.Small(t.Muted, new Thickness(0, 2, 0, 0));
                panel.Children.Add(day);
                Ui.Tip(totals, "Сколько принято и отправлено с момента подключения адаптера", tipStyle);
                Ui.Tip(scale, "Верх шкалы графика", tipStyle);
                Ui.Tip(day, "Трафик и перебои связи за сегодня. Подробно — журнал: щелчок по «пик сегодня»", tipStyle);
            }
            string shownPingTip = null, shownSparkTip = null;
            updaters.Add(x =>
            {
                var n = x.Net;
                if (n == null) return;
                now.Set(n.DownBps, n.UpBps, cfg.NetworkBits, "Сейчас");
                var td = n.Today;
                if (td != null)
                    peaks.Set(td.Down, td.Up, cfg.NetworkBits, "Наибольшая скорость сегодня (с 00:00)", td.DownAt, td.UpAt, "\nЩелчок — журнал сети по дням");

                var h = n.Health;
                string pt;
                if (h == null || !h.PingOn)
                {
                    ping.Text = "";
                    pt = null;
                }
                else
                {
                    if (h.Ongoing != null && h.Ongoing.Kind == "internet") { ping.Text = "нет связи"; ping.Foreground = dangerBrush; }
                    else if (h.PingMs.HasValue) { ping.Text = Fmt.Num(h.PingMs.Value, 0) + Fmt.Nbsp + "мс"; ping.Foreground = h.PingMs.Value > cfg.PingWarnMs ? warnBrush : t.Text; }
                    else { ping.Text = "—" + Fmt.Nbsp + "мс"; ping.Foreground = t.Muted; }
                    pt = "Пинг до интернета (" + h.PingHost + "): " + (h.PingMs.HasValue ? Fmt.Num(h.PingMs.Value, 0) + " мс" : "нет ответа")
                        + (h.Gateway != null ? "\nдо роутера (" + h.Gateway + "): " + (h.GatewayMs.HasValue ? Fmt.Num(h.GatewayMs.Value, 0) + " мс" : "нет ответа") : "")
                        + (h.Ongoing != null ? "\n" + h.Ongoing.Text : "")
                        + "\nОбрывы и высокий пинг записываются в журнал сети";
                }
                if (pt != shownPingTip) { shownPingTip = pt; pingTip.Content = pt; }

                bool slower = h != null && n.LinkBps > 0 && h.LinkMaxSeen > n.LinkBps;
                linkWarn.Visibility = slower ? Visibility.Visible : Visibility.Collapsed;
                if (slower)
                    linkWarn.Text = "Соединение " + Fmt.LinkSpeed(n.LinkBps) + ", а бывало " + Fmt.LinkSpeed(h.LinkMaxSeen)
                        + ": адаптер договорился о меньшей скорости — проверьте кабель, разъём и порт роутера";

                if (spark == null) return;
                double top = NiceScale(Math.Max(n.PeakDown, n.PeakUp), cfg.NetworkBits);
                spark.Set(x.NetDownHistory, x.NetUpHistory, top);
                string st = "Скорость за последние " + window + "; точки — пики:\nполучение " + Fmt.NetRate(n.PeakDown, cfg.NetworkBits)
                    + "\nотдача " + Fmt.NetRate(n.PeakUp, cfg.NetworkBits);
                if (st != shownSparkTip) { shownSparkTip = st; sparkTip.Content = st; }
                scale.Text = "шкала " + (cfg.NetworkBits ? Fmt.LinkSpeed((long)Math.Round(top * 8)) : Fmt.Rate(top));
                totals.Text = "всего ↓" + Fmt.Nbsp + Fmt.Size(n.TotalDown) + " · ↑" + Fmt.Nbsp + Fmt.Size(n.TotalUp);
                if (td != null)
                {
                    day.Inlines.Clear();
                    day.Inlines.Add(new Run("сегодня ↓" + Fmt.Nbsp + Fmt.Size(td.BytesDown) + " · ↑" + Fmt.Nbsp + Fmt.Size(td.BytesUp) + " · "));
                    int problems = td.Problems;
                    if (h != null && h.Ongoing != null) day.Inlines.Add(new Run("сейчас: " + h.Ongoing.Text.ToLowerInvariant()) { Foreground = dangerBrush });
                    else if (problems > 0) day.Inlines.Add(new Run(problems + Fmt.Nbsp + Fmt.Plural(problems, "перебой", "перебоя", "перебоев")) { Foreground = dangerBrush });
                    else day.Inlines.Add(new Run(h != null && h.PingOn ? "без перебоев" : "пинг выключен"));
                }
            });
            return panel;
        }

        // Журнал сети — отдельное плавающее окно (JournalWindow); первый раз — рядом с карточкой у раздела «Сеть».
        void OpenJournal()
        {
            if (act.Journal == null) return;
            var journal = act.Journal();
            if (journal == null) return;
            JournalWindow.Open(t, cfg, state, journal, NetworkAnchor, Window.GetWindow(Root));
        }

        // Экранные координаты карточки и раздела «Сеть»: PointToScreen даёт пиксели, окну нужны независимые единицы — делим на DPI.
        JournalAnchor NetworkAnchor()
        {
            var section = networkPanel;
            if (section == null || Root == null || PresentationSource.FromVisual(section) == null) return null;
            var dpi = VisualTreeHelper.GetDpi(section);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
            double gapAbove = Gap * state.Scale;   // разделитель над разделом
            Point secTop = section.PointToScreen(new Point(0, 0));
            Point secBottom = section.PointToScreen(new Point(0, section.ActualHeight));
            Point cardLeft = Root.PointToScreen(new Point(0, 0));
            Point cardRight = Root.PointToScreen(new Point(Root.ActualWidth, 0));
            var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)cardLeft.X, (int)secTop.Y)).WorkingArea;
            return new JournalAnchor
            {
                CardLeft = cardLeft.X / sx,
                CardRight = cardRight.X / sx,
                SectionTop = secTop.Y / sy - gapAbove,
                SectionBottom = secBottom.Y / sy + gapAbove,
                Area = new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy),
            };
        }

        // Подпись над колонкой («сейчас», «пик за 60 с»), как подписи колонок у дисков.
        TextBlock SpeedCaption(string text, int column)
        {
            var tb = Ui.Text(text, 9.5, t.Muted);
            tb.Margin = new Thickness(1, 0, 0, 2);
            Grid.SetColumn(tb, column);
            return tb;
        }

        // «↓ 32,1  ↑ 14,8 Кбит/с»: цветные стрелки, числа и общая единица.
        sealed class SpeedPairView
        {
            public FrameworkElement Root;
            public TextBlock Down, Up, Unit;
            public ToolTip Tip;
            string shownTip;

            public void Set(double down, double up, bool bits, string what)
            {
                Set(down, up, bits, what, null, null, null);
            }

            public void Set(double down, double up, bool bits, string what, DateTime? downAt, DateTime? upAt, string tail)
            {
                var p = Fmt.NetPair(down, up, bits);
                Down.Text = p[0];
                Up.Text = p[1];
                Unit.Text = p[2];
                string tip = what + "\nполучение " + Fmt.NetRate(down, bits) + At(down, downAt) + "\nотдача " + Fmt.NetRate(up, bits) + At(up, upAt) + (tail ?? "");
                if (tip != shownTip)
                {
                    shownTip = tip;
                    Tip.Content = tip;
                }
            }

            static string At(double v, DateTime? at)
            {
                return v > 0 && at.HasValue ? " в" + Fmt.Nbsp + at.Value.ToString("HH:mm:ss") : "";
            }
        }

        // clickable — пара в кнопке (пики: щелчок открывает журнал).
        SpeedPairView SpeedPair(int column, bool clickable)
        {
            var v = new SpeedPairView();
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
            sp.Children.Add(Arrow(Glyph.Down, t.NetDown, 0));
            v.Down = Ui.Number(13, t.Text, true);
            sp.Children.Add(v.Down);
            sp.Children.Add(Arrow(Glyph.Up, t.NetUp, 10));
            v.Up = Ui.Number(13, t.Text, true);
            sp.Children.Add(v.Up);
            v.Unit = Ui.Text("", 11, t.Text2);
            v.Unit.VerticalAlignment = VerticalAlignment.Bottom;
            v.Unit.Margin = new Thickness(4, 0, 0, 1);
            sp.Children.Add(v.Unit);
            FrameworkElement root = sp;
            if (clickable)
            {
                var b = Ui.ChipButton("");
                b.Content = sp;
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(-6, -1, 0, -2);
                System.Windows.Automation.AutomationProperties.SetName(b, "Журнал сети");
                root = b;
            }
            v.Tip = Ui.Tip(root, null, tipStyle);
            Grid.SetRow(root, 1);
            Grid.SetColumn(root, column);
            v.Root = root;
            return v;
        }

        static TextBlock Arrow(string glyph, Color color, double left)
        {
            return new TextBlock { Text = glyph, FontFamily = Ui.IconFont, FontSize = 11, Foreground = Theme.B(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(left + 1, 1, 5, 0) };
        }

        // Верх шкалы: ближайшее сверху значение ряда 1-2-5 (в битах или байтах), не меньше 1 Мбит/с (128 КБ/с).
        public static double NiceScale(double bytesPerSecond, bool bits)
        {
            double unit = bits ? 1.0 / 8 : 1;              // сколько байт в единице шкалы
            double v = Math.Max(bytesPerSecond / unit, bits ? 1e6 : 128 * 1024);
            double p = Math.Pow(bits ? 10 : 2, Math.Floor(Math.Log(v, bits ? 10 : 2)));
            double[] steps = bits ? new[] { 1.0, 2, 5, 10 } : new[] { 1.0, 2 };
            foreach (var k in steps)
                if (k * p >= v) return k * p * unit;
            return 10 * p * unit;
        }

        sealed class ProcRow
        {
            public TextBlock Name, Cpu, Mem;
            public string Shown;   // имя и число копий, которые сейчас в строке
        }

        FrameworkElement Processes(Sample s)
        {
            var panel = new StackPanel();
            var head = Ui.Columns(Ui.Auto, Ui.Star, Ui.Px(ProcCpuW), Ui.Px(ProcMemW));
            head.Children.Add(Ui.Badge(Glyph.Processes, t.Process));
            var title = SectionTitle("Процессы");
            head.Children.Add(Ui.At(title, 1));
            Ui.Tip(title, "Процессы с одинаковым именем сложены.\nЦП — доля всей мощности процессора, память — частный рабочий набор.", tipStyle);
            var byCpu = Ui.ChipButton("ЦП");
            var byMem = Ui.ChipButton("Память");
            byCpu.Click += (o, e) => { if (act.Sort != null) act.Sort("cpu"); };
            byMem.Click += (o, e) => { if (act.Sort != null) act.Sort("memory"); };
            Ui.Tip(byCpu, "Сортировать по загрузке процессора", tipStyle);
            Ui.Tip(byMem, "Сортировать по памяти", tipStyle);
            head.Children.Add(Ui.At(byCpu, 2));
            head.Children.Add(Ui.At(byMem, 3));
            panel.Children.Add(head);

            var rows = new List<ProcRow>();
            for (int i = 0; i < cfg.TopProcesses; i++)
            {
                var g = Ui.Columns(Ui.Star, Ui.Px(ProcCpuW), Ui.Px(ProcMemW));
                g.Margin = new Thickness(0, i == 0 ? 8 : 5, 0, 0);
                var r = new ProcRow();
                r.Name = Ui.Text("", 11.5, t.Text);
                r.Name.TextTrimming = TextTrimming.CharacterEllipsis;
                r.Cpu = Ui.Number(11.5, t.Text, false);
                r.Cpu.HorizontalAlignment = HorizontalAlignment.Right;
                r.Mem = Ui.Number(11.5, t.Text2, false);
                r.Mem.HorizontalAlignment = HorizontalAlignment.Right;
                g.Children.Add(r.Name);
                g.Children.Add(Ui.At(r.Cpu, 1));
                g.Children.Add(Ui.At(r.Mem, 2));
                panel.Children.Add(g);
                rows.Add(r);
            }

            updaters.Add(x =>
            {
                bool cpu = state.ProcessSort != "memory";
                byCpu.Background = cpu ? t.ChipBg : Brushes.Transparent;
                byCpu.Foreground = cpu ? t.Text : t.Muted;
                byMem.Background = cpu ? Brushes.Transparent : t.ChipBg;
                byMem.Foreground = cpu ? t.Muted : t.Text;
                var list = x.Procs;
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    var p = list != null && i < list.Count ? list[i] : null;
                    string shown = p == null ? "" : p.Name + "\n" + p.Count;
                    if (shown != r.Shown)
                    {
                        r.Shown = shown;
                        r.Name.Inlines.Clear();
                        if (p != null)
                        {
                            r.Name.Inlines.Add(new Run(p.Name));
                            if (p.Count > 1) r.Name.Inlines.Add(new Run(Fmt.Nbsp + "×" + p.Count) { Foreground = t.Muted, FontSize = 10.5 });
                        }
                    }
                    if (p == null)
                    {
                        r.Cpu.Text = "";
                        r.Mem.Text = "";
                        continue;
                    }
                    r.Cpu.Text = Fmt.SharePercent(p.Cpu);
                    r.Cpu.Foreground = p.Cpu < 0.05 ? t.Muted : t.Text;
                    r.Mem.Text = Fmt.Size(p.Memory);
                }
            });
            return panel;
        }
    }
}
