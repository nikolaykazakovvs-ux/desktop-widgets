using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DesktopWidgets.Limits.Core;
using DesktopWidgets.Limits.Core.Claude;
using Xunit;

namespace DesktopWidgets.Limits.Tests
{
    public class JsonTests
    {
        [Fact]
        public void ParsesIntoWpfShapes()
        {
            var o = Json.Obj(Json.Parse("{\"a\":1,\"b\":12.5,\"c\":[true,null,\"x\"],\"d\":{\"e\":9007199254740991}}"));
            Assert.IsType<int>(o["a"]);
            Assert.IsType<double>(o["b"]);
            Assert.IsType<object[]>(o["c"]);
            Assert.IsType<long>(Json.Get(o, "d.e"));
            Assert.Null(Json.TryParse("{broken"));
        }

        [Fact]
        public void SerializeMatchesWpfFormat()
        {
            var d = new Dictionary<string, object> { { "i", 30 }, { "d", 12.5 }, { "w", 70.0 }, { "n", null }, { "s", "a\"b\n" }, { "nan", double.NaN }, { "arr", new object[] { 1, "x" } } };
            Assert.Equal("{\"i\":30,\"d\":12.5,\"w\":70,\"n\":null,\"s\":\"a\\\"b\\n\",\"nan\":null,\"arr\":[1,\"x\"]}", Json.Serialize(d, false));
        }

