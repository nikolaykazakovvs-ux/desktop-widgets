using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ResourcesWidget
{
    // Веб-сервер LibreHardwareMonitor 0.9.x: GET /data.json — дерево узлов {Text, Value, RawValue, SensorId, Type, Children}.
    // Опрос в своём потоке раз в 2 с, чтобы зависший LHM не тормозил виджет. Дерево читают датчик температуры
    // процессора и видеокарта; после разбора оно не меняется, поэтому читать его можно из любого потока.
    sealed class LhmClient : IDisposable
    {
        const double MaxAgeSeconds = 5;   // старше — LibreHardwareMonitor закрыт или завис

        readonly string url;
        readonly Timer timer;
        readonly object gate = new object();
        object root;
        DateTime at;
        int busy;
        bool errorLogged;

        // url — адрес data.json; пусто — не опрашивать.
        public LhmClient(string url)
        {
            this.url = url;
            if (!string.IsNullOrEmpty(url)) timer = new Timer(_ => Poll(), null, 0, 2000);
        }

        // Последнее дерево показаний; null — LibreHardwareMonitor не отвечает. Прежнее дерево не отдаётся.
        public object Root
        {
            get
            {
                lock (gate)
                    return root != null && (DateTime.UtcNow - at).TotalSeconds <= MaxAgeSeconds ? root : null;
            }
        }

        void Poll()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                object tree = null;
                try { tree = Json.TryParse(Fetch()); }
                catch (WebException) { }
                catch (IOException) { }
                lock (gate)
                {
                    root = tree;
                    at = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                lock (gate) root = null;
                if (!errorLogged) Log.Write("lhm: " + ex.GetType().Name + ": " + ex.Message);
                errorLogged = true;
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
            }
        }

        string Fetch()
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Proxy = null;          // только этот компьютер, без системного прокси
            req.Timeout = 1500;
            req.ReadWriteTimeout = 1500;
            req.KeepAlive = false;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        public void Dispose()
        {
            if (timer != null) timer.Dispose();
        }
    }

    static class Lhm
    {
        static readonly Regex Number = new Regex(@"-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

        // Значение датчика — строка с единицей в локали LHM («52,4 °C»); пустое число — датчик сейчас не читается.
        public static bool TryValue(IDictionary<string, object> sensor, out double v)
        {
            v = 0;
            string s = Json.Str(Json.Get(sensor, "RawValue")) ?? Json.Str(Json.Get(sensor, "Value"));
            if (string.IsNullOrEmpty(s)) return false;
            var m = Number.Match(s);
            return m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
