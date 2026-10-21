using System.IO;
using UsageWidget;

internal static partial class Program
{
    private static async Task VerifyRecentUsageAsync()
    {
        _window?.Close();
        var directory = Path.Combine(_output, "recent-ui-" + Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddDays(3).ToUnixTimeSeconds();
        ProviderUsage Sample(double used, int minutes) => new("codex", "ready", "fixture", [new("primary", "주간", used, reset)], now.AddMinutes(minutes));
        var history = new UsageHistory(directory, now.AddHours(-3));
        foreach (var (used, minutes) in new[] { (10, -90), (10, -85), (10, -80), (10, -75), (13, -70),
            (13, -50), (13, -45), (18, -40), (18, -10), (20, -5), (20, -4) })
            history.Observe(Sample(used, minutes), "verification", now.AddMinutes(minutes));
        WidgetStorage.Write(Path.Combine(directory, "dashboard.json"), new DashboardPreferences(["codex", "claude"]));
        WidgetStorage.Write(Path.Combine(directory, "measurement.json"), new MeasurementPreferences(180, 30));
        var settings = Path.Combine(directory, "claude-settings.json"); File.WriteAllText(settings, "{}");
        _window = new MainWindow(directory, testMode: true, claudeSettings: settings);
        _window.Show(); await WaitAsync(() => _window.Ready, "Rolling trend UI ready");
        _window.SetFixture(Sample(20, 0) with { UpdatedAt = DateTimeOffset.UtcNow },
            new("claude", "ready", "fixture", [new("seven_day", "주간", 42, reset), new("five_hour", "5시간", 18, reset)], DateTimeOffset.UtcNow));
        await Task.Delay(650);
        await ScriptAsync($"window.__trendNow={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}; Date.now=()=>window.__trendNow; window.dispatchEvent(new Event('focus')); window.__trendMessages=0; window.addEventListener('usage-state',()=>window.__trendMessages++)");
        await Task.Delay(100);
        await AssertScriptAsync("document.querySelector('.codex .recent-trend').dataset.total==='2' && document.querySelector('.codex .recent-trend').dataset.status==='partial' && document.querySelector('.codex .recent-total').textContent==='2%p'",
            "Card total uses recent time-series consumption rather than the three-hour daily measurement schedule");
        await AssertScriptAsync("document.querySelectorAll('.codex .recent-chart rect').length>0 && document.querySelector('.codex .recent-chart .zero')!==null && document.querySelector('.claude .recent-trend').dataset.total==='' && document.querySelectorAll('.claude .recent-chart rect').length===0",
            "Sparkline draws observed consumption and observed zero while unrecorded quota histories stay blank");
        await AssertScriptAsync("document.querySelector('.codex .recent-chart').getAttribute('aria-label').includes('빈 구간은 미관측') && document.documentElement.scrollWidth<=innerWidth",
            "Rolling chart explains gaps accessibly and fits the detailed card");
        await CaptureAsync("recent-30m.png");
        async Task SelectWindow(string label)
        {
            await ScriptAsync("document.querySelector('.codex .recent-trend').click()"); await Task.Delay(250);
            await ScriptAsync($"document.querySelector('[aria-label=\"비교 구간 {label}\"]').click()"); await Task.Delay(150);
            await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()"); await Task.Delay(650);
        }
        await SelectWindow("1h");
        await AssertScriptAsync("document.querySelector('.codex .recent-trend').dataset.total==='7' && +document.querySelector('.codex .recent-trend').dataset.end===window.__trendNow && +document.querySelector('.codex .recent-trend').dataset.start===window.__trendNow-3600000",
            "Changing comparison range immediately includes the current-time hour without a CLI refresh");
        Check(MeasurementPreferences.Read(directory) is { IntervalMinutes: 180, WindowMinutes: 60 },
            "Live comparison range changes preserve the daily measurement interval");
        await SelectWindow("3h");
        await AssertScriptAsync("document.querySelector('.codex .recent-trend').dataset.total==='10' && document.querySelectorAll('.codex .recent-bucket').length<24",
            "Wider ranges immediately reuse persisted records and leave whole unobserved buckets undrawn");
        await CaptureAsync("recent-3h.png");
        await SelectWindow("1h");
        await ScriptAsync("document.querySelector('[aria-label=\"간략히 보기\"]').click()"); await Task.Delay(950);
        await AssertScriptAsync("document.querySelector('.compact .codex .recent-trend').dataset.total==='7' && document.querySelector('.compact .codex .recent-chart')!==null && document.documentElement.scrollWidth<=innerWidth",
            "Compact cards retain rolling totals and sparklines without horizontal overflow");
        await CaptureAsync("recent-compact.png");
        await ScriptAsync("window.__trendMessages=0; window.__trendNow+=61*60000; window.dispatchEvent(new Event('focus'))"); await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.compact .codex .recent-trend').dataset.total==='' && document.querySelectorAll('.compact .codex .recent-chart rect').length===0 && window.__trendMessages===0",
            "Visible rolling totals age out with the local clock and do not need a native message or extra service query");
        _window.Close(); _window = null;
    }
}
