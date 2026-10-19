using System.IO;
using UsageWidget;

internal static partial class Program
{
    private static async Task VerifyUsageHistoryAsync()
    {
        _window?.Close();
        var profile = Path.Combine(_output, "history-ui-" + Guid.NewGuid().ToString("N"));
        var today = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        double At(DateTimeOffset date) => date.ToUnixTimeMilliseconds() / 1000d;
        var record = new UsageHistory.Series { Id = "fixture-codex", ProviderId = "codex", Label = "주간",
            StartedAt = At(today.AddDays(-6)), LastObservedAt = At(today),
            Coverage = [[At(today.AddDays(-6)), At(today.AddDays(-5))], [At(today.AddDays(-4).AddHours(9)), At(today.AddDays(-4).AddHours(18))],
                [At(today.AddDays(-3)), At(today.AddDays(-1))], [At(today.AddDays(-1).AddHours(10)), At(today.AddDays(-1).AddHours(14))]],
            Points = [[At(today.AddDays(-4).AddHours(12)), 5], [At(today.AddDays(-3).AddHours(12)), 9], [At(today.AddDays(-1).AddHours(12)), 3]] };
        WidgetStorage.Write(Path.Combine(profile, "usage-history.json"), new[] { record, new UsageHistory.Series {
            Id = "fixture-claude", ProviderId = "claude", Label = "5시간", StartedAt = At(today), LastObservedAt = At(today) } });
        WidgetStorage.Write(Path.Combine(profile, "dashboard.json"), new DashboardPreferences(["codex", "claude"]));
        var settings = Path.Combine(profile, "claude-settings.json"); File.WriteAllText(settings, "{}");
        _window = new MainWindow(profile, testMode: true, claudeSettings: settings);
        _window.Show();
        await WaitAsync(() => _window.Ready, "History UI ready");
        await Task.Delay(250);
        await AssertScriptAsync("!document.querySelector('.usage-history') && document.querySelectorAll('.provider').length===2", "Default widget retains its cards without a history graph");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(200);
        await ScriptAsync("document.querySelector('[aria-label=\"사용 기록\"]').click()");
        await Task.Delay(650);
        await AssertScriptAsync("document.querySelectorAll('.history-column').length===7 && document.querySelector('.history-modes [aria-pressed=true]').textContent==='일별'", "Usage history opens with seven daily buckets");
        await AssertScriptAsync("document.querySelector('.history-column.complete .history-column-value').textContent==='0' && document.querySelector('.history-column.missing .history-column-value').textContent==='—' && !!document.querySelector('.history-column.partial')", "Real WebView separates observed zero, partial observations, and missing data");
        await AssertScriptAsync("document.querySelector('.history-summary strong').textContent==='17%p' && document.querySelector('select[aria-label=\"사용 기록 계정과 한도\"]').options.length===2", "Daily totals and separate account/quota choices use persisted history");
        await AssertScriptAsync("document.querySelector('.settings-panel').getBoundingClientRect().bottom<=innerHeight+2 && document.querySelector('.usage-history').scrollWidth<=document.querySelector('.settings-content').clientWidth+2", "History screen fits the native widget width and scrollable height");
        await CaptureAsync("history-daily.png");
        await ScriptAsync("[...document.querySelectorAll('.history-modes button')].find(button=>button.textContent==='시간별').click(); document.querySelector('[aria-label=\"이전 기간\"]').click()");
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelectorAll('.history-column').length>=23 && document.querySelector('.history-chart-scroll').scrollWidth>document.querySelector('.history-chart-scroll').clientWidth", "Hourly view provides scrollable calendar-hour buckets and date navigation");
        await AssertScriptAsync("(()=>{const selected=document.querySelector('.history-column.selected').getBoundingClientRect(), viewport=document.querySelector('.history-chart-scroll').getBoundingClientRect(); return selected.left>=viewport.left-1 && selected.right<=viewport.right+1 && document.querySelector('.history-detail').textContent.includes('관측됨')})()", "Hourly view brings the latest observed hour into view instead of leaving it outside the chart");
        await CaptureAsync("history-hourly.png");
        await ScriptAsync("[...document.querySelectorAll('.history-modes button')].find(button=>button.textContent==='주별').click()");
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelectorAll('.history-column').length===8 && document.querySelector('.history-summary strong').textContent==='17%p'", "Weekly view retains the same observed total across eight calendar weeks");
        await CaptureAsync("history-weekly.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await Task.Delay(450);
        await AssertScriptAsync("!document.querySelector('.usage-history') && document.querySelectorAll('.provider').length===2", "Closing history returns to the unchanged widget cards");
    }
}
