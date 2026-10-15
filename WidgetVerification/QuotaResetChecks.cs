using System.IO;
using UsageWidget;

internal static class QuotaResetChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var start = DateTimeOffset.Parse("2026-10-15T09:00:00Z");
        var deadline = start.AddHours(5).ToUnixTimeSeconds();
        ProviderUsage Sample(double used, int seconds, long? reset, double secondary = 10) =>
            new("codex", "ready", "fixture", [new("primary", "5시간", used, reset), new("secondary", "주간", secondary, deadline + 604800)], start.AddSeconds(seconds));
        var directory = Path.Combine(root, "refill");
        var tracker = new DailyUsageTracker(directory, TimeZoneInfo.Utc);
        ProviderUsage Read(double used, int seconds, long? reset, double secondary = 10) =>
            tracker.Observe(Sample(used, seconds, reset, secondary), "example-account", start.AddSeconds(seconds), TimeSpan.FromHours(1), 1440);
        Read(20, 0, deadline);
        check(Read(30, 3600, deadline, 14).Windows[0].Today?.UsedPercentagePoints == 10, "Refill fixture has measured consumption");
        Read(32, 3610, deadline, 15);
        var refill = Read(0, 3620, deadline, 15);
        check(refill.Windows[0].Today is { UsedPercentagePoints: 0, CyclePercentagePoints: 0, RecentPercentagePoints: 0 } amount &&
            amount.ObservedSince == start.AddSeconds(3620) && amount.MeasuredAt == start.AddSeconds(3620),
            "Full refill with unchanged deadline immediately clears measured and pending consumption");
        check(refill.Windows[1].Today?.UsedPercentagePoints == 4, "Refilling one quota preserves the other quota's consumption");
        check(Read(0, 3630, deadline, 15).Windows[0].Today?.MeasuredAt == start.AddSeconds(3620), "Repeated 100-percent snapshots do not postpone the measurement clock");
        Read(3, 3640, deadline, 15);
        tracker = new DailyUsageTracker(directory, TimeZoneInfo.Utc);
        var afterRestart = Read(5, 7220, deadline, 15).Windows[0].Today!;
        check(afterRestart.UsedPercentagePoints == 5 && afterRestart.CyclePercentagePoints == 5 && afterRestart.RecentPercentagePoints == 5,
            "Relaunch retains the new baseline and only counts consumption after refill");
        var stale = tracker.Observe(Sample(0, 3615, deadline), "example-account", start.AddSeconds(7220), TimeSpan.FromHours(1));
        check(stale.Windows[0].Today is null && Read(5, 7220, deadline, 15).Windows[0].Today?.UsedPercentagePoints == 5,
            "An out-of-order full snapshot cannot reset newer consumption");
        var providerReset = Read(1, 7230, deadline + 86400, 15).Windows[0].Today!;
        check(providerReset.UsedPercentagePoints == 0 && providerReset.RecentPercentagePoints == 0 && providerReset.MeasuredAt == start.AddSeconds(7230),
            "Provider reset starts a baseline even when consumption has already resumed");

        tracker = new DailyUsageTracker(Path.Combine(root, "correction"), TimeZoneInfo.Utc);
        Read(20, 0, deadline);
        Read(25, 3600, deadline);
        Read(24, 3610, deadline + 1);
        var corrected = Read(26, 7200, deadline + 2).Windows[0].Today!;
        check(corrected.UsedPercentagePoints == 6 && corrected.RecentPercentagePoints == 6,
            "Percentage corrections and deadline jitter keep the consumption high-water mark");
        check(Read(27, 10800, deadline + 3600).Windows[0].Today?.UsedPercentagePoints == 7,
            "A deadline extension without replenishment does not clear consumption");

        tracker = new DailyUsageTracker(Path.Combine(root, "scheduled"), TimeZoneInfo.Utc);
        Read(20, 0, start.AddSeconds(120).ToUnixTimeSeconds());
        Read(25, 60, start.AddSeconds(120).ToUnixTimeSeconds());
        check(Read(30, 180, deadline).Windows[0].Today is { UsedPercentagePoints: 0, RecentPercentagePoints: 0 },
            "Passing the old reset boundary clears consumption even if the first new-cycle sample is higher");

        tracker = new DailyUsageTracker(Path.Combine(root, "no-deadline"), TimeZoneInfo.Utc);
        Read(20, 0, null); Read(25, 3600, null);
        check(Read(0, 3610, null).Windows[0].Today is { UsedPercentagePoints: 0, RecentPercentagePoints: 0 },
            "A full refill also resets quotas whose provider supplies no deadline");

        tracker = new DailyUsageTracker(Path.Combine(root, "midnight"), TimeZoneInfo.Utc);
        var midnight = start.Date.AddDays(1);
        var before = new DateTimeOffset(midnight, TimeSpan.Zero).AddMinutes(-2);
        ProviderUsage AroundMidnight(double used, DateTimeOffset time) => new("claude", "ready", "fixture", [new("primary", "5시간", used, deadline + 86400)], time);
        tracker.Observe(AroundMidnight(20, before), "example-claude", before);
        tracker.Observe(AroundMidnight(25, before.AddMinutes(1)), "example-claude", before.AddMinutes(1));
        var nextDay = tracker.Observe(AroundMidnight(0, before.AddMinutes(3)), "example-claude", before.AddMinutes(3), TimeSpan.FromHours(1));
        check(nextDay.Windows[0].Today is { UsedPercentagePoints: 0, RecentPercentagePoints: 0, IsCurrentDay: true },
            "Claude refill across midnight clears carried rolling consumption and the old clock");
    }
}
