using System;
using System.Collections.Generic;
using System.Linq;

namespace LimitsWidget
{
    // Only adapter-owned constants cross the public boundary. UI text and raw keys never become IDs.
    sealed class QuotaObservation
    {
        public string PoolId, WindowId;
        public long? DurationSeconds;
        public double? UsedPercent;
        public DateTime? ResetsAt, MeasuredAt;

        public static string Window(double? minutes)
        {
            return minutes == 300 ? "five_hour" : minutes == 10080 ? "seven_day" : minutes == 1440 ? "daily" : null;
        }

        public static string[] Models(string provider, string pool)
        {
            if (provider == "codex" && pool == "subscription.general") return new[] { "codex.general" };
            if (provider == "codex" && pool == "subscription.reserve") return new[] { "codex.reserve" };
            if (provider == "claude" && pool == "subscription.general") return new[] { "claude.opus", "claude.sonnet", "claude.haiku" };
            if (provider == "claude" && pool == "subscription.opus") return new[] { "claude.opus" };
            if (provider == "claude" && pool == "subscription.sonnet") return new[] { "claude.sonnet" };
            if (provider == "claude" && pool == "subscription.cowork") return new[] { "claude.cowork" };
            if (provider == "claude" && pool == "subscription.oauth_apps") return new[] { "claude.oauth_apps" };
            if (provider == "antigravity" && pool == "subscription.gemini") return new[] { "gemini.general" };
            if (provider == "antigravity" && pool == "subscription.claude_gpt") return new[] { "claude.general", "gpt.general" };
            if (provider == "kimi" && pool == "subscription.code") return new[] { "kimi.code" };
            if (provider == "kimi" && pool == "subscription.shared") return new[] { "kimi.code", "kimi.work", "kimi.claw" };
            return null;
        }

        public static QuotaObservation Create(string provider, string pool, string window, long? duration, double? used, DateTime? reset)
        {
            if (Models(provider, pool) == null || !new[] { "five_hour", "seven_day", "daily", "month" }.Contains(window)) return null;
            if (used.HasValue && (double.IsNaN(used.Value) || double.IsInfinity(used.Value) || used < 0 || used > 100)) used = null;
            duration = window == "five_hour" ? 18000 : window == "seven_day" ? 604800 : window == "daily" ? 86400 : (long?)null;
            return new QuotaObservation { PoolId = pool, WindowId = window, DurationSeconds = duration,
                UsedPercent = used, ResetsAt = reset.HasValue ? reset.Value.ToUniversalTime() : (DateTime?)null };
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> { { "poolId", PoolId }, { "windowId", WindowId }, { "durationSeconds", DurationSeconds },
                { "usedPercent", UsedPercent }, { "resetsAt", ResetsAt }, { "measuredAt", MeasuredAt } };
        }

        public static QuotaObservation FromDict(object value)
        {
            if (Json.Obj(value) == null) return null;
            return new QuotaObservation { PoolId = Json.Str(Json.Get(value, "poolId")), WindowId = Json.Str(Json.Get(value, "windowId")),
                DurationSeconds = Json.Num(Json.Get(value, "durationSeconds")).HasValue ? (long?)Json.Num(Json.Get(value, "durationSeconds")).Value : null,
                UsedPercent = Json.Num(Json.Get(value, "usedPercent")), ResetsAt = Json.Time(Json.Get(value, "resetsAt")),
                MeasuredAt = Json.Time(Json.Get(value, "measuredAt")) };
        }
    }
}
