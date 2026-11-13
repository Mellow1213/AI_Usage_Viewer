using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using UsageWidget;

internal static partial class Program
{
    private static async Task MeasureLogoMemoryAsync(bool baseline)
    {
        var profile = Path.Combine(_output, "profile-" + Guid.NewGuid().ToString("N"));
        WidgetStorage.Write(Path.Combine(profile, "dashboard.json"), new DashboardPreferences(["codex", "claude"]));
        var settings = Path.Combine(profile, "claude-settings.json");
        File.WriteAllText(settings, "{}");
        _window = new MainWindow(profile, testMode: true, claudeSettings: settings, foregroundWindow: () => IntPtr.Zero);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Logo memory benchmark ready");
        var web = _window.WebView!;
        var now = DateTimeOffset.UtcNow;
        _window.SetFixture(
            new("codex", "ready", "계정 사용량", [new("primary", "주간", 8, now.AddDays(3).ToUnixTimeSeconds())], now),
            new("claude", "ready", "계정 사용량", [new("five_hour", "5시간", 36, now.AddHours(2).ToUnixTimeSeconds())], now));
        await WaitForRenderedViewAsync(".widget[data-view='0'] .provider");
        await Task.Delay(2000);

        var rounds = new List<object>();
        for (var round = 1; round <= 2; round++)
        {
            await ScriptAsync("window.chrome.webview.postMessage({action:'collapse'})");
            await WaitAsync(() => _window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth == 48 && !_window.IsResizing, "Native logo settled");
            await WaitForRenderedViewAsync(".logo-shell");
            Check(web.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "A newly folded logo keeps the normal memory target");
            var folded = Stopwatch.StartNew();
            var samples = new List<object>();
            foreach (var seconds in new[] { 12, 14, 16 })
            {
                var remaining = TimeSpan.FromSeconds(seconds) - folded.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
                var sample = PerformanceMeasurement.SampleMemory(web);
                samples.Add(new { elapsedSeconds = folded.Elapsed.TotalSeconds, privateResidentMiB = sample.Resident, privateCommitMiB = sample.Commit });
            }
            var target = web.MemoryUsageTargetLevel;
            Check(target == (baseline ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low),
                $"Settled logo uses the expected {target} memory target");
            await ScriptAsync("""
                (() => {
                  const timing = window.logoReopenTime = { firstVisibleMs: null, fullyVisibleMs: null };
                  const started = performance.now();
                  document.querySelector('.logo-shell').click();
                  const frame = () => {
                    const card = document.querySelector('.widget[data-view="0"]');
                    const opacity = card ? Number(getComputedStyle(card).opacity) : 0;
                    const providers = [...(card?.querySelectorAll('.provider') ?? [])];
                    const visible = providers.map(provider => opacity * Number(getComputedStyle(provider).opacity));
                    const elapsed = performance.now() - started;
                    if (visible.some(value => value >= .05)) timing.firstVisibleMs ??= elapsed;
                    if (visible.length === 2 && visible.every(value => value >= .99) && !document.querySelector('.is-transitioning')) {
                      timing.fullyVisibleMs = elapsed;
                    } else if (elapsed < 10000) requestAnimationFrame(frame);
                  };
                  requestAnimationFrame(frame);
                })()
                """);
            await WaitForBenchmarkScriptAsync("window.logoReopenTime?.fullyVisibleMs !== null && !!window.logoReopenTime",
                value => value == "true");
            var latency = await ScriptAsync("window.logoReopenTime");
            Check(web.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal, "Reopening restores the normal memory target and displays both cards");
            Check(await ScriptAsync("document.querySelectorAll('.widget .provider').length") == "2", "Both fixture accounts remain visible after reopening");
            var result = new { round, target = target.ToString(), samples, reopen = JsonSerializer.Deserialize<JsonElement>(latency) };
            rounds.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(result));
            await Task.Delay(1000);
        }
        WidgetStorage.Write(Path.Combine(_output, "logo-memory.json"), new
        {
            baseline, runtime = web.Environment.BrowserVersionString, rounds,
            scope = "WPF and all its WebView2 processes; 2 fixture cards; animations enabled; no accounts or AI CLI"
        });
    }

    private static Task<string> WaitForRenderedViewAsync(string selector) => WaitForBenchmarkScriptAsync(
        $"(() => {{ const node = document.querySelector({JsonSerializer.Serialize(selector)}); return !!node && Number(getComputedStyle(node.closest('.widget, .logo-shell')).opacity) >= .99 && !document.querySelector('.is-logo-moving, .is-transitioning'); }})()",
        value => value == "true");

    private static async Task<string> WaitForBenchmarkScriptAsync(string script, Func<string, bool> completed)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            var result = await ScriptAsync(script);
            if (completed(result)) return result;
            await Task.Delay(80);
        }
        var state = await ScriptAsync("""
            ({ timing: window.logoReopenTime, classes: document.querySelector('.view-shell')?.className,
               view: document.querySelector('[data-view]')?.dataset.view,
               opacity: [...document.querySelectorAll('.widget, .provider')].map(node => getComputedStyle(node).opacity) })
            """);
        throw new TimeoutException($"The logo memory benchmark view did not finish rendering. State: {state}");
    }
}
