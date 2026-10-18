using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ResourcesWidget
{
    // Кольцевой буфер истории для графиков.
    sealed class Ring
    {
        readonly double[] data;
        int head, count;

        public Ring(int capacity)
        {
            data = new double[capacity];
        }

        public void Add(double v)
        {
            data[head] = v;
            head = (head + 1) % data.Length;
            if (count < data.Length) count++;
        }

        // от старых значений к новым
        public double Max()
        {
            double m = 0;
            for (int i = 0; i < count; i++) if (data[i] > m) m = data[i];
            return m;
        }

        public double[] ToArray()
        {
            var res = new double[count];
            int start = (head - count + data.Length) % data.Length;
            for (int i = 0; i < count; i++) res[i] = data[(start + i) % data.Length];
            return res;
        }
    }

    // Фоновый опрос датчиков. Процессор, память, видеокарта и сеть опрашиваются каждый такт (значок в трее
    // и история графиков). Диски и процессы — каждый такт, пока карточка на экране, и раз в четыре такта,
    // пока она скрыта: так при вызове карточки скорости и загрузка по процессам уже посчитаны.
    sealed class Sampler : IDisposable
    {
        readonly Config cfg;
        readonly CpuSensor cpu = new CpuSensor();
        readonly LhmClient lhm;
        readonly CpuTempSensor cpuTemp;
        bool tempErrorLogged;
        readonly MemorySensor mem = new MemorySensor();
        readonly GpuSensor gpu;
        readonly NetSensor net = new NetSensor();
        readonly DiskSensor disks;
        readonly ProcessSensor procs = new ProcessSensor();
        readonly Ring cpuHist, gpuHist, downHist, upHist;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Stopwatch clock = new Stopwatch();
        Thread thread;
        double lastSeconds;
        int tick;
        List<DiskInfo> lastDisks = new List<DiskInfo>();
        List<ProcInfo> lastProcs;
        public readonly NetJournal Journal; // журнал сети: пики по дням, поминутные записи, перебои

        public volatile bool Detailed;          // карточка на экране
        public volatile string ProcessSort = "cpu";
        public event Action<Sample> Updated;    // вызывается в потоке опроса

        // journalDir — папка журнала сети; journalReadOnly — только читать, без пинга (режимы без окна).
        public Sampler(Config cfg, string journalDir, bool journalReadOnly)
        {
            this.cfg = cfg;
            Journal = new NetJournal(journalDir, journalReadOnly, cfg);
            lhm = new LhmClient(cfg.LhmUrl);
            cpuTemp = new CpuTempSensor(lhm);
            gpu = new GpuSensor(cfg.GpuSource, lhm);
            disks = new DiskSensor(cfg.Disks);
            int points = cfg.HistoryPoints;
            cpuHist = new Ring(points);
            gpuHist = new Ring(points);
            downHist = new Ring(points);
            upHist = new Ring(points);
        }

        public void Start()
        {
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "sampler";
            thread.Priority = ThreadPriority.BelowNormal;
            thread.Start();
        }

        void Loop()
        {
            Prime();
            long next = cfg.IntervalMs;
            var pace = Stopwatch.StartNew();
            while (true)
            {
                long wait = next - pace.ElapsedMilliseconds;
                if (wait < 0)
                {
                    // проснулись после сна или долгой паузы — не догонять пропущенные такты
                    next = pace.ElapsedMilliseconds;
                    wait = 0;
                }
                if (stop.WaitOne((int)wait)) return;
                next += cfg.IntervalMs;
                try
                {
                    var s = Collect(true);
                    var handler = Updated;
                    if (handler != null) handler(s);
                }
                catch (Exception ex)
                {
                    Log.Write("sampler: " + ex);
                }
            }
        }

        // Первое чтение задаёт точку отсчёта для всех разностных показателей.
        public void Prime()
        {
            clock.Restart();
            lastSeconds = 0;
            cpu.Read();
            gpu.Read();     // статистика WDDM считает загрузку по разности
            net.Read(0);
            lastDisks = disks.Read(true);
            lastProcs = procs.Read(ProcessSort, cfg.TopProcesses);
        }

        public Sample Collect(bool respectVisibility)
        {
            double now = clock.Elapsed.TotalSeconds;
            double seconds = now - lastSeconds;
            lastSeconds = now;
            bool visible = Detailed || !respectVisibility;

            var s = new Sample();
            s.Time = DateTime.Now;
            s.Uptime = TimeSpan.FromMilliseconds(Native.GetTickCount64());
            s.Cpu = cpu.Read();
            try
            {
                s.Cpu.Temp = cpuTemp.Read();
                s.Cpu.TempSource = cpuTemp.Source;
            }
            catch (Exception ex)
            {
                if (!tempErrorLogged) Log.Write("cpu temp: " + ex.GetType().Name + ": " + ex.Message);
                tempErrorLogged = true;
            }
            s.Mem = mem.Read();
            s.Gpu = gpu.Read();
            s.Net = net.Read(seconds);
            if (visible || ++tick % 4 == 0)
            {
                lastDisks = disks.Read(visible);    // свободное место пересчитывается только для видимой карточки
                lastProcs = procs.Read(ProcessSort, cfg.TopProcesses);
            }
            s.Disks = lastDisks;
            s.Procs = lastProcs;

            cpuHist.Add(s.Cpu.Total);
            gpuHist.Add(s.Gpu != null ? s.Gpu.Load : 0);
            downHist.Add(s.Net != null ? s.Net.DownBps : 0);
            upHist.Add(s.Net != null ? s.Net.UpBps : 0);
            NetHealth health;
            var today = Journal.Update(s.Net, s.Time, seconds, out health);
            if (s.Net != null)
            {
                s.Net.PeakDown = downHist.Max();
                s.Net.PeakUp = upHist.Max();
                s.Net.Today = today;
                s.Net.Health = health;
            }
            s.CpuHistory = cpuHist.ToArray();
            s.GpuHistory = gpuHist.ToArray();
            s.NetDownHistory = downHist.ToArray();
            s.NetUpHistory = upHist.ToArray();
            return s;
        }

        public void Dispose()
        {
            stop.Set();
            // если поток опроса не успел остановиться, буферы не освобождаем — процесс всё равно завершается
            if (thread != null && thread != Thread.CurrentThread && !thread.Join(1500)) return;
            Journal.Dispose();
            cpu.Dispose();
            lhm.Dispose();
            gpu.Dispose();
            procs.Dispose();
        }
    }
}
