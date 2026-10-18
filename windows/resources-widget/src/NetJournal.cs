using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ResourcesWidget
{
    // Сеть за один календарный день (по местному времени).
    sealed class DayPeaks
    {
        public DateTime Day;                        // 00:00 этого дня
        public double Down, Up;                     // наибольшая скорость за такт опроса (секунду), байт/с
        public DateTime DownAt, UpAt;
        public double MinuteDown, MinuteUp;         // наибольшая средняя скорость за минуту — устойчивая нагрузка
        public DateTime MinuteDownAt, MinuteUpAt;
        public double BytesDown, BytesUp;           // трафик за день, пока работал виджет
        public double Seconds;                      // сколько секунд виджет считал в этот день
        public long LinkMin, LinkMax;               // скорость соединения адаптера за день, бит/с
        public string Adapter;
        public int PingSent, PingLost;              // пинг до интернета (pingHost)
        public double PingSum, PingMax;             // мс, по ответившим
        public int GwSent, GwLost;                  // пинг до роутера (основной шлюз)
        public int Outages;                         // обрывы: нет связи с интернетом или с роутером
        public double OutageSeconds;
        public int Lags, Disconnects, LinkChanges;  // долгий высокий пинг, отключения сети, смены скорости соединения

        public double PingAvg { get { return PingSent - PingLost > 0 ? PingSum / (PingSent - PingLost) : 0; } }
        public double PingLossPercent { get { return PingSent > 0 ? 100.0 * PingLost / PingSent : 0; } }
        public int Problems { get { return Outages + Lags + Disconnects + LinkChanges; } }

        public DayPeaks Clone()
        {
            return (DayPeaks)MemberwiseClone();
        }
    }

    // Событие связи: смена скорости соединения, отключение сети, обрыв, высокий пинг.
    sealed class NetEvent
    {
        public DateTime Start, End;
        public string Kind;     // link, down, internet, gateway, lag, adapter
        public bool Bad;
        public string Text;
    }

    // Состояние связи для карточки.
    sealed class NetHealth
    {
        public string PingHost, Gateway;
        public double? PingMs, GatewayMs;   // последний ответ; null — не отвечает или пинг выключен
        public bool PingOn;
        public NetEvent Ongoing;            // идущий сейчас обрыв или высокий пинг
        public long LinkMaxSeen;            // самая высокая скорость соединения этого адаптера в журнале
    }

    // Поминутная запись дня (график в журнале).
    sealed class DayDetail
    {
        public const int Minutes = 1440;
        public readonly double[] AvgDown = Nan(), AvgUp = Nan(), MaxDown = Nan(), MaxUp = Nan(), Ping = Nan(), PingMax = Nan(), GwPing = Nan();
        public readonly int[] Lost = new int[Minutes], GwLost = new int[Minutes];
        public readonly long[] Link = new long[Minutes];

        static double[] Nan()
        {
            var a = new double[Minutes];
            for (int i = 0; i < a.Length; i++) a[i] = double.NaN;
            return a;
        }
    }

    // Журнал сети в папке network-log рядом с exe:
    //   days.json        — итоги по дням (пики, трафик, пинг, число перебоев), 400 дней;
    //   yyyy-MM-dd.csv   — поминутно: средняя и пиковая скорость, скорость соединения, пинг и потери; 400 дней;
    //   events.csv       — события: смена скорости соединения, отключения, обрывы связи, высокий пинг.
    // Пинг идёт в своём потоке раз в pingIntervalSec до основного шлюза (роутера) и до pingHost (интернет):
    // если не отвечает только интернет — сбой у провайдера, если и роутер — в домашней сети.
    sealed class NetJournal : IDisposable
    {
        const int KeepDays = 400;
        const double MinuteSeconds = 60;
        const int FailsForOutage = 3, SlowForLag = 3;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const string MinuteHeader = "time;avgDownBps;avgUpBps;maxDownBps;maxUpBps;linkBps;pingSent;pingLost;pingAvgMs;pingMaxMs;gwSent;gwLost;gwAvgMs";

        readonly string dir;
        readonly bool readOnly;
        readonly Config cfg;
        readonly object gate = new object();
        readonly List<DayPeaks> days = new List<DayPeaks>();   // от старых к новым
        readonly List<NetEvent> events = new List<NetEvent>();
        DayPeaks today;
        DateTime lastSave = DateTime.Now;
        bool dirty, peakChanged;

        // скользящая минута для «пика за минуту»
        readonly Queue<double[]> window = new Queue<double[]>();
        double winDown, winUp, winSec;

        // текущая минута для поминутной записи
        DateTime minuteStart;
        double mDown, mUp, mSec, mMaxDown, mMaxUp, mPingSum, mPingMax, mGwSum;
        long mLink;
        int mSent, mLost, mGwSent, mGwLost;

        // адаптер и эпизоды
        string lastAdapter;
        long lastLink;
        bool wasDown;
        DateTime downSince;
        int fails, slow;
        DateTime firstFail, firstSlow;
        NetEvent outage, lag;
        double lagMax;
        int outageGwFails, outageGwOk;
        double? lastPing, lastGw;
        DateTime lastPingAt, lastGwAt;

        // пинг
        readonly Pinger pinger;
        DateTime nextGatewayLookup = DateTime.MinValue;

        // path — папка журнала; null — только в памяти. readOnly — читать, но не писать и не пинговать (--dump, --render).
        public NetJournal(string dir, bool readOnly, Config cfg)
        {
            this.dir = dir;
            this.readOnly = readOnly;
            this.cfg = cfg;
            LoadDays();
            LoadEvents();
            if (!readOnly && !string.IsNullOrEmpty(cfg.PingHost)) pinger = new Pinger(cfg.PingHost, cfg.PingIntervalSec);
        }

        public string Dir { get { return dir; } }

        // Активный адаптер и скорость его соединения (0 — сеть отключена или опроса ещё не было).
        public void Current(out string adapter, out long link)
        {
            lock (gate)
            {
                adapter = lastAdapter;
                link = wasDown ? 0 : lastLink;
            }
        }

        // Учесть такт опроса; возвращает копию сегодняшнего дня и состояние связи.
        public DayPeaks Update(NetInfo n, DateTime now, double seconds, out NetHealth health)
        {
            lock (gate)
            {
                if (today == null || today.Day != now.Date) NewDay(now);
                if (minuteStart == default(DateTime)) minuteStart = Minute(now);
                else if (Minute(now) != minuteStart) FlushMinute(Minute(now));

                Adapter(n, now);
                if (n != null && seconds > 0) Traffic(n, now, seconds);
                if (pinger != null) DrainPings(n);

                double since = (now - lastSave).TotalSeconds;
                if (dirty && (since >= 300 || (peakChanged && since >= 60))) SaveDays(now);
                health = Health(n, now);
                return today.Clone();
            }
        }

        static DateTime Minute(DateTime t)
        {
            return new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
        }

        // ---------- адаптер: отключения и скорость соединения ----------

        void Adapter(NetInfo n, DateTime now)
        {
            if (n == null)
            {
                if (!wasDown && lastAdapter != null)
                {
                    wasDown = true;
                    downSince = now;
                    CloseEpisodes(now);
                }
                return;
            }
            if (wasDown)
            {
                wasDown = false;
                var dur = now - downSince;
                Add(new NetEvent { Start = downSince, End = now, Kind = "down", Bad = true, Text = "Сеть отключалась на " + Fmt.Duration(dur) + " (" + lastAdapter + ")" });
                today.Disconnects++;
            }
            if (lastAdapter != null && n.Name != lastAdapter)
                Add(new NetEvent { Start = now, End = now, Kind = "adapter", Text = "Сеть идёт через «" + n.Name + "» вместо «" + lastAdapter + "»" });
            else if (lastAdapter != null && lastLink > 0 && n.LinkBps > 0 && n.LinkBps != lastLink)
            {
                bool worse = n.LinkBps < lastLink;
                Add(new NetEvent
                {
                    Start = now, End = now, Kind = "link", Bad = worse,
                    Text = "Скорость соединения " + Fmt.LinkSpeed(lastLink) + " → " + Fmt.LinkSpeed(n.LinkBps) + " (" + n.Name + ")"
                        + (worse ? ": адаптер договорился о меньшей скорости — кабель, разъём или порт роутера" : ""),
                });
                today.LinkChanges++;
            }
            if (n.Name != lastAdapter) nextGatewayLookup = DateTime.MinValue;
            lastAdapter = n.Name;
            if (n.LinkBps > 0) lastLink = n.LinkBps;
            if (pinger != null && now >= nextGatewayLookup)
            {
                pinger.Gateway = FindGateway(n.Name);
                nextGatewayLookup = now.AddSeconds(30);
            }
        }

        static string FindGateway(string alias)
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != alias) continue;
                    foreach (var g in ni.GetIPProperties().GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)) return g.Address.ToString();
                }
            }
            catch (NetworkInformationException)
            {
            }
            return null;
        }

        // ---------- скорость и трафик ----------

        void Traffic(NetInfo n, DateTime now, double seconds)
        {
            var d = today;
            if (n.DownBps > d.Down) { d.Down = n.DownBps; d.DownAt = now; peakChanged = true; }
            if (n.UpBps > d.Up) { d.Up = n.UpBps; d.UpAt = now; peakChanged = true; }
            if (n.LinkBps > 0)
            {
                if (d.LinkMin == 0 || n.LinkBps < d.LinkMin) d.LinkMin = n.LinkBps;
                if (n.LinkBps > d.LinkMax) d.LinkMax = n.LinkBps;
            }
            d.Adapter = n.Name;
            dirty = true;
            if (seconds > 5)
            {
                // сон или долгая пауза: скорость за этот промежуток — среднее, в пики и минуту не идёт
                window.Clear();
                winDown = winUp = winSec = 0;
                return;
            }
            d.BytesDown += n.DownBps * seconds;
            d.BytesUp += n.UpBps * seconds;
            d.Seconds += seconds;

            mDown += n.DownBps * seconds;
            mUp += n.UpBps * seconds;
            mSec += seconds;
            mMaxDown = Math.Max(mMaxDown, n.DownBps);
            mMaxUp = Math.Max(mMaxUp, n.UpBps);
            if (n.LinkBps > 0) mLink = mLink == 0 ? n.LinkBps : Math.Min(mLink, n.LinkBps);

            // средняя за последние 60 с
            window.Enqueue(new[] { n.DownBps * seconds, n.UpBps * seconds, seconds });
            winDown += n.DownBps * seconds;
            winUp += n.UpBps * seconds;
            winSec += seconds;
            while (window.Count > 1 && winSec - window.Peek()[2] >= MinuteSeconds - 0.5)
            {
                var old = window.Dequeue();
                winDown -= old[0];
                winUp -= old[1];
                winSec -= old[2];
            }
            if (winSec < MinuteSeconds - 1.5) return;   // минута ещё не набралась
            double down = winDown / winSec, up = winUp / winSec;
            if (down > d.MinuteDown) { d.MinuteDown = down; d.MinuteDownAt = now; peakChanged = true; }
            if (up > d.MinuteUp) { d.MinuteUp = up; d.MinuteUpAt = now; peakChanged = true; }
        }

        // ---------- пинг: обрывы и высокий пинг ----------

        void DrainPings(NetInfo n)
        {
            Pinger.Result r;
            while (pinger.Results.TryDequeue(out r))
            {
                if (n == null || wasDown) continue;   // сеть отключена — это уже событие «отключение»
                if (r.Gateway)
                {
                    mGwSent++;
                    today.GwSent++;
                    if (r.Ms < 0) { mGwLost++; today.GwLost++; lastGw = null; }
                    else { mGwSum += r.Ms; lastGw = r.Ms; lastGwAt = r.At; }
                    if (outage != null) { if (r.Ms < 0) outageGwFails++; else outageGwOk++; }
                    continue;
                }
                mSent++;
                today.PingSent++;
                dirty = true;
                if (r.Ms < 0)
                {
                    mLost++;
                    today.PingLost++;
                    lastPing = null;
                    if (fails++ == 0) firstFail = r.At;
                    if (fails >= FailsForOutage && outage == null)
                    {
                        outage = new NetEvent { Start = firstFail, Kind = "internet", Bad = true };
                        outageGwFails = outageGwOk = 0;
                    }
                    continue;
                }
                lastPing = r.Ms;
                lastPingAt = r.At;
                mPingSum += r.Ms;
                mPingMax = Math.Max(mPingMax, r.Ms);
                today.PingSum += r.Ms;
                today.PingMax = Math.Max(today.PingMax, r.Ms);
                fails = 0;
                if (outage != null) CloseOutage(r.At);
                if (r.Ms > cfg.PingWarnMs)
                {
                    if (slow++ == 0) firstSlow = r.At;
                    if (slow >= SlowForLag && lag == null)
                    {
                        lag = new NetEvent { Start = firstSlow, Kind = "lag", Bad = true };
                        lagMax = 0;
                    }
                    if (lag != null) lagMax = Math.Max(lagMax, r.Ms);
                }
                else
                {
                    slow = 0;
                    if (lag != null) CloseLag(r.At);
                }
            }
        }

        void CloseOutage(DateTime end)
        {
            var e = outage;
            outage = null;
            e.End = end;
            bool local = outageGwFails > outageGwOk;
            e.Kind = local ? "gateway" : "internet";
            string gw = pinger != null ? pinger.Gateway : null;
            e.Text = local
                ? "Нет связи с роутером " + Fmt.Duration(e.End - e.Start) + (gw != null ? " (" + gw + " не отвечал)" : "") + " — сбой в домашней сети"
                : "Нет интернета " + Fmt.Duration(e.End - e.Start) + " (" + cfg.PingHost + " не отвечал" + (gw != null ? ", роутер отвечал" : "") + ") — сбой у провайдера или роутера";
            Add(e);
            today.Outages++;
            today.OutageSeconds += (e.End - e.Start).TotalSeconds;
        }

        void CloseLag(DateTime end)
        {
            var e = lag;
            lag = null;
            e.End = end;
            e.Text = "Высокий пинг до " + lagMax.ToString("0", Inv) + Fmt.Nbsp + "мс " + Fmt.Duration(e.End - e.Start) + " (порог " + cfg.PingWarnMs + Fmt.Nbsp + "мс)";
            Add(e);
            today.Lags++;
        }

        void CloseEpisodes(DateTime now)
        {
            if (outage != null) CloseOutage(now);
            if (lag != null) CloseLag(now);
            fails = slow = 0;
        }

        NetHealth Health(NetInfo n, DateTime now)
        {
            var h = new NetHealth { PingOn = pinger != null, PingHost = cfg.PingHost };
            if (pinger != null)
            {
                h.Gateway = pinger.Gateway;
                double stale = Math.Max(5, cfg.PingIntervalSec * 3);
                if (lastPing.HasValue && (now - lastPingAt).TotalSeconds < stale) h.PingMs = lastPing;
                if (lastGw.HasValue && (now - lastGwAt).TotalSeconds < stale) h.GatewayMs = lastGw;
            }
            if (outage != null) h.Ongoing = new NetEvent { Start = outage.Start, Kind = "internet", Bad = true, Text = "Нет связи с интернетом " + Fmt.Duration(now - outage.Start) };
            else if (lag != null) h.Ongoing = new NetEvent { Start = lag.Start, Kind = "lag", Bad = true, Text = "Высокий пинг " + Fmt.Duration(now - lag.Start) };
            if (n != null)
                foreach (var d in days)
                    if (d.Adapter == n.Name) h.LinkMaxSeen = Math.Max(h.LinkMaxSeen, d.LinkMax);
            return h;
        }

        // ---------- поминутная запись ----------

        void FlushMinute(DateTime next)
        {
            if (mSec > 0 || mSent > 0 || mGwSent > 0)
            {
                var sb = new StringBuilder();
                sb.Append(minuteStart.ToString("HH:mm", Inv)).Append(';')
                  .Append(R(mSec > 0 ? mDown / mSec : 0)).Append(';').Append(R(mSec > 0 ? mUp / mSec : 0)).Append(';')
                  .Append(R(mMaxDown)).Append(';').Append(R(mMaxUp)).Append(';').Append(mLink).Append(';')
                  .Append(mSent).Append(';').Append(mLost).Append(';')
                  .Append(mSent - mLost > 0 ? (mPingSum / (mSent - mLost)).ToString("0.0", Inv) : "").Append(';')
                  .Append(mSent - mLost > 0 ? mPingMax.ToString("0", Inv) : "").Append(';')
                  .Append(mGwSent).Append(';').Append(mGwLost).Append(';')
                  .Append(mGwSent - mGwLost > 0 ? (mGwSum / (mGwSent - mGwLost)).ToString("0.0", Inv) : "");
                AppendLine(Path.Combine(dir ?? "", minuteStart.ToString("yyyy-MM-dd", Inv) + ".csv"), sb.ToString(), MinuteHeader);
            }
            minuteStart = next;
            mDown = mUp = mSec = mMaxDown = mMaxUp = mPingSum = mPingMax = mGwSum = 0;
            mLink = 0;
            mSent = mLost = mGwSent = mGwLost = 0;
        }

        static string R(double v)
        {
            return Math.Round(v).ToString("0", Inv);
        }

        void AppendLine(string path, string line, string header)
        {
            if (readOnly || string.IsNullOrEmpty(dir)) return;
            try
            {
                Directory.CreateDirectory(dir);
                bool fresh = !File.Exists(path);
                File.AppendAllText(path, (fresh && header != null ? header + "\r\n" : "") + line + "\r\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Write("network-log: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Поминутные данные дня для графика (из файла; сегодняшняя неполная минута не входит).
        public DayDetail ReadDay(DateTime day)
        {
            var res = new DayDetail();
            string path = Path.Combine(dir ?? "", day.ToString("yyyy-MM-dd", Inv) + ".csv");
            if (string.IsNullOrEmpty(dir) || !File.Exists(path)) return res;
            try
            {
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var f = line.Split(';');
                    TimeSpan t;
                    if (f.Length < 13 || !TimeSpan.TryParseExact(f[0], @"hh\:mm", Inv, out t)) continue;
                    int i = (int)t.TotalMinutes;
                    if (i < 0 || i >= DayDetail.Minutes) continue;
                    res.AvgDown[i] = D(f[1]);
                    res.AvgUp[i] = D(f[2]);
                    res.MaxDown[i] = D(f[3]);
                    res.MaxUp[i] = D(f[4]);
                    long link;
                    long.TryParse(f[5], NumberStyles.Integer, Inv, out link);
                    res.Link[i] = link;
                    int lost;
                    int.TryParse(f[7], NumberStyles.Integer, Inv, out lost);
                    res.Lost[i] = lost;
                    res.Ping[i] = D(f[8]);
                    res.PingMax[i] = D(f[9]);
                    int.TryParse(f[11], NumberStyles.Integer, Inv, out lost);
                    res.GwLost[i] = lost;
                    res.GwPing[i] = D(f[12]);
                }
            }
            catch (Exception ex)
            {
                Log.Write("network-log: " + ex.Message);
            }
            return res;
        }

        static double D(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : double.NaN;
        }

        // ---------- события ----------

        void Add(NetEvent e)
        {
            events.Add(e);
            AppendLine(Path.Combine(dir ?? "", "events.csv"),
                e.Start.ToString("yyyy-MM-dd HH:mm:ss", Inv) + ";" + e.End.ToString("yyyy-MM-dd HH:mm:ss", Inv) + ";" + e.Kind + ";" + (e.Bad ? 1 : 0) + ";" + (e.Text ?? "").Replace(';', ',').Replace('\n', ' '),
                "start;end;kind;bad;text");
            dirty = peakChanged = true;
        }

        void LoadEvents()
        {
            string path = Path.Combine(dir ?? "", "events.csv");
            if (string.IsNullOrEmpty(dir) || !File.Exists(path)) return;
            try
            {
                DateTime from = DateTime.Today.AddDays(-KeepDays);
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var f = line.Split(new[] { ';' }, 5);
                    DateTime s, e;
                    if (f.Length < 5 || !DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out s)
                        || !DateTime.TryParseExact(f[1], "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.None, out e) || s < from) continue;
                    events.Add(new NetEvent { Start = s, End = e, Kind = f[2], Bad = f[3] == "1", Text = f[4] });
                }
            }
            catch (Exception ex)
            {
                Log.Write("events.csv не прочитан: " + ex.Message);
            }
        }

        // События дня (или все, если day = null), от новых к старым; идущий сейчас эпизод — первым.
        public List<NetEvent> Events(DateTime? day)
        {
            lock (gate)
            {
                var res = new List<NetEvent>();
                var now = DateTime.Now;
                if (outage != null && (day == null || outage.Start.Date == day.Value)) res.Add(new NetEvent { Start = outage.Start, End = now, Kind = "internet", Bad = true, Text = "Сейчас: нет связи с интернетом " + Fmt.Duration(now - outage.Start) });
                if (lag != null && (day == null || lag.Start.Date == day.Value)) res.Add(new NetEvent { Start = lag.Start, End = now, Kind = "lag", Bad = true, Text = "Сейчас: высокий пинг " + Fmt.Duration(now - lag.Start) });
                for (int i = events.Count - 1; i >= 0; i--)
                    if (day == null || events[i].Start.Date == day.Value) res.Add(events[i]);
                return res;
            }
        }

        // ---------- итоги по дням ----------

        void NewDay(DateTime now)
        {
            if (today != null)
            {
                if (minuteStart != default(DateTime)) FlushMinute(Minute(now));
                if (dirty) SaveDays(now);
            }
            var last = days.Count > 0 ? days[days.Count - 1] : null;
            if (last != null && last.Day == now.Date) today = last;
            else
            {
                today = new DayPeaks { Day = now.Date };
                days.Add(today);
                while (days.Count > KeepDays) days.RemoveAt(0);
                if (!readOnly) Prune(now.Date);
            }
        }

        // Поминутные файлы старше 400 дней удаляются.
        void Prune(DateTime todayDate)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "????-??-??.csv"))
                {
                    DateTime d;
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", Inv, DateTimeStyles.None, out d) && d < todayDate.AddDays(-KeepDays))
                        File.Delete(f);
                }
            }
            catch (Exception ex)
            {
                Log.Write("network-log: " + ex.Message);
            }
        }

        // Копии дней, от новых к старым.
        public List<DayPeaks> Snapshot()
        {
            lock (gate)
            {
                var res = new List<DayPeaks>(days.Count);
                for (int i = days.Count - 1; i >= 0; i--) res.Add(days[i].Clone());
                return res;
            }
        }

        void SaveDays(DateTime now)
        {
            lastSave = now;
            dirty = peakChanged = false;
            if (readOnly || string.IsNullOrEmpty(dir)) return;
            var list = new List<object>();
            foreach (var d in days)
            {
                var o = new Dictionary<string, object>();
                o["day"] = d.Day.ToString("yyyy-MM-dd", Inv);
                o["downBps"] = Math.Round(d.Down);
                o["downAt"] = Time(d.DownAt, d.Down);
                o["upBps"] = Math.Round(d.Up);
                o["upAt"] = Time(d.UpAt, d.Up);
                o["minuteDownBps"] = Math.Round(d.MinuteDown);
                o["minuteDownAt"] = Time(d.MinuteDownAt, d.MinuteDown);
                o["minuteUpBps"] = Math.Round(d.MinuteUp);
                o["minuteUpAt"] = Time(d.MinuteUpAt, d.MinuteUp);
                o["bytesDown"] = Math.Round(d.BytesDown);
                o["bytesUp"] = Math.Round(d.BytesUp);
                o["seconds"] = Math.Round(d.Seconds);
                o["linkMinBps"] = d.LinkMin;
                o["linkMaxBps"] = d.LinkMax;
                o["adapter"] = d.Adapter;
                o["pingSent"] = d.PingSent;
                o["pingLost"] = d.PingLost;
                o["pingSumMs"] = Math.Round(d.PingSum);
                o["pingMaxMs"] = Math.Round(d.PingMax);
                o["gwSent"] = d.GwSent;
                o["gwLost"] = d.GwLost;
                o["outages"] = d.Outages;
                o["outageSeconds"] = Math.Round(d.OutageSeconds);
                o["lags"] = d.Lags;
                o["disconnects"] = d.Disconnects;
                o["linkChanges"] = d.LinkChanges;
                list.Add(o);
            }
            var root = new Dictionary<string, object>();
            root["_help"] = "Итоги сети по дням (виджет «Системные ресурсы»): пики за секунду и за минуту (байт/с), трафик (байт), скорость соединения (бит/с), пинг (мс) и перебои.";
            root["days"] = list;
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "days.json"), tmp = path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(root, true), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Log.Write("days.json: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static string Time(DateTime t, double value)
        {
            return value > 0 ? t.ToString("HH:mm:ss", Inv) : null;
        }

        void LoadDays()
        {
            string path = Path.Combine(dir ?? "", "days.json");
            if (string.IsNullOrEmpty(dir) || !File.Exists(path)) return;
            try
            {
                var arr = Json.Arr(Json.Get(Json.TryParse(File.ReadAllText(path, Encoding.UTF8)), "days"));
                if (arr == null) return;
                foreach (var item in arr)
                {
                    DateTime day;
                    if (!DateTime.TryParseExact(Json.Str(Json.Get(item, "day")) ?? "", "yyyy-MM-dd", Inv, DateTimeStyles.None, out day)) continue;
                    var d = new DayPeaks { Day = day.Date };
                    d.Down = Num(item, "downBps");
                    d.DownAt = At(day, item, "downAt");
                    d.Up = Num(item, "upBps");
                    d.UpAt = At(day, item, "upAt");
                    d.MinuteDown = Num(item, "minuteDownBps");
                    d.MinuteDownAt = At(day, item, "minuteDownAt");
                    d.MinuteUp = Num(item, "minuteUpBps");
                    d.MinuteUpAt = At(day, item, "minuteUpAt");
                    d.BytesDown = Num(item, "bytesDown");
                    d.BytesUp = Num(item, "bytesUp");
                    d.Seconds = Num(item, "seconds");
                    d.LinkMin = (long)Num(item, "linkMinBps");
                    d.LinkMax = (long)Num(item, "linkMaxBps");
                    d.Adapter = Json.Str(Json.Get(item, "adapter"));
                    d.PingSent = (int)Num(item, "pingSent");
                    d.PingLost = (int)Num(item, "pingLost");
                    d.PingSum = Num(item, "pingSumMs");
                    d.PingMax = Num(item, "pingMaxMs");
                    d.GwSent = (int)Num(item, "gwSent");
                    d.GwLost = (int)Num(item, "gwLost");
                    d.Outages = (int)Num(item, "outages");
                    d.OutageSeconds = Num(item, "outageSeconds");
                    d.Lags = (int)Num(item, "lags");
                    d.Disconnects = (int)Num(item, "disconnects");
                    d.LinkChanges = (int)Num(item, "linkChanges");
                    days.Add(d);
                }
                days.Sort((a, b) => a.Day.CompareTo(b.Day));
            }
            catch (Exception ex)
            {
                Log.Write("days.json не прочитан: " + ex.Message);
            }
        }

        static double Num(object item, string key)
        {
            var v = Json.Num(Json.Get(item, key));
            return v.HasValue && v.Value > 0 ? v.Value : 0;
        }

        static DateTime At(DateTime day, object item, string key)
        {
            TimeSpan t;
            string s = Json.Str(Json.Get(item, key));
            return s != null && TimeSpan.TryParseExact(s, @"hh\:mm\:ss", Inv, out t) ? day.Date + t : day.Date;
        }

        // Выход: дописать текущую минуту, закрыть идущие эпизоды, сохранить итоги.
        public void Dispose()
        {
            if (pinger != null) pinger.Dispose();
            lock (gate)
            {
                if (readOnly) return;
                var now = DateTime.Now;
                if (outage != null || lag != null) CloseEpisodes(now);
                if (minuteStart != default(DateTime)) FlushMinute(Minute(now));
                SaveDays(now);
            }
        }
    }

    // Пинг в своём потоке: роутер (основной шлюз) и узел в интернете. Результаты забирает журнал в потоке опроса.
    sealed class Pinger : IDisposable
    {
        public struct Result
        {
            public DateTime At;
            public bool Gateway;
            public double Ms;   // -1 — нет ответа
        }

        public readonly System.Collections.Concurrent.ConcurrentQueue<Result> Results = new System.Collections.Concurrent.ConcurrentQueue<Result>();
        public volatile string Gateway;
        readonly string host;
        readonly int intervalMs;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        readonly Thread thread;

        public Pinger(string host, int intervalSec)
        {
            this.host = host;
            intervalMs = Math.Max(1, intervalSec) * 1000;
            thread = new Thread(Loop) { IsBackground = true, Name = "pinger", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        void Loop()
        {
            using (var ping = new Ping())
            {
                var buffer = new byte[32];
                var pace = System.Diagnostics.Stopwatch.StartNew();
                long next = intervalMs;
                // по расписанию, а не «интервал после ответа»: долгие ответы и таймауты не сдвигают замеры
                while (!stop.WaitOne((int)Math.Max(0, next - pace.ElapsedMilliseconds)))
                {
                    next += intervalMs;
                    if (next < pace.ElapsedMilliseconds) next = pace.ElapsedMilliseconds + intervalMs;   // после сна не догонять
                    Send(ping, host, false, buffer);
                    string gw = Gateway;
                    if (gw != null) Send(ping, gw, true, buffer);
                    while (Results.Count > 600)   // журнал не забирает (поток опроса стоит) — не копить
                    {
                        Result drop;
                        Results.TryDequeue(out drop);
                    }
                }
            }
        }

        void Send(Ping ping, string target, bool gateway, byte[] buffer)
        {
            double ms = -1;
            try
            {
                var reply = ping.Send(target, 1000, buffer);
                if (reply != null && reply.Status == IPStatus.Success) ms = reply.RoundtripTime;
            }
            catch (PingException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            Results.Enqueue(new Result { At = DateTime.Now, Gateway = gateway, Ms = ms });
        }

        public void Dispose()
        {
            stop.Set();
            thread.Join(2500);
        }
    }
}
