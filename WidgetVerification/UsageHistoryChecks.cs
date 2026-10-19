using System.IO;
using UsageWidget;

internal static class UsageHistoryChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        root = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var start = DateTimeOffset.Parse("2026-10-19T23:58:00Z");
        var deadline = start.AddHours(5).ToUnixTimeSeconds();
        ProviderUsage Sample(double used, int seconds, long? reset = null) => new("codex", "ready", "fixture",
            [new("primary", "5시간", used, reset ?? deadline)], start.AddSeconds(seconds));
        var history = new UsageHistory(root, start);
        void Observe(double used, int seconds, long? reset = null, string identity = "private@example.test") =>
            history.Observe(Sample(used, seconds, reset), identity, start.AddSeconds(seconds));
        Observe(20, 0); Observe(20, 60);
        check(history.Records.Single().Coverage.Single()[1] - history.Records.Single().Coverage.Single()[0] == 60
            && history.Records.Single().Points.Count == 0, "History records observed zero as coverage with no consumption");
        Observe(25, 180); Observe(24, 240); Observe(26, 300); Observe(26, 300);
        check(history.Records.Single().Points.Sum(p => p[1]) == 6 && history.Records.Single().Coverage.Count == 1,
            "History crosses midnight and ignores duplicate snapshots and percentage corrections");
        Observe(80, 1200); Observe(82, 1260);
        check(history.Records.Single().Points.Sum(p => p[1]) == 8 && history.Records.Single().Coverage.Count == 2,
            "A long sampling gap is unknown and consumption during that gap is not assigned");
        Observe(0, 1320); Observe(3, 1380);
        check(history.Records.Single().Points.Sum(p => p[1]) == 11 && history.Records.Single().Coverage.Count == 3,
            "Refills preserve previous history and leave the reset interval unobserved");
        var priorCoverage = history.Records.Single().Coverage.Sum(c => c[1] - c[0]);
        history = new UsageHistory(root, start.AddSeconds(1400));
        Observe(10, 1440); Observe(12, 1500);
        check(history.Records.Single().Points.Sum(p => p[1]) == 13
            && history.Records.Single().Coverage.Sum(c => c[1] - c[0]) == priorCoverage + 60,
            "Relaunch preserves records without bridging the application's offline interval");
        history.Observe(new("codex", "error", "offline", []), "private@example.test", start.AddSeconds(1510));
        history.Observe(Sample(12, 1500), "private@example.test", start.AddSeconds(1520));
        Observe(15, 1560); Observe(16, 1620);
        check(history.Records.Single().Points.Sum(p => p[1]) == 14, "Connection failures break history coverage even for short gaps");
        history.RetainProviders([], start.AddSeconds(1660)); Observe(18, 1680);
        check(history.Records.Single().Points.Sum(p => p[1]) == 14, "Hidden or removed providers cannot bridge unobserved intervals");
        Observe(50, 1740, identity: "other@example.test"); Observe(52, 1800, identity: "other@example.test");
        check(history.Records.Count == 2 && history.Records[1].Points.Single()[1] == 2, "Different account identities retain separate history series");
        var stored = File.ReadAllText(Path.Combine(root, "usage-history.json"));
        check(!stored.Contains("@example.test"), "History files never contain account email identifiers");
        Observe(60, 1810, deadline + 86400, "other@example.test");
        Observe(62, 1820, deadline + 86400, "other@example.test");
        check(history.Records[1].Points.Sum(p => p[1]) == 12, "A deadline extension alone does not erase valid consumption history");
        history.Interrupt(start.AddSeconds(1830));
        history.Observe(Sample(62, 1820, deadline + 86400), "other@example.test", start.AddSeconds(1840));
        Observe(64, 1860, deadline + 86400, "other@example.test"); Observe(65, 1920, deadline + 86400, "other@example.test");
        check(history.Records[1].Points.Sum(p => p[1]) == 13, "Resume notifications leave even short sleep intervals and pre-wake cached snapshots unobserved");
        var future = start.AddDays(91);
        history.Observe(Sample(1, 0) with { UpdatedAt = future, Windows = [new("primary", "5시간", 1, future.AddHours(5).ToUnixTimeSeconds())] },
            "other@example.test", future);
        check(history.Records.All(s => s.Points.Count == 0 && s.Coverage.Count == 0), "History retention prunes old points and coverage after 90 days");
        var broken = Path.Combine(root, "broken"); Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "usage-history.json"), "{ broken history");
        new UsageHistory(broken, start).Observe(Sample(10, 0), "test", start);
        check(File.ReadAllText(Path.Combine(broken, "usage-history.json")) == "{ broken history", "Unreadable history files are preserved rather than overwritten");
    }
}
