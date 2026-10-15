using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageWidget;

internal sealed record DailyUsage(double UsedPercentagePoints, double CyclePercentagePoints, DateTimeOffset ObservedSince,
    DateTimeOffset MeasuredAt, DateTimeOffset SourceAt, DateTimeOffset NextMeasurementAt,
    double RecentPercentagePoints, int WindowMinutes, bool IsCurrentDay);

// These are observed quota changes, not token counts or a provider's daily billing total.
internal sealed class DailyUsageTracker
{
    private readonly string _path;
    private readonly TimeZoneInfo _zone;
    private Dictionary<string, DailyAccount> _accounts = [];

    internal DailyUsageTracker(string directory, TimeZoneInfo? zone = null)
    {
        _path = Path.Combine(directory, "daily-usage.json");
        _zone = zone ?? TimeZoneInfo.Local;
        try
        {
            if (File.Exists(_path)) _accounts = JsonSerializer.Deserialize<Dictionary<string, DailyAccount>>(File.ReadAllText(_path), WidgetStorage.Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { WidgetStorage.Log("Daily usage restore failed", ex); }
    }

    internal ProviderUsage Observe(ProviderUsage usage, string? identity, DateTimeOffset now, TimeSpan? measurementInterval = null, int windowMinutes = 30)
    {
        if (identity is null || usage.Status != "ready" || usage.UpdatedAt is not { } sample || sample > now.AddMinutes(1)) return usage;
        var day = Date(sample);
        // A cached Claude snapshot from yesterday must not become today's baseline.
        if (day != Date(now)) return usage;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var changed = false;
        Dictionary<string, DailyWindow>? previousDay = null;
        if (!_accounts.TryGetValue(usage.Id, out var account) || account.Identity != fingerprint || account.Day != day)
        {
            if (account is not null && account.Identity == fingerprint) previousDay = account.Windows;
            account = new DailyAccount { Identity = fingerprint, Day = day };
            _accounts[usage.Id] = account;
            changed = true;
        }
        var windows = new List<QuotaWindow>();
        foreach (var quota in usage.Windows)
        {
            if (!double.IsFinite(quota.UsedPercent) || quota.UsedPercent < 0 || quota.ResetsAt is { } reset && reset <= now.ToUnixTimeSeconds())
            { windows.Add(quota); continue; }
            var used = Math.Clamp(quota.UsedPercent, 0, 100);
            if (!account.Windows.TryGetValue(quota.Key, out var history))
            {
                history = new DailyWindow { Label = quota.Label, LastUsed = used, ResetsAt = quota.ResetsAt, LastSample = sample, ObservedSince = sample };
                if (previousDay?.TryGetValue(quota.Key, out var previous) == true && previous.Label == quota.Label &&
                    !WasReplenished(previous, used, quota.ResetsAt, sample))
                {
                    // Keep rolling history and the measurement clock across midnight.
                    // Only the calendar-day accumulator and its painted segment reset.
                    history.Changes = previous.Changes;
                    history.ReportedAt = previous.ReportedAt;
                    history.ReportedSourceAt = previous.ReportedSourceAt;
                    history.ReportedRecentSpent = previous.ReportedRecentSpent;
                    history.ReportedWindowMinutes = previous.ReportedWindowMinutes;
                }
                account.Windows[quota.Key] = history;
                changed = true;
            }
            else if (sample > history.LastSample)
            {
                if (history.Label != quota.Label || WasReplenished(history, used, quota.ResetsAt, sample))
                {
                    // Replenishment starts a new baseline and clears measured/pending
                    // consumption immediately, independently of the measurement interval.
                    history = new DailyWindow { Label = quota.Label, LastUsed = used, ResetsAt = quota.ResetsAt, LastSample = sample, ObservedSince = sample };
                    account.Windows[quota.Key] = history;
                }
                else
                {
                    var increase = Math.Max(0, used - history.LastUsed);
                    history.Spent += increase;
                    history.CycleSpent += increase;
                    if (increase > 0) history.Changes.Add(new(sample, increase));
                    // Keep a high-water mark so rounding/refunds cannot count the same consumption twice.
                    history.LastUsed = Math.Max(history.LastUsed, used);
                    history.ResetsAt = quota.ResetsAt;
                }
                history.LastSample = sample;
                changed = true;
            }
            var interval = measurementInterval ?? TimeSpan.Zero;
            // Two days cover the longest comparison window at the previous daily
            // measurement. Event timestamps represent receipt of quota changes.
            if (history.Changes.RemoveAll(point => point.At < now.AddDays(-2)) > 0) changed = true;
            // Keep observing resets and corrections at the source cadence, but expose
            // today's cumulative total only on the user's chosen measurement schedule.
            var newObservation = history.ReportedSourceAt is null || history.LastSample > history.ReportedSourceAt;
            if (history.ReportedAt is null || newObservation && now - history.ReportedAt.Value >= interval)
            {
                history.ReportedSpent = history.Spent;
                history.ReportedCycleSpent = history.CycleSpent;
                history.ReportedAt = now;
                history.ReportedSourceAt = history.LastSample;
                history.ReportedRecentSpent = SumChanges(history, now, windowMinutes);
                history.ReportedWindowMinutes = windowMinutes;
                changed = true;
            }
            else if (history.ReportedWindowMinutes != windowMinutes)
            {
                // Changing the comparison window reuses the latest measured time;
                // it never silently advances the daily measurement schedule.
                history.ReportedRecentSpent = SumChanges(history, history.ReportedAt.Value, windowMinutes);
                history.ReportedWindowMinutes = windowMinutes;
                changed = true;
            }
            // Out-of-order data does not borrow a later observation's daily amount.
            windows.Add(sample < history.LastSample ? quota : quota with { Today = new(history.ReportedSpent, history.ReportedCycleSpent,
                history.ObservedSince, history.ReportedAt!.Value, history.ReportedSourceAt!.Value, history.ReportedAt.Value + interval,
                history.ReportedRecentSpent, windowMinutes, Date(history.ReportedAt.Value) == day) });
        }
        if (changed)
        {
            try { WidgetStorage.Write(_path, _accounts); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WidgetStorage.Log("Daily usage save failed", ex); }
        }
        return usage with { Windows = windows };
    }

    private string Date(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, _zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    private static bool WasReplenished(DailyWindow history, double used, long? reset, DateTimeOffset sample)
    {
        // A reset ticket/provider refill may leave the reset timestamp unchanged.
        if (used == 0 && (history.LastUsed > 0 || history.Spent > 0 || history.Changes.Count > 0)) return true;
        if (history.ResetsAt is not { } previous || reset is not { } next) return false;
        // A completed cycle can already have consumption again by the next poll.
        if (previous <= sample.ToUnixTimeSeconds() && next > previous) return true;
        // Ignore small deadline jitter and ordinary percentage corrections.
        return used < history.LastUsed && Math.Abs((double)next - previous) >= 60;
    }
    private static double SumChanges(DailyWindow history, DateTimeOffset measuredAt, int minutes) =>
        history.Changes.Where(point => point.At > measuredAt.AddMinutes(-minutes) && point.At <= measuredAt).Sum(point => point.Points);
    internal sealed record ConsumptionPoint(DateTimeOffset At, double Points);

    internal sealed class DailyAccount
    {
        public string Identity { get; set; } = "";
        public string Day { get; set; } = "";
        public Dictionary<string, DailyWindow> Windows { get; set; } = [];
    }
    internal sealed class DailyWindow
    {
        public string Label { get; set; } = "";
        public double LastUsed { get; set; }
        public long? ResetsAt { get; set; }
        public DateTimeOffset LastSample { get; set; }
        public DateTimeOffset ObservedSince { get; set; }
        public double Spent { get; set; }
        public double CycleSpent { get; set; }
        public double ReportedSpent { get; set; }
        public double ReportedCycleSpent { get; set; }
        public DateTimeOffset? ReportedAt { get; set; }
        public DateTimeOffset? ReportedSourceAt { get; set; }
        public List<ConsumptionPoint> Changes { get; set; } = [];
        public double ReportedRecentSpent { get; set; }
        public int ReportedWindowMinutes { get; set; }
    }
}
