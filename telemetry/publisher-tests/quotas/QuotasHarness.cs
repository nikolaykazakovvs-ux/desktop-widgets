using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LimitsWidget
{
    static class QuotasHarness
    {
        static string output;
        static int assertions;
        static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
        static ServiceSnapshot Snap(string id, DateTime observed, double used)
        {
            string pool = id == "kimi" ? "subscription.code" : id == "antigravity" ? "subscription.claude_gpt" : "subscription.general";
            var q = QuotaObservation.Create(id, pool, "five_hour", 18000, used, observed.AddHours(2));
            q.MeasuredAt = observed;
            return new ServiceSnapshot { Id = id, Status = SvcStatus.Ok, DataTime = observed, LastAttemptAt = observed,
                Title = "account-canary", Message = "provider-raw-canary", Source = "secret-canary",
                Bars = new List<LimitBar> { new LimitBar { Label = "secret-canary", Used = used, ResetsAt = q.ResetsAt, Quota = q } },
                Extras = new List<ExtraItem> { new ExtraItem { Label = "credits", Value = "secret-canary" } } };
        }
        static string Emit(string name, params ServiceSnapshot[] snaps)
        {
            var json = TelemetryPublisher.Serialize(snaps.Select(s => Json.Serialize(TelemetryPublisher.ProjectProvider(s), false)).ToArray(), 300000, "fixture", DateTime.UtcNow);
            File.WriteAllText(Path.Combine(output, "case-" + name + ".json"), json);
            return json;
        }
        static void Wait(Func<bool> condition, string message, int timeout)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeout);
            while (DateTime.UtcNow < end) { if (condition()) return; Thread.Sleep(20); }
            throw new Exception(message);
        }
        static object Read(string path)
        {
            try { using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) using (var r = new StreamReader(f)) return Json.Parse(r.ReadToEnd()); }
            catch (IOException) { return null; }
        }
        static bool HasProvider(string path, string id, string status)
        {
            var list = Json.Arr(Json.Get(Read(path), "providers"));
            return list != null && list.Any(p => Json.Str(Json.Get(p,"provider_id")) == id && Json.Str(Json.Get(p,"status")) == status);
        }
        sealed class Fake : IProvider
        {
            public string Id { get; set; }
            public int Calls;
            public bool Error;
            public ManualResetEvent Hold;
            public DateTime Completed;
            public ServiceSnapshot Collect(ProviderContext ctx)
            {
                Interlocked.Increment(ref Calls);
                if (Hold != null && !Hold.WaitOne(4000)) throw new Exception("fake_timeout");
                if (Error) throw new Exception("synthetic_failure");
                var snap = Snap(Id, ctx.UtcNow(), 37);
                ProviderUtil.MarkFresh(snap, ctx, false);
                Completed = snap.DataTime.Value;
                return snap;
            }
        }
        static void ParserFixtures()
        {
            var fixtures = Path.Combine(output,"payloads"); Directory.CreateDirectory(fixtures);
            File.WriteAllText(Path.Combine(fixtures,"codex-ratelimits.json"), "{\"result\":{\"rateLimits\":{\"primary\":{\"windowDurationMins\":300,\"usedPercent\":25,\"resetsAt\":1893456000},\"secondary\":{\"windowDurationMins\":10080,\"usedPercent\":40,\"resetsAt\":1893456000}}}}");
            File.WriteAllText(Path.Combine(fixtures,"claude-get-usage.json"), "{\"cache_status\":\"cached\",\"rate_limits\":{\"five_hour\":{\"utilization\":80},\"seven_day\":{\"utilization\":90},\"seven_day_opus\":{\"utilization\":50}}}");
            File.WriteAllText(Path.Combine(fixtures,"kimi-server-usage.json"), "{\"data\":{\"kind\":\"ok\",\"quota\":{\"usages\":{\"limit5h\":{\"usedRatio\":0.5},\"monthTotal\":{\"usedRatio\":0.7},\"monthCode\":{\"usedRatio\":0.2}}}}}");
            File.WriteAllText(Path.Combine(fixtures,"antigravity-usage.json"), "{\"command\":{\"data\":{\"groups\":[{\"name\":\"Gemini Models\",\"buckets\":[{\"window\":\"5h\",\"remaining_fraction\":0.2}]},{\"name\":\"Claude and GPT models\",\"buckets\":[{\"window\":\"weekly\",\"remaining_fraction\":0.4}]}]}}}");
            // Optional credits fixture is complete too; no provider fallback is permitted.
            File.WriteAllText(Path.Combine(fixtures,"antigravity-credits.json"), "{}");
            var cfg = new Config(); var snaps = new List<ServiceSnapshot>();
            foreach (var provider in new IProvider[] { new CodexProvider(), new ClaudeProvider(), new KimiProvider(), new AntigravityProvider() })
            {
                var ctx = new ProviderContext { Config=cfg, Svc=cfg.Svc(provider.Id), Now=DateTime.Now, FixturesDir=fixtures, FixtureOnly=true };
                var s = provider.Collect(ctx); s.LastAttemptAt=DateTime.UtcNow; snaps.Add(s);
                Check(s.Status==SvcStatus.Fixture, "parser fixture status " + provider.Id);
            }
            Check(snaps[1].Bars.All(b=>b.Quota==null || b.Quota.MeasuredAt==null),"cached Claude is not fresh");
            Check(snaps[2].Bars.All(b=>b.Quota==null || b.Quota.MeasuredAt.HasValue),"Kimi query completion is a source observation");
            Check(snaps[2].Bars.Any(b=>b.Quota!=null && b.Quota.PoolId=="subscription.shared"),"Kimi month shared identity");
            Check(snaps[3].Bars.Any(b=>b.Quota!=null && QuotaObservation.Models("antigravity",b.Quota.PoolId).Length==2),"Antigravity Claude/GPT share a pool");
            Emit("adapter-pools",snaps.ToArray());
            File.WriteAllText(Path.Combine(fixtures,"claude-get-usage.json"), "{\"rate_limits\":{\"five_hour\":{\"utilization\":80}}}");
            var noProvenance = new ClaudeProvider().Collect(new ProviderContext { Config=cfg, Svc=cfg.Svc("claude"), Now=DateTime.Now, FixturesDir=fixtures, FixtureOnly=true });
            noProvenance.LastAttemptAt=DateTime.UtcNow;
            Check(noProvenance.Bars.All(b=>b.Quota==null || b.Quota.MeasuredAt==null),"Claude without cache_status still has unknown source age");
            Emit("claude-no-provenance",noProvenance);
            var missing=Path.Combine(output,"missing-fixtures"); Directory.CreateDirectory(missing);
            foreach(var provider in new IProvider[]{new CodexProvider(),new ClaudeProvider(),new KimiProvider(),new AntigravityProvider()})
            {
                bool stopped=false;
                try { provider.Collect(new ProviderContext{Config=cfg,Svc=cfg.Svc(provider.Id),FixturesDir=missing,FixtureOnly=true,Now=DateTime.Now}); }
                catch(InvalidOperationException e){ stopped=e.Message=="fixture_missing"; }
                Check(stopped,"missing fixture fails closed " + provider.Id);
            }
        }
        static void MainChecks()
        {
            DateTime old=DateTime.UtcNow.AddHours(-2);
            var s=Snap("codex",old,77); s.Bars[0].Quota.ResetsAt=DateTime.UtcNow.AddMinutes(-5);
            s.Bars[0].Used=0; s.Bars[0].Label="mutated UI label";
            var reset=Emit("reset-pending",s);
            Check(reset.Contains("77"),"visual reset preserves observation");
            var cache=ServiceSnapshot.FromDict(s.ToDict()); cache.Status=SvcStatus.Stale;cache.LastAttemptAt=DateTime.UtcNow;
            Emit("cached-error",cache); Check(cache.Bars[0].Quota.MeasuredAt.HasValue,"typed metadata roundtrip");
            var legacy=Snap("claude",old,3);legacy.Bars[0].Quota=null;Emit("legacy-unknown",legacy);
            var unknown=Snap("kimi",old,2);unknown.Bars[0].Quota.MeasuredAt=null;Emit("unknown",unknown);
            Emit("partial",Snap("codex",old,12),unknown);
            var cachedOk=Snap("claude",old,65);cachedOk.ObservationTimeUnknown=true;
            ProviderUtil.MarkFresh(cachedOk,new ProviderContext{Now=DateTime.Now},false);cachedOk.LastAttemptAt=DateTime.UtcNow;
            Check(cachedOk.Bars[0].Quota.MeasuredAt==old,"successful cached read rejuvenated original time");
            Emit("cached-ok",cachedOk);
            ParserFixtures();

            var cfg=new Config();cfg.Order=new List<string>{"codex","claude"};cfg.Svc("codex").Enabled=true;cfg.Svc("claude").Enabled=true;
            var state=new State(); var fast=new Fake{Id="codex"};var slow=new Fake{Id="claude",Hold=new ManualResetEvent(false)};
            using(var pub=new TelemetryPublisher(Path.Combine(output,"publisher"),true,300))
            {
                var collector=new Collector(cfg,state,null,pub,new IProvider[]{fast,slow},null);
                DateTime batch=DateTime.UtcNow;
                var work=Task.Factory.StartNew(()=>collector.Collect());
                try
                {
                    Wait(()=>HasProvider(pub.TargetPath,"codex","ok"),"fast provider blocked by slow provider: "+pub.LastError,3500);
                    Check(!work.IsCompleted,"fast published before batch complete");
                    Thread.Sleep(80);slow.Hold.Set();Check(work.Wait(4000),"batch completion");
                    Wait(()=>HasProvider(pub.TargetPath,"claude","ok"),"slow completion not published",3500);
                    Check(slow.Completed>batch.AddMilliseconds(50),"measurement is completion, not batch start");
                    // Simultaneous completions and heartbeat: every latest provider must survive.
                    Parallel.Invoke(()=>pub.Accept(Snap("codex",DateTime.UtcNow.AddSeconds(-1),11)),()=>pub.Accept(Snap("claude",DateTime.UtcNow.AddSeconds(-1),22)),()=>pub.Accept(Snap("kimi",DateTime.UtcNow.AddSeconds(-1),33)),()=>pub.Heartbeat());
                    Wait(()=>HasProvider(pub.TargetPath,"kimi","ok"),"parallel provider update lost",3500);
                    var before=Read(pub.TargetPath);var original=Json.Str(Json.Get(before,"measured_at"));var count=pub.PublishedCount;
                    Wait(()=>pub.PublishedCount>count,"production 10s heartbeat missing",11500);
                    var after=Read(pub.TargetPath);
                    Check(Json.Str(Json.Get(after,"measured_at"))==original,"heartbeat rejuvenated measurements");
                    Check(fast.Calls==1 && slow.Calls==1,"heartbeat called provider");
                    Check(HasProvider(pub.TargetPath,"codex","ok") && HasProvider(pub.TargetPath,"claude","ok"),"parallel state lost");
                    File.WriteAllText(Path.Combine(output,"case-heartbeat.json"),Json.Serialize(after,false));
                    fast.Error=true;slow.Error=true;collector.Collect();
                    Wait(()=>HasProvider(pub.TargetPath,"codex","partial"),"cache fallback not published",3500);
                    File.WriteAllText(Path.Combine(output,"case-collector-cache-error.json"),Json.Serialize(Read(pub.TargetPath),false));
                }
                finally {slow.Hold.Set();}
            }
        }
        public static int Main(string[] args)
        {
            output=Path.GetFullPath(args[0]);Program.BaseDir=output;
            try {MainChecks(); Console.WriteLine("PASS: "+assertions+" C# assertions; fake providers only");return 0;}
            catch(Exception e){Console.Error.WriteLine("FAIL: "+e.Message);return 1;}
        }
    }
}
