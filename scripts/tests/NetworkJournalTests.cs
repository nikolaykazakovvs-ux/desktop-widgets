// Integration test for the release: synthetic network samples and ping replies only.
// No packets, drivers, real adapters, registry changes or widget UI are used.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using ResourcesWidget;

static class NetworkJournalTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static int Main(string[] args)
    {
        string output = args[0];
        Directory.CreateDirectory(output);
        var cfg = new Config();
        Check(cfg.PingHost == "1.1.1.1", "Internet ping default was lost");
        Check(cfg.LhmUrl == "http://127.0.0.1:8085/data.json", "Local sensor default was lost");
        cfg.PingHost = ""; // no real pinger thread
        var journal = new NetJournal(output, false, cfg);
        DateTime at = DateTime.Now.Date.AddHours(12);
        var net = new NetInfo { Name = "Release-test-adapter", LinkBps = 1000000000, DownBps = 1000000, UpBps = 250000 };
        NetHealth health;
        var firstDay = journal.Update(net, at, 1, out health);
        for (int i = 1; i < 60; i++) firstDay = journal.Update(net, at.AddSeconds(i), 1, out health);
        Check(firstDay.MinuteDown > 0 && firstDay.Down > 0, "Second and minute peaks were not recorded");
        at = at.AddMinutes(1);
        net.LinkBps = 100000000;
        journal.Update(net, at.AddSeconds(1), 1, out health);
        journal.Update(null, at.AddSeconds(2), 1, out health);
        journal.Update(net, at.AddSeconds(4), 1, out health);

        // An in-memory queue replaces Pinger; no constructor or thread is started.
        var pinger = (Pinger)FormatterServices.GetUninitializedObject(typeof(Pinger));
        var replies = new ConcurrentQueue<Pinger.Result>();
        typeof(Pinger).GetField("Results").SetValue(pinger, replies);
        pinger.Gateway = "127.0.0.1";
        FieldInfo field = typeof(NetJournal).GetField("pinger", BindingFlags.NonPublic | BindingFlags.Instance);
        field.SetValue(journal, pinger);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                replies.Enqueue(new Pinger.Result { At = at.AddSeconds(5 + i), Gateway = true, Ms = 1 });
                replies.Enqueue(new Pinger.Result { At = at.AddSeconds(5 + i), Ms = -1 });
            }
            replies.Enqueue(new Pinger.Result { At = at.AddSeconds(9), Ms = 10 });
            for (int i = 0; i < 3; i++) replies.Enqueue(new Pinger.Result { At = at.AddSeconds(10 + i), Ms = 250 });
            replies.Enqueue(new Pinger.Result { At = at.AddSeconds(14), Ms = 10 });
            journal.Update(net, at.AddSeconds(15), 1, out health);
            Check(health.PingOn, "Synthetic ping replies were not enabled");
            // A second outage with gateway failures must be diagnosed as local-network failure.
            for (int i = 0; i < 3; i++) replies.Enqueue(new Pinger.Result { At = at.AddSeconds(20 + i), Ms = -1 });
            replies.Enqueue(new Pinger.Result { At = at.AddSeconds(24), Gateway = true, Ms = -1 });
            replies.Enqueue(new Pinger.Result { At = at.AddSeconds(25), Ms = 10 });
            journal.Update(net, at.AddSeconds(26), 1, out health);
            journal.Update(net, at.AddMinutes(1), 1, out health);
        }
        finally
        {
            field.SetValue(journal, null);
            journal.Dispose();
        }
        string events = File.ReadAllText(Path.Combine(output, "events.csv"));
        foreach (string kind in new[] { ";link;", ";down;", ";internet;", ";gateway;", ";lag;" })
            Check(events.Contains(kind), "Missing event: " + kind);
        string minutes = File.ReadAllText(Path.Combine(output, at.ToString("yyyy-MM-dd") + ".csv"));
        Check(minutes.Contains("pingSent;pingLost;pingAvgMs;pingMaxMs;gwSent;gwLost;gwAvgMs"), "Ping columns are missing");
        Check(File.Exists(Path.Combine(output, "days.json")), "Daily network summary was not persisted");
        using (var reloaded = new NetJournal(output, true, cfg))
        {
            string adapter;
            long link;
            reloaded.Current(out adapter, out link);
            Check(!double.IsNaN(reloaded.ReadDay(at).AvgDown[12 * 60]), "Persisted minute history cannot be read");
        }
        Console.WriteLine("PASS: network link changes, disconnects, internet/router outages, high ping, minute CSV, daily JSON and history reload.");
        return 0;
    }
}
