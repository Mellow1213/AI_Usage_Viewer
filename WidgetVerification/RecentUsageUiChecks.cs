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
        var codex = Sample(20, 0) with { UpdatedAt = DateTimeOffset.UtcNow };
        var claude = new ProviderUsage("claude", "ready", "fixture", [new("seven_day", "주간", 42, reset), new("five_hour", "5시간", 18, reset)], DateTimeOffset.UtcNow);
        _window.SetFixture(codex, claude);
        await Task.Delay(650);
        await ScriptAsync($"window.__trendNow={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}; Date.now=()=>window.__trendNow; window.dispatchEvent(new Event('focus')); window.__trendMessages=0; window.addEventListener('usage-state',()=>window.__trendMessages++)");
        await Task.Delay(100);
        await ScriptAsync("window.addEventListener('usage-state',event=>window.__trendState=event.detail); window.chrome.webview.postMessage({action:'ready'})");
        await Task.Delay(150);
        // The ready handshake starts snapshot polling; fixtures must stay isolated from that poller.
        _window.SetFixture(codex, claude);
        await AssertScriptAsync("!document.querySelector('.recent-trend') && window.__trendState.providers.every(provider=>provider.windows.every(quota=>!quota.trend))",
            "Detailed cards contain no trend area and native history payload is disabled by default");
        await CaptureAsync("cards-without-trends.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()"); await Task.Delay(200);
        await ScriptAsync("document.querySelector('[aria-label=\"사용량 추이\"]').click()"); await Task.Delay(350);
        await AssertScriptAsync("document.getElementById('settings-title').textContent==='사용량 추이' && document.querySelector('.recent-trend').dataset.total==='2' && document.querySelector('.recent-trend').dataset.status==='partial' && document.querySelector('.recent-total').textContent==='2%p'",
            "The separate optional trend screen loads history on demand and shows the current-time total");
        await AssertScriptAsync("document.querySelectorAll('.recent-chart rect').length>0 && document.querySelector('.recent-chart .zero')!==null && document.querySelectorAll('#trend-source option').length===3",
            "Optional graph distinguishes observed zero and offers separate account and quota choices");
        await AssertScriptAsync("document.querySelector('.recent-chart').getAttribute('aria-label').includes('빈 구간은 미관측') && document.documentElement.scrollWidth<=innerWidth && document.querySelector('.settings-panel').getBoundingClientRect().bottom<=innerHeight+2",
            "Optional trend screen fits the native widget and describes missing history accessibly");
        await CaptureAsync("trends-option-30m.png");
        await ScriptAsync("(()=>{const chart=document.querySelector('.trend-scrubber'); chart.focus(); chart.dispatchEvent(new KeyboardEvent('keydown',{key:'Home',bubbles:true}))})()"); await Task.Delay(100);
        await AssertScriptAsync("document.querySelector('.trend-inspection').dataset.selected==='0' && document.querySelector('.trend-inspection').textContent.includes('미관측') && document.querySelector('.trend-inspection strong').textContent==='—' && document.querySelector('.trend-crosshair')!==null",
            "Keyboard inspection reveals a missing interval without reporting zero consumption");
        var zeroIndex = int.Parse(await ScriptAsync("+document.querySelector('.recent-bucket.zero').dataset.index"));
        for (var i = 0; i < zeroIndex; i++)
        {
            await ScriptAsync("document.querySelector('.trend-scrubber').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}))");
            await Task.Delay(20);
        }
        await AssertScriptAsync("document.querySelector('.trend-inspection').textContent.includes('소비 없음') && document.querySelector('.trend-inspection strong').textContent==='0%p'",
            "Keyboard inspection distinguishes an observed zero from missing intervals");
        await ScriptAsync("(()=>{const chart=document.querySelector('.trend-scrubber'); chart.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true})); const index=+document.querySelector('.recent-bucket:not(.zero)').dataset.index; const box=chart.getBoundingClientRect(); const count=+chart.getAttribute('aria-valuemax')+1; window.__selectedTrendBucket=index; chart.dispatchEvent(new MouseEvent('click',{clientX:box.left+(index+.5)/count*box.width*248/280,clientY:box.top+20,bubbles:true}))})()"); await Task.Delay(100);
        await AssertScriptAsync("+document.querySelector('.trend-inspection').dataset.selected===window.__selectedTrendBucket && document.querySelector('.trend-inspection strong').textContent==='2%p' && document.querySelector('[aria-label=\"그래프 선택 해제\"]')!==null",
            "Clicking a consumption bar pins its exact interval and amount");
        await CaptureAsync("trends-inspection.png");
        await ScriptAsync("document.querySelector('[aria-label=\"그래프 선택 해제\"]').click()"); await Task.Delay(100);
        await AssertScriptAsync("document.querySelector('.trend-inspection').dataset.selected==='' && document.querySelector('.trend-crosshair')===null",
            "Clearing a selection restores the graph exploration hint");
        async Task SelectWindow(string label)
        {
            await ScriptAsync($"document.querySelector('[aria-label=\"비교 구간 {label}\"]').click()"); await Task.Delay(180);
        }
        await SelectWindow("1h");
        await AssertScriptAsync("document.querySelector('.recent-trend').dataset.total==='7' && +document.querySelector('.recent-trend').dataset.end===window.__trendNow && +document.querySelector('.recent-trend').dataset.start===window.__trendNow-3600000",
            "Changing the optional comparison range immediately recalculates from now without a CLI query");
        Check(MeasurementPreferences.Read(directory) is { IntervalMinutes: 180, WindowMinutes: 60 },
            "Live comparison range changes preserve the daily measurement interval");
        await SelectWindow("3h");
        await AssertScriptAsync("document.querySelector('.recent-trend').dataset.total==='10' && document.querySelectorAll('.recent-bucket').length<24",
            "Wider optional ranges reuse persisted records and leave unobserved buckets blank");
        await AssertScriptAsync("document.querySelector('.trend-inspection').dataset.selected==='' && document.querySelectorAll('.trend-x-label').length===3 && document.querySelectorAll('.trend-grid').length===3 && document.querySelector('.trend-plot-heading').textContent.includes('5분')",
            "Range changes reset inspection and use readable time and value axes with natural bucket intervals");
        await CaptureAsync("trends-option-3h.png");
        await ScriptAsync("(()=>{const source=document.getElementById('trend-source'); source.value='claude:seven_day'; source.dispatchEvent(new Event('change',{bubbles:true}))})()"); await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.recent-trend').dataset.total==='' && document.querySelectorAll('.recent-chart rect').length===0",
            "Switching to a quota with no history shows an empty graph instead of zero consumption");
        await ScriptAsync("(()=>{const source=document.getElementById('trend-source'); source.value='codex:primary'; source.dispatchEvent(new Event('change',{bubbles:true}))})()"); await Task.Delay(150);
        await SelectWindow("1h");
        await ScriptAsync("window.__trendMessages=0; window.__trendNow+=61*60000; window.dispatchEvent(new Event('focus'))"); await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.recent-trend').dataset.total==='' && document.querySelectorAll('.recent-chart rect').length===0 && window.__trendMessages===0",
            "The optional screen ages records out locally without another native message");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()"); await Task.Delay(650);
        await AssertScriptAsync("!document.querySelector('.recent-trend') && window.__trendState.providers.every(provider=>provider.windows.every(quota=>!quota.trend))",
            "Closing the optional screen releases history payloads and returns to graph-free cards");
        await ScriptAsync("document.querySelector('[aria-label=\"간략히 보기\"]').click()"); await Task.Delay(950);
        await AssertScriptAsync("document.querySelector('.compact')!==null && !document.querySelector('.recent-trend') && document.documentElement.scrollWidth<=innerWidth",
            "Compact cards also omit optional totals and graphs");
        await CaptureAsync("compact-without-trends.png");
        _window.Close(); _window = null;
    }
}