        [Fact]
        public void TimeAcceptsIsoAndEpoch()
        {
            var a = Json.Time("2030-01-01T00:00:00Z").Value.ToUniversalTime();
            var b = Json.Time(1893456000000L).Value.ToUniversalTime();
            Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), a);
            Assert.Equal(a, b);
        }
    }

    public class ClaudeParserTests
    {
        static readonly DateTime Now = DateTime.Now;

        [Fact]
        public void MissingUtilizationStaysUnknownAndUnconfirmedPoolNotGuessed()
        {
            var data = Json.Parse("{\"rate_limits\":{\"five_hour\":{\"resets_at\":\"2030-01-01T00:00:00Z\"},\"seven_day\":{\"utilization\":30},"
                + "\"model_scoped\":[{\"display_name\":\"Fable\",\"resets_at\":\"2030-01-01T00:00:00Z\"},{\"display_name\":\"Opus 9\",\"key\":\"seven_day_opus\",\"utilization\":12}]}}");
            var s = new ServiceSnapshot { Id = "claude" };
            Assert.True(ClaudeParser.FillFromUsage(s, data, Now));
            Assert.False(s.Bars.First(b => b.Label.StartsWith("Сессия")).Used.HasValue);
            var fable = s.Bars.First(b => b.Label.Contains("Fable"));
            Assert.False(fable.Used.HasValue);
            Assert.Null(fable.Quota);
            Assert.Equal("subscription.opus", s.Bars.First(b => b.Label.Contains("Opus 9")).Quota.PoolId);
            Assert.True(s.ObservationTimeUnknown);
        }

        [Fact]
        public void NoRateLimitsLeavesSnapshotUntouched()
        {
            var s = new ServiceSnapshot { Id = "claude" };
            Assert.False(ClaudeParser.FillFromUsage(s, Json.Parse("{\"rate_limits_available\":false}"), Now));
            Assert.Empty(s.Bars);
        }

        [Fact]
        public void UsageTextFallback()
        {
            var bars = ClaudeParser.ParseUsageText("Current session: 2% used · resets 4:19pm (Europe/Kyiv)\nCurrent week (all models): 29% used · resets Sep 28, 7:59pm (Europe/Kyiv)\nCurrent week (Opus): 42% used", Now);
            Assert.Equal(3, bars.Count);
            Assert.Equal("five_hour", bars[0].Quota.WindowId);
            Assert.Equal(29, bars[1].Used);
            Assert.Equal("subscription.opus", bars[2].Quota.PoolId);
            Assert.NotNull(bars[0].ResetsAt);
        }

        [Fact]
        public void AuthKeepsOnlyPlan()
        {
            var r = ClaudeParser.ParseAuth("{\"loggedIn\":true,\"subscriptionType\":\"max\",\"email\":\"canary.claude@example.test\"}");
            Assert.True(r.Answered);
            Assert.Equal("Max", r.Plan);
            Assert.False(ClaudeParser.ParseAuth("plain text").Answered);
            Assert.Null(ClaudeParser.ParseAuth("{\"loggedIn\":false,\"subscriptionType\":\"max\"}").Plan);
            Assert.Null(ClaudeParser.PlanName("Max <script>"));
        }

        [Fact]
        public void DesktopHistoryHasRealObservationTime()
        {
            long t = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
            var s = new ServiceSnapshot { Id = "claude", ObservationTimeUnknown = true };
            Assert.True(ClaudeParser.FillFromDesktopHistory(s, "{\"samples\":[{\"t\":" + t + ",\"u\":{\"fh\":10,\"sd\":20}}]}", DateTime.Now));
            Assert.False(s.ObservationTimeUnknown);
            Assert.All(s.Bars, b => Assert.NotNull(b.Quota.MeasuredAt));
            long old = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds();
            Assert.False(ClaudeParser.FillFromDesktopHistory(new ServiceSnapshot(), "{\"samples\":[{\"t\":" + old + ",\"u\":{\"fh\":10}}]}", DateTime.Now));
        }
    }

    public class ProjectionTests
    {
        static ServiceSnapshot Snap(SvcStatus status, params LimitBar[] bars)
        {
            var s = new ServiceSnapshot { Id = "claude", Status = status, LastAttemptAt = DateTime.UtcNow };
            s.Bars.AddRange(bars);
            return s;
        }

        static LimitBar Bar(string pool, string window, double? used, bool measured)
        {
            var q = QuotaObservation.Create("claude", pool, window, null, used, DateTime.UtcNow.AddHours(2));
            if (measured) q.MeasuredAt = DateTime.UtcNow.AddSeconds(-1);
            return new LimitBar { Label = "x", Used = used, Quota = q };
        }

        [Fact]
        public void MeasuredWindowsAreOk()
        {
            var p = QuotasProjection.ProjectProvider(Snap(SvcStatus.Ok, Bar("subscription.general", "five_hour", 30, true), Bar("subscription.general", "seven_day", 50, true)));
            Assert.Equal("ok", p["status"]);
            Assert.Equal("none", p["reason"]);
            var w = (Dictionary<string, object>)((List<object>)((Dictionary<string, object>)((List<object>)p["pools"])[0])["windows"])[0];
            Assert.Equal(70.0, w["remaining_percent"]);
        }

        [Fact]
        public void UnknownObservationTimeIsNeverOk()
        {
            var p = QuotasProjection.ProjectProvider(Snap(SvcStatus.Ok, Bar("subscription.general", "seven_day", 86, false)));
            Assert.Equal("partial", p["status"]);
            var w = (Dictionary<string, object>)((List<object>)((Dictionary<string, object>)((List<object>)p["pools"])[0])["windows"])[0];
            Assert.Equal("unknown", w["status"]);
            Assert.Null(w["used_percent"]);
        }

        [Fact]
        public void LabelsPlansAndAccountDataNeverExported()
        {
            var s = Snap(SvcStatus.Ok, Bar("subscription.general", "seven_day", 10, true), new LimitBar { Label = "Неделя · Fable", Used = 100 });
            s.Plan = "Max"; s.AutoPlan = "Max"; s.Message = "canary.claude@example.test";
            string text = QuotasProjection.Serialize(new[] { Json.Serialize(QuotasProjection.ProjectProvider(s), false) }, 300000, "live", DateTime.UtcNow);
            foreach (var canary in new[] { "Fable", "Max", "canary", "Неделя" }) Assert.DoesNotContain(canary, text);
        }

        [Fact]
        public void WritesContractCasesForSchemaValidation()
        {
            // CI проверяет эти файлы telemetry/tests/contract.py (тот же валидатор, что и для WPF-версии).
            string dir = Environment.GetEnvironmentVariable("XPLAT_CONTRACT_OUT");
            if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetTempPath(), "xplat-contract-" + Environment.ProcessId);
            Directory.CreateDirectory(dir);
            var cases = new Dictionary<string, ServiceSnapshot>
            {
                { "ok", Snap(SvcStatus.Ok, Bar("subscription.general", "five_hour", 30, true), Bar("subscription.general", "seven_day", 50, true)) },
                { "unknown-time", Snap(SvcStatus.Ok, Bar("subscription.general", "seven_day", 86, false)) },
                { "error", Snap(SvcStatus.Error, ClaudeParser.Placeholders()) },
            };
            foreach (var c in cases)
            {
                string json = QuotasProjection.Serialize(new[] { Json.Serialize(QuotasProjection.ProjectProvider(c.Value), false) }, 300000, "fixture", DateTime.UtcNow);
                File.WriteAllText(Path.Combine(dir, "case-" + c.Key + ".json"), json);
            }
            string empty = QuotasProjection.Serialize(new[] { Json.Serialize(QuotasProjection.EmptyProvider("claude"), false) }, 300000, "fixture", DateTime.UtcNow);
            File.WriteAllText(Path.Combine(dir, "case-unavailable.json"), empty);
            Assert.Equal(4, Directory.GetFiles(dir, "case-*.json").Length);
        }
    }

    public class PlacementTests
    {
        static readonly Rect Main = new Rect(0, 0, 2560, 1400);
        static readonly Rect Left = new Rect(-1920, 200, 1920, 1040);

        [Fact]
        public void StaysOnSecondMonitorWithNegativeCoordinates()
        {
            var r = Placement.Fit(new Rect(-1500, 300, 340, 400), new[] { Main, Left }, 0);
            Assert.Equal(-1500, r.X);
            Assert.Equal(300, r.Y);
        }

        [Fact]
        public void DisconnectedMonitorMovesToPrimary()
        {
            var r = Placement.Fit(new Rect(-1500, 300, 340, 400), new[] { Main }, 0);
            Assert.True(r.X >= Main.X && r.Right <= Main.Right);
        }

        [Fact]
        public void StraddlingWindowGoesToMonitorWithLargestOverlap()
        {
            var r = Placement.Fit(new Rect(-100, 300, 340, 400), new[] { Main, Left }, 0);
            Assert.True(r.X >= 0, r.ToString());
        }

        [Fact]
        public void PartlyBelowWorkAreaIsPulledUp()
        {
            var r = Placement.Fit(new Rect(100, 1300, 340, 400), new[] { Main }, 0);
            Assert.True(r.Bottom <= Main.Bottom);
        }

        [Fact]
        public void DefaultIsTopRightOfPrimary()
        {
            var r = Placement.Default(340, 400, new[] { Left, Main }, 1);
            Assert.True(r.Right <= Main.Right && r.X > Main.Right - 400 && r.Y < 50);
        }
    }
}
