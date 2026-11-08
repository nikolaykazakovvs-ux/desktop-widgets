using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopWidgets.Limits.Core
{
    // Подмножество windows/limits-widget/src/Model.cs, нужное прототипу. Поля и смысл совпадают с WPF-версией.
    public enum SvcStatus { Ok, Stale, NoData, NeedsPermission, Error, Fixture }

    public sealed class LimitBar
    {
        public QuotaObservation Quota; // типизированные факты подписки; не зависят от подписей UI
        public string Chip;            // тип окна: "5 ч", "7 д"
        public string Label;           // подпись: "Неделя · все модели"
        public double? Used;           // израсходовано, 0..100; null — нет данных (никогда не превращается в 0)
        public DateTime? ResetsAt;     // время сброса
        public string Note;
        public string Tip;
        public bool Placeholder;
    }

    public sealed class ExtraPart
    {
        public string Key, Name;
        public double Percent;
    }

    public sealed class ExtraItem
    {
        public string Label, Value, Detail, Tip;
        public string Accent;          // "info" | "warn" | "ok" | "danger" | "neutral"
        public double? Fill;
        public List<ExtraPart> Parts;
        public DateTime? ExpiresAt;
    }

    public sealed class ServiceSnapshot
    {
        public DateTime? LastAttemptAt;   // UTC: завершение последней попытки опроса
        public bool ObservationTimeUnknown;
        public string Id, Title, Plan, PlanSource, AutoPlan, AutoPlanSource, Url, ProfileUrl, Source;
        public SvcStatus Status;
        public string StatusText, Message, ActionId, ActionLabel;
        public DateTime? DataTime;
        public bool Attention;
        public long Generation;
        public List<ExtraItem> Extras = new List<ExtraItem>();
        public List<LimitBar> Bars = new List<LimitBar>();

        public ServiceSnapshot Clone()
        {
            var c = (ServiceSnapshot)MemberwiseClone();
            c.Extras = Extras.ToList();
            c.Bars = Bars.ToList();
            return c;
        }
    }

    public sealed class ServiceConfig
    {
        public string Id;
        public bool Enabled = false;   // как в публичной WPF-поставке: источники включаются явно
        public string Title;
        public string Plan;            // ручное имя тарифа
        public string Url, ProfileUrl;
        public string Command;         // путь к CLI, если его нет в PATH
        public int TimeoutSeconds = 45;
        public int MaxProcesses;       // Windows: лимит процессов в Job; на macOS/Linux пока не поддерживается

        public static ServiceConfig ClaudeDefaults() => new ServiceConfig
        {
            Id = "claude", Title = "Claude Code",
            Url = "https://claude.ai/settings/usage", ProfileUrl = "https://claude.ai/settings/billing"
        };
    }

    public static class PlanResolver
    {
        public static void Apply(ServiceSnapshot snap, ServiceConfig svc)
        {
            string manual = svc != null && !string.IsNullOrWhiteSpace(svc.Plan) ? svc.Plan.Trim() : null;
            if (manual != null) { snap.Plan = manual; snap.PlanSource = "manual"; }
            else if (!string.IsNullOrWhiteSpace(snap.AutoPlan)) { snap.Plan = snap.AutoPlan; snap.PlanSource = "provider"; }
            else { snap.Plan = null; snap.PlanSource = null; }
            if (svc != null)
            {
                snap.Url = SafeUrl(svc.Url);
                snap.ProfileUrl = SafeUrl(svc.ProfileUrl);
            }
        }

        // Ссылки открываются только по https.
        public static string SafeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri u) || u.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(u.Host)) return null;
            return u.AbsoluteUri;
        }
    }
}
