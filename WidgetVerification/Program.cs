using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using UsageWidget;

internal static partial class Program
{
    private static readonly List<string> Passed = [];
    private static string _output = "";
    private static MainWindow? _window;
    private static int _exitCode = 1;
    [STAThread]
    private static int Main(string[] args)
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (args.Length > 0 && args[0] == "--github-release") return VerifyGithubReleaseAsync(args).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "--update-e2e") return RunPackagedUpdateScenario(args);
        if (args.Contains("--rpc-fixture")) return RunRpcFixture();
        if (args.Contains("--claude-usage-fixture")) return RunClaudeUsageFixture(args.Contains("--unsupported"));
        if (args.Contains("--claude-account-fixture")) return RunClaudeAccountFixture();
        if (args.Contains("--presence-fixture")) return RunPresenceFixture(args);
        _output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/widget-verification");
        Directory.CreateDirectory(_output);
        WidgetStorage.UseDataDirectory(_output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            string? error = null;
            try
            {
                AccountRemovalChecks.Run(Path.Combine(_output, "account-removal"), Check);
                StorageIsolationChecks.Run(Path.Combine(_output, "storage-isolation"), Check);
                if (args.Contains("--updates-only")) await VerifyUpdatesAsync();
                else if (args.Contains("--presence-only")) await VerifyPresenceAsync();
                else if (args.Contains("--performance-only")) await MeasurePerformanceAsync();
                else if (args.Contains("--performance-checks-only")) await VerifyPerformanceChangesAsync();
                else if (args.Contains("--multi-ai-only")) await VerifyMultipleAiAsync();
                else if (args.Contains("--multi-codex-only")) await VerifyMultipleCodexAsync();
                else await VerifyAsync(args.Contains("--live"));
                _exitCode = 0;
            }
            catch (Exception ex) { error = ex.ToString(); Console.Error.WriteLine(error); }
            finally
            {
                _window?.Close();
                File.WriteAllText(Path.Combine(_output, "results.json"), JsonSerializer.Serialize(new { Success = _exitCode == 0, Passed, Error = error }, WidgetStorage.Json));
                app.Shutdown();
            }
        };
        app.Run();
        return _exitCode;
    }

    private static async Task VerifyPerformanceChangesAsync()
    {
        var profile = Path.Combine(_output, "profile-" + Guid.NewGuid().ToString("N"));
        WidgetStorage.Write(Path.Combine(profile, "dashboard.json"), new DashboardPreferences(["codex", "claude"]));
        var settings = Path.Combine(profile, "claude-settings.json");
        File.WriteAllText(settings, "{}");
        _window = new MainWindow(profile, testMode: true, claudeSettings: settings);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Optimization checks ready");
        // Production minification renames callbacks. Track long-lived display
        // timers by duration; animation/transition timers are shorter than 1s.
        await ScriptAsync("window.__clockTimers=new Set(); window.__messages=0; window.addEventListener('usage-state',()=>window.__messages++); const originalTimeout=window.setTimeout; const originalClear=window.clearTimeout; window.setTimeout=(callback,delay,...args)=>{let id;const clock=delay>1000&&delay<=60000;id=originalTimeout(()=>{if(clock)window.__clockTimers.delete(id);callback(...args)},delay);if(clock)window.__clockTimers.add(id);return id}; window.clearTimeout=id=>{window.__clockTimers.delete(id);originalClear(id)}");
        var now = DateTimeOffset.UtcNow;
        var codex = new ProviderUsage("codex", "ready", "계정 사용량", [new("primary", "주간", 8, now.AddDays(3).ToUnixTimeSeconds())], now, "prolite");
        var claude = new ProviderUsage("claude", "ready", "Claude 사용량 조회", [new("five_hour", "5시간", 36, now.AddHours(2).ToUnixTimeSeconds()), new("seven_day", "주간", 62, now.AddDays(2).ToUnixTimeSeconds())], now);
        _window.SetFixture(codex, claude);
        await Task.Delay(800);
        await AssertScriptAsync("window.__clockTimers.size===2", "Visible cards own their time clocks without an App-wide interval");
        await ScriptAsync("window.__messages=0");
        var publish = typeof(MainWindow).GetMethod("Publish", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var i = 0; i < 4; i++) { publish.Invoke(_window, [false]); await Task.Delay(80); }
        await AssertScriptAsync("window.__messages===0", "Identical quota snapshots produce no native-to-React messages");
        _window.SetFixture(codex with { UpdatedAt = now.AddMilliseconds(1) }, claude);
        await Task.Delay(100);
        await AssertScriptAsync("window.__messages===1", "A fresh source timestamp is delivered even when quota percentages are unchanged");
        await ScriptAsync("window.__messages=0; window.chrome.webview.postMessage({action:'pin'})");
        await Task.Delay(120);
        await AssertScriptAsync("window.__messages===1", "Non-quota state changes still reach React");
        await ScriptAsync("window.chrome.webview.postMessage({action:'toggleLogo'})");
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Logo && !_window.IsResizing, "Logo settled");
        await Task.Delay(700);
        await AssertScriptAsync("window.__clockTimers.size===0 && document.querySelector('.logo-shell .triangle').classList.contains('waving')", "Logo mode cancels all card clocks while retaining the idle ripple");
        await ScriptAsync("window.__messages=0");
        for (var i = 0; i < 4; i++) { publish.Invoke(_window, [false]); await Task.Delay(80); }
        await AssertScriptAsync("window.__messages===0", "Idle logo does not receive duplicate snapshots");
        _window.SetFixture(codex, claude, busy: true);
        await Task.Delay(500);
        await AssertScriptAsync("document.querySelector('.logo-shell .triangle').classList.contains('syncing') && document.querySelector('.logo-shell .triangle').style.transform.includes('rotate')", "Synchronization keeps the faster wave and rotating logo");
        _window.SetFixture(codex, claude);
        await ScriptAsync("window.chrome.webview.postMessage({action:'toggleLogo'})");
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Expanded && !_window.IsResizing, "Card restored");
        await Task.Delay(700);
        await AssertScriptAsync("window.__clockTimers.size===2 && document.querySelector('.codex .number').textContent==='92'", "Restoring cards resumes their clocks and retains quota values");
        // Native polling is stopped by SetFixture: these changes must come from
        // the local display clock, not from a new server snapshot.
        var boundary = DateTimeOffset.UtcNow;
        _window.SetFixture(codex with { UpdatedAt = boundary.AddSeconds(-59) }, claude);
        await Task.Delay(1600);
        await AssertScriptAsync("document.querySelector('.codex .card-foot').textContent.includes('1분 전 수신')", "Receipt age updates at its boundary without a native message");
        boundary = DateTimeOffset.UtcNow;
        _window.SetFixture(codex with { Windows = [new("primary", "주간", 8, boundary.AddSeconds(3).ToUnixTimeSeconds())], UpdatedAt = boundary }, claude);
        await Task.Delay(3400);
        await AssertScriptAsync("document.querySelector('.codex .reset').textContent.includes('초기화됨') && document.querySelector('.codex .meter').getAttribute('aria-valuenow')===null", "Quota expires promptly without a native message or one-second App timer");
        await ScriptAsync("window.__messages=0; window.chrome.webview.postMessage({action:'ready'})");
        await Task.Delay(120);
        await AssertScriptAsync("window.__messages>=1", "A new ready handshake always receives full state");
    }

    private static async Task MeasurePerformanceAsync()
    {
        var profile = Path.Combine(_output, "profile-" + Guid.NewGuid().ToString("N"));
        WidgetStorage.Write(Path.Combine(profile, "dashboard.json"), new DashboardPreferences(["codex", "claude"]));
        var settings = Path.Combine(profile, "claude-settings.json");
        File.WriteAllText(settings, "{}");
        _window = new MainWindow(profile, testMode: true, claudeSettings: settings);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Benchmark ready");
        var now = DateTimeOffset.UtcNow;
        _window.SetFixture(new("codex", "ready", "계정 사용량", [new("primary", "주간", 8, now.AddDays(3).ToUnixTimeSeconds())], now, "prolite"),
            new("claude", "ready", "Claude 사용량 조회", [new("five_hour", "5시간", 36, now.AddHours(2).ToUnixTimeSeconds()), new("seven_day", "주간", 62, now.AddDays(2).ToUnixTimeSeconds())], now));
        // Simulate unchanged source snapshots at the real host's two-second cadence.
        // Isolated fixtures exclude login/network/CLI variability from the UI comparison.
        var publish = typeof(MainWindow).GetMethod("Publish", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var polling = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        polling.Tick += (_, _) => publish.Invoke(_window, publish.GetParameters().Length == 0 ? null : [false]);
        polling.Start();
        await ScriptAsync("window.__benchmarkMessages=0; window.addEventListener('usage-state',()=>window.__benchmarkMessages++)");
        await _window.WebView!.CallDevToolsProtocolMethodAsync("Performance.enable", "{}");
        var measurements = new List<object>();
        try
        {
            // Reverse the second round to expose warm-up/order effects.
            foreach (var mode in new[] { WidgetViewMode.Expanded, WidgetViewMode.Logo, WidgetViewMode.Logo, WidgetViewMode.Expanded })
            {
                if (_window.ViewMode != mode)
                    await ScriptAsync("window.chrome.webview.postMessage({action:'toggleLogo'})");
                await WaitAsync(() => _window.ViewMode == mode && !_window.IsResizing, "Benchmark mode settled");
                await Task.Delay(10000);
                await ScriptAsync("window.__benchmarkMessages=0");
                Console.WriteLine($"MEASURING {mode}: 30 seconds");
                var result = await PerformanceMeasurement.MeasureAsync(_window.WebView, mode.ToString(), 30);
                var messages = JsonSerializer.Deserialize<int>(await ScriptAsync("window.__benchmarkMessages"));
                measurements.Add(new { result, messages });
                Console.WriteLine(JsonSerializer.Serialize(new { result, messages }));
            }
        }
        finally { polling.Stop(); }
        WidgetStorage.Write(Path.Combine(_output, "performance.json"), new
        {
            logicalProcessors = Environment.ProcessorCount, warmupSeconds = 10, sampleSeconds = 30,
            scope = "WPF host and its entire WebView2 process group; fixed connected fixtures; no AI CLI or network; unchanged snapshots every two seconds; animations enabled",
            measurements
        });
        Check(measurements.Count == 4, "Two expanded and two logo performance samples completed");
    }

    private static async Task VerifyAsync(bool live)
    {
        VerifyDailyUsage();
        QuotaResetChecks.Run(Path.Combine(_output, "quota-resets"), Check);
        VerifyMeasurementSchedule();
        await VerifyClaudeUsageControlAsync();
        await VerifyAccountsAsync();
        using (var doc = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":8,\"windowDurationMins\":10080,\"resetsAt\":1792297410},\"secondary\":null,\"planType\":\"prolite\"}}"))
        {
            var usage = UsageParser.Codex(doc.RootElement);
            Check(usage.Windows.Count == 1 && usage.Windows[0].RemainingPercent == 92 && usage.Windows[0].Label == "주간", "Codex remaining percentage and actual window duration parsed");
        }
        using (var doc = JsonDocument.Parse("{\"rateLimits\":null,\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":125,\"windowDurationMins\":300}},\"other\":{\"primary\":{\"usedPercent\":1}}}}"))
            Check(UsageParser.Codex(doc.RootElement).Windows[0].RemainingPercent == 0, "Per-bucket Codex selection and over-limit clamping");
        using (var doc = JsonDocument.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":null},\"secondary\":{\"usedPercent\":-1}}}"))
            Check(UsageParser.Codex(doc.RootElement).Windows.Count == 0, "Missing or invalid limits never become invented 100% values");
        using (var doc = JsonDocument.Parse("{\"updatedAt\":\"2026-10-13T00:00:00Z\",\"rate_limits\":{\"five_hour\":{\"used_percentage\":36,\"resets_at\":1792297410},\"seven_day\":{\"used_percentage\":62}}}"))
            Check(UsageParser.Claude(doc.RootElement).Windows.Select(w => w.RemainingPercent).SequenceEqual(new double[] { 64, 38 }), "Claude documented rate_limits fields parsed");

        var settings = Path.Combine(_output, "claude-settings.json");
        File.WriteAllText(settings, "{\"theme\":\"dark\",\"statusLine\":{\"type\":\"command\",\"command\":\"printf 'original status'\",\"padding\":2}}\n");
        var bridge = new ClaudeBridge(Path.Combine(_output, "bridge"), settings);
        var backupCount = Directory.Exists(Path.Combine(_output, "bridge")) ? Directory.GetFiles(Path.Combine(_output, "bridge"), "claude-settings-backup-*.json").Length : 0;
        bridge.Install();
        Check(bridge.Installed, "Claude bridge install detected");
        bridge.Install();
        Check(Directory.GetFiles(Path.Combine(_output, "bridge"), "claude-settings-backup-*.json").Length == backupCount + 1, "Bridge installation is idempotent");
        var payload = "{\"session_id\":\"private\",\"cwd\":\"secret-path\",\"rate_limits\":{\"five_hour\":{\"used_percentage\":36,\"resets_at\":1792297410},\"seven_day\":{\"used_percentage\":62,\"resets_at\":1792297410}}}";
        var scriptOutput = await InvokeBridgeAsync(bridge, payload);
        Check(scriptOutput == "original status", "Existing status line output preserved");
        Check(bridge.Read().Windows.Count == 2, "Real bridge script writes a complete usage snapshot");
        var stored = File.ReadAllText(bridge.SnapshotFile);
        Check(!stored.Contains("private") && !stored.Contains("secret-path") && !stored.Contains("session_id"), "Snapshot contains only quota fields and receipt time");
        await InvokeBridgeAsync(bridge, "{\"model\":{\"display_name\":\"Claude\"}}");
        Check(File.ReadAllText(bridge.SnapshotFile) == stored, "Absent quota payload retains last received data without claiming freshness");
        var edited = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settings))!;
        edited["theme"] = "light";
        File.WriteAllText(settings, edited.ToJsonString());
        bridge.Uninstall();
        using (var doc = JsonDocument.Parse(File.ReadAllText(settings)))
            Check(doc.RootElement.GetProperty("theme").GetString() == "light" && doc.RootElement.GetProperty("statusLine").GetProperty("command").GetString() == "printf 'original status'", "Uninstall restores only statusLine and preserves newer unrelated settings");
        File.WriteAllText(settings, "{ broken settings");
        try { bridge.Install(); throw new Exception("Malformed settings were replaced"); }
        catch (JsonException) { Check(File.ReadAllText(settings) == "{ broken settings", "Malformed Claude settings preserved"); }
        File.WriteAllText(settings, "{}");

        var uiProfile = Path.Combine(_output, "profile-" + Guid.NewGuid().ToString("N"));
        _window = new MainWindow(uiProfile, testMode: true, claudeSettings: settings);
        _window.Show();
        await WaitAsync(() => _window.Ready, "React ready handshake");
        await AssertScriptAsync("document.querySelector('.empty-dashboard').textContent.includes('AI를 추가하세요') && document.querySelectorAll('.provider').length === 0", "Fresh widget starts with an empty dashboard rather than preselected AI cards");
        await AssertScriptAsync("document.querySelector('.triangle').classList.contains('disconnected') && !document.querySelector('.triangle').classList.contains('waving') && getComputedStyle(document.querySelector('.triangle')).color==='rgb(119, 127, 123)' && [...document.querySelectorAll('.triangle path')].every(path=>getComputedStyle(path).animationName==='none')", "Empty dashboard uses a gray stationary logo without implying an active connection");
        await Task.Delay(350);
        await CaptureAsync("empty-dashboard.png");
        await ScriptAsync("document.querySelector('[aria-label=\"첫 AI 추가\"]').click()");
        await Task.Delay(400);
        await AssertScriptAsync("document.querySelectorAll('.catalog-option').length === 2 && document.querySelector('.menu').getBoundingClientRect().bottom <= innerHeight", "AI picker presents supported providers and fits the empty native window");
        await CaptureAsync("provider-picker.png");
        await ScriptAsync("document.querySelector('[aria-label=\"Codex 추가\"]').click()");
        await Task.Delay(250);
        await AssertScriptAsync("document.querySelectorAll('.account-box').length === 1 && !document.querySelector('[aria-label=\"AI 계정 추가\"]').disabled && document.querySelector('.codex .account-status').textContent === '미연결'", "Selecting the first Codex opens its disconnected account details and keeps additional-account selection available");
        Check(!AccountProfiles.ReadPreferences(uiProfile).CodexEnabled, "Adding a card does not adopt or log in to an account");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await Task.Delay(400);
        var oneCardHeight = _window.ActualHeight;
        await CaptureAsync("one-provider.png");
        await ScriptAsync("document.querySelector('[aria-label=\"AI 추가\"]').click()");
        await Task.Delay(350);
        await ScriptAsync("document.querySelector('[aria-label=\"Claude 추가\"]').click()");
        await Task.Delay(350);
        await AssertScriptAsync("document.querySelector('.menu').getBoundingClientRect().bottom <= innerHeight", "Picker removal controls fit after both providers are selected");
        await CaptureAsync("provider-picker-selected.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await Task.Delay(400);
        Check(_window.ActualHeight > oneCardHeight + 100, "Native window grows to fit the selected number of cards");
        await AssertScriptAsync("document.querySelector('.codex .number').textContent === '—' && document.querySelector('.claude .number').textContent === '—'", "Newly selected providers do not silently adopt existing accounts");
        Check(!_window.WebView!.Settings.AreHostObjectsAllowed, "General native host objects disabled");
        Check(_window.WebView.Settings.IsNonClientRegionSupportEnabled, "Native WebView2 caption dragging enabled before navigation");
        await AssertScriptAsync("getComputedStyle(document.querySelector('.drag-zone')).getPropertyValue('app-region') === 'drag' && getComputedStyle(document.querySelector('.header-actions')).getPropertyValue('app-region') === 'no-drag'", "Caption drag region excludes pin, collapse and settings controls");
        await _window.WebView.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"no-preference\"}]}");
        await Task.Delay(150);
        var now = DateTimeOffset.UtcNow;
        var codex = new ProviderUsage("codex", "ready", "계정 사용량", [new("primary", "주간", 8, now.AddDays(3).AddHours(4).ToUnixTimeSeconds())], now, "prolite");
        var claude = new ProviderUsage("claude", "ready", "Claude Code에서 수신", [new("five_hour", "5시간", 36, now.AddHours(2).AddMinutes(18).ToUnixTimeSeconds()), new("seven_day", "주간", 62, now.AddDays(2).ToUnixTimeSeconds())], now);
        _window.SetFixture(codex, new("claude", "disconnected", "로그인 필요", []));
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.triangle').classList.contains('disconnected') && !document.querySelector('.triangle').classList.contains('waving') && [...document.querySelectorAll('.triangle path')].every(path=>getComputedStyle(path).animationName==='none')", "One disconnected AI stops the logo ripple even when the other AI is connected");
        await CaptureAsync("partial-connection.png");
        _window.SetFixture(codex, claude with { Status = "error" });
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.triangle').classList.contains('disconnected') && !document.querySelector('.triangle').classList.contains('waving')", "A usage connection error grays the logo even with cached quota values");
        _window.SetFixture(codex, claude);
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.triangle').classList.contains('connected') && document.querySelector('.triangle').classList.contains('waving') && getComputedStyle(document.querySelector('.triangle')).color==='rgb(196, 232, 161)'", "Reconnecting every selected AI restores the green idle ripple");
        await Task.Delay(1300);
        await AssertScriptAsync("document.querySelectorAll('.provider').length === 2 && document.querySelector('.codex .number').textContent === '92' && document.querySelector('.claude .number').textContent === '38'", "Native-to-React quota state and weekly percentage display");
        await AssertScriptAsync("document.documentElement.scrollWidth <= innerWidth && document.querySelector('.widget').getBoundingClientRect().height <= innerHeight + 2", "Expanded card fits native window without scrollbars or clipping");
        await CaptureAsync("cards.png");
        await ScriptAsync("document.querySelector('[aria-label=\"관측 측정 설정\"]').click()");
        await Task.Delay(350);
        await AssertScriptAsync("document.querySelectorAll('.measurement-options button').length === 12 && document.querySelector('.measurement-slider').min === '1' && document.querySelector('.measurement-slider').max === '180' && document.querySelector('.menu').getBoundingClientRect().bottom <= innerHeight", "Measurement slider and preset buttons fit the native window with a one-minute to three-hour range");
        async Task SliderKey(string key, int code)
        {
            await ScriptAsync("document.querySelector('.measurement-slider').focus()");
            foreach (var type in new[] { "keyDown", "keyUp" })
                await _window!.WebView!.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = code }));
            await Task.Delay(100);
        }
        await SliderKey("Home", 36);
        Check(MeasurementPreferences.Read(uiProfile).IntervalMinutes == 1, "Slider keyboard Home saves the minimum one-minute aggregation interval");
        await SliderKey("ArrowRight", 39);
        Check(MeasurementPreferences.Read(uiProfile).IntervalMinutes == 2, "Slider accepts one-minute steps between preset values");
        await AssertScriptAsync("document.querySelector('.measurement-heading output').textContent === '2m' && document.querySelector('.measurement-slider').getAttribute('aria-valuetext') === '2m'", "Custom interval is reflected in the slider label and accessible value");
        await SliderKey("End", 35);
        Check(MeasurementPreferences.Read(uiProfile).IntervalMinutes == 180, "Slider keyboard End saves the maximum three-hour aggregation interval");
        await ScriptAsync("document.querySelector('[aria-label=\"집계 주기 5m\"]').click()");
        await Task.Delay(100);
        Check(MeasurementPreferences.Read(uiProfile).IntervalMinutes == 5, "Aggregation preset button saves immediately");
        await ScriptAsync("document.querySelector('[aria-label=\"집계 주기 3h\"]').click()");
        await ScriptAsync("document.querySelector('[aria-label=\"비교 구간 1h\"]').click()");
        await Task.Delay(100);
        Check(MeasurementPreferences.Read(uiProfile) is { IntervalMinutes: 180, WindowMinutes: 60 }, "Measurement interval and comparison window save independently");
        await ScriptAsync("for(const minutes of [0,181,1.5])window.chrome.webview.postMessage({action:'setMeasurementInterval',minutes})");
        await Task.Delay(100);
        Check(MeasurementPreferences.Read(uiProfile).IntervalMinutes == 180, "Native measurement settings reject out-of-range and fractional aggregation intervals");
        await CaptureAsync("measurement-settings.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");

        // Observe real frames across two idle cycles: native state publications
        // must not restart the shimmer, trigger loading, or rotate the SVG.
        // Disable physical input to the fixture while its clock is being sampled.
        _window.IsEnabled = false;
        await ScriptAsync("window.idleLogoFrames=[]; window.idleLogoSampling=true; function sampleIdleLogo(){if(!window.idleLogoSampling)return; const svg=document.querySelector('.drag-zone .triangle'); if(svg) window.idleLogoFrames.push({at:performance.now(),transform:svg.style.transform,brightness:[...svg.querySelectorAll('path')].map(p=>Number(getComputedStyle(p).filter.match(/brightness\\(([^)]+)\\)/)?.[1]??1))}); requestAnimationFrame(sampleIdleLogo);} requestAnimationFrame(sampleIdleLogo)");
        await Task.Delay(8000);
        await ScriptAsync("window.idleLogoSampling=false");
        _window.IsEnabled = true;
        File.WriteAllText(Path.Combine(_output, "idle-logo-frames.json"), await ScriptAsync("window.idleLogoFrames"));
        await AssertScriptAsync("[0,1,2].every(i=>{const peaks=window.idleLogoFrames.filter(f=>f.brightness[i]>1.2);const starts=peaks.filter((f,j)=>j===0||f.at-peaks[j-1].at>1000);return starts.length>=2&&starts.slice(2).every((f,j)=>Math.abs(f.at-starts[j+1].at-3000)<350)}) && new Set(window.idleLogoFrames.map(f=>f.transform)).size===1", "Idle logo ripples through all three pieces every three seconds without rotating");

        _window.SetFixture(codex, claude, busy: true);
        await Task.Delay(400);
        var transform1 = await ScriptAsync("document.querySelector('.triangle').outerHTML");
        await Task.Delay(500);
        var transform2 = await ScriptAsync("document.querySelector('.triangle').outerHTML");
        Check(transform1 != transform2 && await ScriptAsync("getComputedStyle(document.querySelector('.triangle path')).animationDuration") == "\"0.85s\"", "Synchronization speeds up the triangle wave and rotates the logo");
        await AssertScriptAsync("document.querySelector('.codex .number').textContent === '92'", "Refresh preserves visible quota values");
        await CaptureAsync("loading.png");
        var damaged = codex with { Windows = [codex.Windows[0] with { UsedPercent = 91 }], UpdatedAt = now.AddSeconds(1) };
        _window.SetFixture(damaged, claude);
        await Task.Delay(300);
        await AssertScriptAsync($"(()=>{{const times=[...document.querySelectorAll('.codex .observed-usage time')];return times.length>0&&times.every(time=>new Date(time.dateTime).getTime()==={damaged.UpdatedAt!.Value.ToUnixTimeMilliseconds()});}})()",
            "Full card observation clock advances to the latest received snapshot instead of the initial baseline");
        await AssertScriptAsync("document.querySelector('.codex .meter-trail').getBoundingClientRect().width > document.querySelector('.codex .meter-fill').getBoundingClientRect().width + 20", "Quota damage leaves a delayed HP trail before catching up");
        await CaptureAsync("damage-trail.png");
        await Task.Delay(1800);
        await AssertScriptAsync("document.querySelector('.codex .number').textContent === '9' && document.querySelector('.codex').classList.contains('low')", "Quota updates settle to correct values with low-balance styling");
        await AssertScriptAsync("document.querySelector('.codex .today-usage').textContent.includes('83') && Math.abs(document.querySelector('.codex .meter-trail').getBoundingClientRect().width - document.querySelector('.codex .meter-fill').getBoundingClientRect().width) < 2", "Daily observed consumption stays visible after the transient damage trail settles");
        await _window.WebView.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"reduce\"}]}");
        var reducedDamage = damaged with { Windows = [codex.Windows[0] with { UsedPercent = 93 }], UpdatedAt = now.AddSeconds(2) };
        _window.SetFixture(reducedDamage, claude);
        await Task.Delay(120);
        await AssertScriptAsync("Math.abs(document.querySelector('.codex .meter-trail').getBoundingClientRect().width - document.querySelector('.codex .meter-fill').getBoundingClientRect().width) < 2", "Reduced-motion setting removes delayed HP animation");
        await AssertScriptAsync("[...document.querySelectorAll('.drag-zone .triangle path')].every(p=>getComputedStyle(p).filter==='brightness(1)')", "Reduced-motion setting disables the idle triangle highlight");
        await _window.WebView.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"no-preference\"}]}");
        await CaptureAsync("low-balance.png");

        await ScriptAsync("document.querySelector('[aria-label=\"항상 위에 고정\"]').click()");
        await WaitAsync(() => !_window.Topmost, "unpin");
        Check(true, "Pin control changes native Topmost state");
        var fullHeight = _window.ActualHeight;
        var anchoredRight = _window.Left + _window.ActualWidth;
        await AssertScriptAsync("document.querySelectorAll('.header-actions .view-toggle').length===1 && document.querySelector('.view-toggle').getAttribute('aria-label')==='간략히 보기' && !document.querySelector('.header-actions .lucide-minus') && !document.querySelector('.header-actions .lucide-plus') && getComputedStyle(document.querySelector('.logo-toggle')).getPropertyValue('app-region')==='no-drag'", "A single detail button replaces plus/minus while the header logo remains clickable outside the drag region");
        await ScriptAsync("document.querySelector('.logo-toggle').click()");
        await Task.Delay(650);
        Check(_window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth <= 50 && _window.ActualHeight <= 50,
            "Clicking the full-card triangle goes directly to the logo");
        await AssertScriptAsync("document.querySelector('.logo-shell').title.includes('자세히 보기로 돌아가기')", "Minimized full card offers restoration to its remembered detail level");
        await ScriptAsync("document.querySelector('.logo-shell').click()");
        await Task.Delay(650);
        Check(_window.ViewMode == WidgetViewMode.Expanded && Math.Abs(_window.ActualHeight-fullHeight)<3,
            "Clicking the logo restores the full card rather than the compact card");
        _window.ResizeFrames.Clear();
        await StartLogoSamplingAsync();
        await ScriptAsync("document.querySelector('[aria-label=\"간략히 보기\"]').click()");
        await Task.Delay(55);
        Check(Math.Abs(_window.ActualHeight - fullHeight) < 2, "Content hides before the native frame begins shrinking");
        await AssertScriptAsync("Number(getComputedStyle(document.querySelector('[data-view=\"1\"]')).opacity) < .05 && document.querySelector('.transition-mark') !== null", "Incoming content stays hidden while the triangle carries the transition");
        var transitionRotation = await ScriptAsync("document.querySelector('.transition-mark .triangle').style.transform");
        await Task.Delay(90);
        Check(_window.IsResizing && _window.ActualHeight < fullHeight && _window.ActualHeight > 160,
            "Full-to-summary transition passes through intermediate native heights");
        Check(transitionRotation != await ScriptAsync("document.querySelector('.transition-mark .triangle').style.transform"), "Transition triangle rotates even when usage refresh is idle");
        await Task.Delay(750);
        var resizeFrames = _window.ResizeFrames.ToArray();
        File.WriteAllText(Path.Combine(_output, "native-resize-frames.json"), JsonSerializer.Serialize(resizeFrames, WidgetStorage.Json));
        Check(resizeFrames.Length >= 8 && resizeFrames.Zip(resizeFrames.Skip(1)).All(pair => pair.Second.ElapsedMs >= pair.First.ElapsedMs),
            "Native fold uses a continuous frame clock without restarting midway");
        Check(resizeFrames.Zip(resizeFrames.Skip(1)).All(pair => pair.Second.Height <= pair.First.Height + .1),
            "Native fold never jumps back toward its original height between frames");
        Check(resizeFrames.All(frame => frame.ViewportFrozen)
            && resizeFrames.Max(frame => frame.ViewportHeight) - resizeFrames.Min(frame => frame.ViewportHeight) < .1,
            "Hidden card keeps one fixed WebView viewport throughout the native fold");
        File.WriteAllText(Path.Combine(_output, "logo-samples-full-compact.json"), await ScriptAsync("window.logoSamples"));
        await AssertScriptAsync("(()=>{const samples=window.logoSamples.filter(sample=>sample.angle>20&&sample.angle<90);return samples.length>5&&Math.max(...samples.map(sample=>sample.viewportHeight))-Math.min(...samples.map(sample=>sample.viewportHeight))<=1&&Math.max(...samples.map(sample=>sample.viewportWidth))-Math.min(...samples.map(sample=>sample.viewportWidth))<=1;})()",
            "Browser layout viewport stays fixed while the native window changes size");
        await AssertScriptAsync("window.logoSampling=false; window.logoSamples.length > 5 && window.logoSamples.every(sample=>sample.visible<=1) && !document.querySelector('.transition-mark')", "Every sampled frame has at most one visible logo, including the rotation-to-static handoff");
        await AssertScriptAsync("window.logoSamples.every(sample=>sample.visible!==1||sample.peakOpacity>.95)", "Logo handoff waits for the incoming card fade instead of briefly dimming its logo");
        await AssertScriptAsync("(()=>{const angles=window.logoSamples.filter(sample=>sample.angle!==null).map(sample=>sample.angle);return angles.length>3&&Math.max(...angles)>100&&Math.max(...angles)<=120.1&&angles.every((angle,index)=>index===0||angle>=angles[index-1]-.1);})()", "Transition rotates only 120 degrees with easing and no restart or reversal");
        await AssertScriptAsync("document.querySelector('.drag-zone .triangle').style.transform.includes('rotate(120deg)')", "Static logo preserves the completed 120-degree orientation without snapping back");
        Check(_window.ActualHeight < 250, "Collapse shrinks the actual native window");
        await AssertScriptAsync("document.querySelector('.compact .claude > strong').textContent === '38%' && document.querySelector('.compact .claude .compact-label').textContent === '주간' && document.querySelector('.compact .claude .compact-subvalue').textContent === '64%' && document.querySelectorAll('.compact .claude .compact-reset').length === 2 && [...document.querySelectorAll('.compact .claude .compact-reset')].every(n=>n.textContent.trim()!== '—' && n.title.includes('다음 리셋')) && document.querySelector('.widget').getBoundingClientRect().height <= innerHeight + 2 && document.querySelector('.compact .claude').scrollWidth <= document.querySelector('.compact .claude').clientWidth", "Compact Claude shows weekly and five-hour quotas with reset countdowns and exact-time tooltips without clipping");
        await CaptureAsync("compact.png");
        await AssertScriptAsync($"(()=>{{const time=document.querySelector('.compact .codex .observed-usage time');return time&&new Date(time.dateTime).getTime()==={reducedDamage.UpdatedAt!.Value.ToUnixTimeMilliseconds()};}})()",
            "Compact observation clock also follows the latest received snapshot");
        File.WriteAllText(Path.Combine(_output, "compact-text-layout.json"), await ScriptAsync("[...document.querySelectorAll('.compact strong')].map(strong=>({provider:strong.parentElement.className,text:strong.textContent,rect:strong.getBoundingClientRect().toJSON(),children:[...strong.children].map(child=>({text:child.textContent,rect:child.getBoundingClientRect().toJSON(),font:getComputedStyle(child).font,display:getComputedStyle(child).display,color:getComputedStyle(child).color,opacity:getComputedStyle(child).opacity}))}))"));
        await AssertScriptAsync("document.querySelectorAll('.header-actions .view-toggle').length===1 && document.querySelector('.view-toggle').getAttribute('aria-label')==='자세히 보기' && !document.querySelector('.header-actions .lucide-minus') && !document.querySelector('.header-actions .lucide-plus')", "Compact card keeps the same single detail button with the opposite action");
        await AssertScriptAsync("document.querySelector('.compact .codex .today-usage').textContent.includes('85') && document.documentElement.scrollWidth <= innerWidth", "Compact summary retains the observed daily amount without overflow");
        await ScriptAsync("document.querySelector('[aria-label=\"로고만 표시\"]').click()");
        await Task.Delay(145);
        Check(_window.IsResizing && _window.ActualWidth > 48 && _window.ActualWidth < 304,
            "Summary-to-logo transition animates the actual native width");
        Check(Math.Abs(_window.Left + _window.ActualWidth - anchoredRight) < 3, $"Resize animation keeps the native right edge anchored (expected {anchoredRight:F2}, actual {_window.Left + _window.ActualWidth:F2})");
        await AssertScriptAsync("Number(getComputedStyle(document.querySelector('[data-view=\"2\"]')).opacity) < .05 && [...document.querySelectorAll('.widget')].every(card=>Number(getComputedStyle(card).opacity)<.01)", "Logo morph contains no visible cropped outgoing card text or meters");
        await CaptureAsync("folding.png");
        await Task.Delay(650);
        Check(_window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth <= 50 && _window.ActualHeight <= 50,
            "Second collapse shrinks the native window to a 48px logo");
        await AssertScriptAsync("document.querySelector('.logo-shell .triangle') !== null && !document.querySelector('.header') && !document.querySelector('.compact') && document.body.innerText.trim() === ''",
            "Logo mode contains only the triangle without hidden card space or text");
        await CaptureAsync("logo-only.png");
        _window.SetFixture(reducedDamage, new("claude", "disconnected", "로그인 필요", []));
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.logo-shell .triangle').classList.contains('disconnected') && !document.querySelector('.logo-shell .triangle').classList.contains('waving')", "Logo-only mode also stops the ripple when an AI disconnects");
        await CaptureAsync("logo-disconnected.png");
        _window.SetFixture(reducedDamage, claude);
        await Task.Delay(150);
        await ScriptAsync("document.querySelector('.logo-shell').click()");
        await Task.Delay(145);
        Check(_window.IsResizing && _window.ActualWidth > 48 && _window.ActualWidth < 304,
            "Logo expansion passes through intermediate native widths");
        await ScriptAsync("window.chrome.webview.postMessage({action:'collapse'})");
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth == 48 && !_window.IsResizing, "rapid reversal settles");
        Check(_window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth == 48 && !_window.IsResizing,
            "Rapidly reversing the transition settles at the latest requested view");
        var handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        bool handled = false;
        _window.WindowProc(handle, 0x0231, IntPtr.Zero, IntPtr.Zero, ref handled);
        _window.Left += 8;
        await Task.Delay(120);
        _window.WindowProc(handle, 0x0232, IntPtr.Zero, IntPtr.Zero, ref handled);
        await Task.Delay(120);
        Check(_window.ViewMode == WidgetViewMode.Logo, "Dragging the logo preserves logo mode");
        _window.WindowProc(handle, 0x0231, IntPtr.Zero, IntPtr.Zero, ref handled);
        _window.WindowProc(handle, 0x0232, IntPtr.Zero, IntPtr.Zero, ref handled);
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Compact, "native logo click restores summary");
        await Task.Delay(500);
        Check(_window.ActualWidth == 304, "Stationary native caption click restores the summary width");
        await ScriptAsync("document.querySelector('[aria-label=\"로고만 표시\"]').click()");
        await Task.Delay(500);
        _window.Close();
        _window = new MainWindow(uiProfile, testMode: true, claudeSettings: settings);
        _window.Show();
        await WaitAsync(() => _window.Ready, "restored logo ready");
        Check(_window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth <= 50 && _window.ActualHeight <= 50,
            "Logo mode and native window size survive restart");
        await AssertScriptAsync("document.querySelector('.logo-shell').title.includes('간략히 보기로 돌아가기')", "The compact-card restoration choice survives restart while minimized");
        Check(DashboardPreferences.Read(uiProfile, new()).ProviderIds.SequenceEqual(new[] { "codex", "claude" }), "Selected AI cards and their order survive restart");
        Check(MeasurementPreferences.Read(uiProfile) is { IntervalMinutes: 180, WindowMinutes: 60 }, "Both measurement settings survive restart");
        _window.SetFixture(codex, claude);
        await ScriptAsync("document.querySelector('.logo-shell').click()");
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Compact, "logo expands to summary");
        await Task.Delay(450);
        // Start measuring the next spin only after the preceding logo-to-summary spin ends.
        var previousSpin = Stopwatch.StartNew();
        while (await ScriptAsync("document.querySelector('.transition-mark') === null") != "true")
        {
            if (previousSpin.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Previous logo spin did not settle");
            await Task.Delay(30);
        }
        await ScriptAsync("window.expansionStart=Number(document.querySelector('.drag-zone .triangle').style.transform.match(/rotate\\(([-\\d.]+)deg\\)/)?.[1]??0)");
        await StartLogoSamplingAsync();
        await ScriptAsync("document.querySelector('[aria-label=\"자세히 보기\"]').click()");
        await Task.Delay(700);
        File.WriteAllText(Path.Combine(_output, "expansion-frames.json"), await ScriptAsync("({start:window.expansionStart,samples:window.logoSamples,transform:document.querySelector('.drag-zone .triangle').style.transform})"));
        await AssertScriptAsync("window.logoSampling=false; (()=>{const angles=window.logoSamples.filter(sample=>sample.angle!==null).map(sample=>sample.angle);return angles.length>5&&angles[0]<=window.expansionStart+.1&&angles.at(-1)<window.expansionStart-110&&angles.every((angle,index)=>index===0||angle<=angles[index-1]+.1)&&window.logoSamples.every(sample=>sample.visible<=1);})()", "Expansion rotates counterclockwise by 120 degrees without reversing or overlapping logos");
        await AssertScriptAsync("Math.abs(Number(document.querySelector('.drag-zone .triangle').style.transform.match(/rotate\\(([-\\d.]+)deg\\)/)?.[1]??0)-(window.expansionStart-120))<.1", "Expanded static logo retains the counterclockwise finish without snapping back");
        await ScriptAsync("window.chrome.webview.postMessage({action:'toggleMotion'})");
        await Task.Delay(150);
        await ScriptAsync("document.querySelector('[aria-label=\"간략히 보기\"]').click()");
        await Task.Delay(100);
        Check(!_window.IsResizing && _window.ActualHeight < 250, "Widget motion toggle skips native fold animation");
        await ScriptAsync("document.querySelector('[aria-label=\"자세히 보기\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("window.chrome.webview.postMessage({action:'toggleMotion'})");
        await Task.Delay(150);
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(350);
        await AssertScriptAsync("document.querySelector('.menu').getBoundingClientRect().bottom < innerHeight", "Settings menu fits the window");
        await CaptureAsync("settings.png");
        await ScriptAsync("document.querySelector('[aria-label=\"화면 설정\"]').click()");
        await Task.Delay(250);
        await AssertScriptAsync("document.querySelectorAll('.dock-option').length===4 && document.querySelectorAll('.dock-option[aria-pressed=\"true\"]').length===1", "Settings offers all four screen corners with the current anchor selected");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"계정 관리\"]').click()");
        await Task.Delay(250);
        await AssertScriptAsync("document.querySelectorAll('.account-box').length === 2", "Account manager lists both connected providers");
        foreach (var id in new[] { "codex", "claude" })
        {
            await ExpandAccountAsync(id);
            await AssertScriptAsync($"document.querySelector('.account-box[data-account-id=\"{id}\"] .account-primary').textContent.includes('다른 계정으로 로그인') && document.querySelector('.account-box[data-account-id=\"{id}\"]').textContent.includes('기존 CLI 계정 사용')", "Connected account details expose switch-account and existing-CLI choices");
        }
        await CaptureAsync("account-login.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await VerifyCornerAnchorsAsync(uiProfile, settings, codex, claude);
        _window.SetFixture(codex with { Windows = [new("primary", "주간", 91, now.AddSeconds(-1).ToUnixTimeSeconds())] }, new("claude", "disconnected", "Claude Code 사용량을 연결해주세요.", []));
        await Task.Delay(800);
        await AssertScriptAsync("document.querySelector('.codex .number').textContent === '—' && document.querySelector('.claude .number').textContent === '—'", "Expired and missing windows do not show stale or fabricated availability");
        await CaptureAsync("waiting.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(350);
        await ScriptAsync("document.querySelector('[aria-label=\"계정 관리\"]').click()");
        await Task.Delay(150);
        await ExpandAccountAsync("codex");
        await ScriptAsync("document.querySelector('[aria-label=\"Codex 계정 항목 삭제\"]').click()");
        await Task.Delay(150);
        await ExpandAccountAsync("claude");
        await ScriptAsync("document.querySelector('[aria-label=\"Claude 계정 항목 삭제\"]').click()");
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.account-empty')!==null && document.querySelectorAll('.account-box').length===0", "Deleting every account removes all account tags and restore controls");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await Task.Delay(400);
        await AssertScriptAsync("document.querySelector('.empty-dashboard')!==null && document.querySelectorAll('.provider').length===0", "Removing all cards returns to the empty dashboard");
        Check(DashboardPreferences.Read(uiProfile, new(true, true, true, true)).ProviderIds.Length == 0, "Deleting every account persists an empty dashboard");
        Check(AccountProfiles.ReadPreferences(uiProfile) == new ConnectionPreferences(), "Deleting the primary accounts clears their saved connection flags");
        await CaptureAsync("removed-all.png");
        _window.WebView!.Navigate("https://example.com/");
        await Task.Delay(250);
        Check(_window.WebView.Source == _window.WidgetAddress, "Remote navigation blocked from privileged local card");
        if (live)
        {
            await ScriptAsync("window.chrome.webview.postMessage({action:'addProvider',provider:'codex'})");
            await Task.Delay(200);
            await _window.SelectExistingAsync("codex");
            Check(_window.CodexUsage.Status == "ready" && _window.CodexUsage.Windows.Count > 0, "Live Codex app-server reads signed-in account without a model turn");
            await Task.Delay(1200);
            await CaptureAsync("live-codex.png");
        }
        await VerifyMultipleCodexAsync();
        await VerifyMultipleAiAsync();
    }

    private static async Task VerifyMultipleAiAsync()
    {
        _window?.Close();
        var directory = Path.Combine(_output, "multi-ai-" + Guid.NewGuid().ToString("N"));
        var legacy = new ConnectionPreferences(CodexEnabled: true, ClaudeEnabled: true);
        var registry = AiAccountPreferences.Read(directory, legacy);
        Check(registry.Profiles.Select(p => p.Service).SequenceEqual(new[] { "codex", "claude" }) && registry.Profiles.All(p => p.Enabled),
            "Common account registry migrates both existing services and their connection settings");
        registry = registry.Add("codex", out var extraCodex);
        registry = registry.Update(extraCodex with { Enabled = true });
        registry = registry.Add("claude", out var extraClaude);
        registry = registry.Update(extraClaude with { Enabled = true });
        WidgetStorage.Write(Path.Combine(directory, "account-profiles.json"), registry);
        WidgetStorage.Write(Path.Combine(directory, "connections.json"), legacy);
        var ids = new[] { "codex", "claude", extraCodex.Id, extraClaude.Id };
        WidgetStorage.Write(Path.Combine(directory, "dashboard.json"), new DashboardPreferences(ids));
        var firstClaudeHome = AccountProfiles.ClaudeHome(directory);
        var secondClaudeHome = AccountProfiles.ClaudeHome(directory, extraClaude.Id);
        Check(firstClaudeHome != secondClaudeHome && firstClaudeHome == Path.Combine(directory, "Accounts", "Claude"),
            "Claude keeps its original login directory and gives each extra account a separate config folder");
        foreach (var invalid in new[] { "claude-../../private", "claude-invalid", extraCodex.Id })
        {
            try { AccountProfiles.ClaudeHome(directory, invalid); throw new Exception("Unsafe Claude account path accepted"); }
            catch (ArgumentException) { Check(true, "Invalid or cross-service Claude profile ID rejected"); }
        }
        foreach (var profile in registry.Profiles)
        {
            var home = profile.Service == "codex" ? AccountProfiles.CodexHome(directory, profile.Id) : AccountProfiles.ClaudeHome(directory, profile.Id);
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "fixture-login"), "signed-in");
            if (profile.Service == "claude")
            {
                WidgetStorage.Write(Path.Combine(home, "settings.json"), new { theme = profile.Name, statusLine = new { type = "command", command = "printf 'original'" } });
                new ClaudeBridge(Path.Combine(home, "UsageBridge"), Path.Combine(home, "settings.json")).Install();
            }
        }
        CodexClient CodexFactory(CodexProfile profile) => new(AccountProfiles.CodexHome(directory, profile.Id), () =>
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "UsageWidget.Verification.exe"));
            foreach (var argument in new[] { "--rpc-fixture", "--fixture-email", profile.Id == "codex" ? "codex-one@example.invalid" : "codex-two@example.invalid", "--fixture-used", profile.Id == "codex" ? "12" : "27" }) info.ArgumentList.Add(argument);
            return info;
        });
        ClaudeAuthClient ClaudeFactory(AiAccountProfile profile) => new(AccountProfiles.ClaudeHome(directory, profile.Id), arguments =>
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "UsageWidget.Verification.exe"));
            foreach (var argument in new[] { "--claude-account-fixture", "--fixture-email", profile.Id == "claude" ? "claude-one@example.invalid" : "claude-two@example.invalid", "--fixture-five-hour", profile.Id == "claude" ? "20" : "45", "--fixture-weekly", profile.Id == "claude" ? "30" : "68", "--fixture-reset", DateTimeOffset.UtcNow.AddDays(2).ToString("O") }) info.ArgumentList.Add(argument);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            info.Environment["ANTHROPIC_API_KEY"] = "fixture-inherited-key";
            info.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = "fixture-inherited-token";
            return info;
        });
        _window = new MainWindow(directory, testMode: true, codexFactory: CodexFactory, openCodexLogin: _ => { }, claudeFactory: ClaudeFactory);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Mixed AI React ready");
        await _window.RefreshAsync(true);
        await Task.Delay(1100);
        await AssertScriptAsync("document.querySelectorAll('.provider.codex').length===2 && document.querySelectorAll('.provider.claude').length===2 && [...document.querySelectorAll('.provider .number')].map(node=>node.textContent).join(',')==='88,70,73,32'",
            "Mixed dashboard routes independent Codex and Claude CLI quotas to four account cards");
        foreach (var home in new[] { firstClaudeHome, secondClaudeHome })
        {
            using var env = JsonDocument.Parse(File.ReadAllText(Path.Combine(home, "fixture-environment.json")));
            Check(env.RootElement.GetProperty("home").GetString() == home && !env.RootElement.GetProperty("apiKeyInherited").GetBoolean()
                && !env.RootElement.GetProperty("oauthTokenInherited").GetBoolean(), "Each Claude CLI receives its own config directory without inherited credentials");
        }
        var firstSnapshot = Path.Combine(firstClaudeHome, "UsageBridge", "claude-usage.json");
        var secondSnapshot = Path.Combine(secondClaudeHome, "UsageBridge", "claude-usage.json");
        Check(File.Exists(firstSnapshot) && File.Exists(secondSnapshot) && File.ReadAllText(firstSnapshot) != File.ReadAllText(secondSnapshot),
            "Claude usage bridges store different accounts in different snapshot files");
        await CaptureAsync("multi-ai-cards.png");
        await ScriptAsync("document.querySelector('[aria-label=\"간략히 보기\"]').click()");
        await Task.Delay(1000);
        await AssertScriptAsync($"document.querySelector('.compact>[data-account-id=\"claude\"] strong').textContent==='70%' && document.querySelector('.compact>[data-account-id=\"{extraClaude.Id}\"] strong').textContent==='32%' && document.querySelector('.compact>[data-account-id=\"{extraClaude.Id}\"] .compact-subvalue').textContent==='55%'",
            "Every compact Claude account independently prioritizes weekly quota and retains its own five-hour subquota");
        await CaptureAsync("multi-ai-compact.png");
        await ScriptAsync("document.querySelector('[aria-label=\"자세히 보기\"]').click()");
        await Task.Delay(850);
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"계정 관리\"]').click()");
        await Task.Delay(400);
        await ExpandAccountAsync(extraClaude.Id);
        await AssertScriptAsync($"document.querySelector('[aria-label=\"AI 계정 추가\"]')!==null && document.querySelectorAll('.account-box').length===4 && !document.querySelector('.account-box[data-account-id=\"{extraClaude.Id}\"]').textContent.includes('기존 CLI 계정 사용')",
            "Common account manager offers an AI chooser and extra Claude slots stay isolated from the system CLI");
        await ScriptAsync($"document.querySelector('.account-box[data-account-id=\"{extraClaude.Id}\"] .account-primary').click()");
        await Task.Delay(900);
        await AssertScriptAsync($"document.querySelector('.account-box[data-account-id=\"{extraClaude.Id}\"] .account-summary-copy small').textContent==='claude-two@example.invalid' && document.querySelector('.account-box[data-account-id=\"claude\"] .account-summary-copy small').textContent==='claude-one@example.invalid'",
            "Extra Claude login button completes the selected CLI profile's auth flow without replacing the first account");
        await CaptureAsync("multi-ai-accounts.png");
        await ScriptAsync("document.querySelector('[aria-label=\"AI 계정 추가\"]').click()");
        await Task.Delay(350);
        await AssertScriptAsync("!document.querySelector('[aria-label=\"Codex 추가\"]').disabled && !document.querySelector('[aria-label=\"Claude 추가\"]').disabled",
            "Both AI services remain available for repeated account additions");
        await CaptureAsync("multi-ai-picker.png");
        var beforeCount = AiAccountPreferences.Read(directory, new()).Profiles.Length;
        await ScriptAsync("document.querySelector('[aria-label=\"Claude 추가\"]').click()");
        await Task.Delay(300);
        var restored = AiAccountPreferences.Read(directory, new());
        var added = restored.Profiles.Last();
        Check(restored.Profiles.Length == beforeCount + 1 && added.Service == "claude" && !added.Enabled,
            "Service picker adds a distinct persisted Claude slot without adopting another login");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"계정 관리\"]').click()");
        await Task.Delay(300);
        await ExpandAccountAsync(extraClaude.Id);
        await ScriptAsync($"[...document.querySelectorAll('.account-box[data-account-id=\"{extraClaude.Id}\"] .account-secondary')].find(button=>button.textContent==='로그아웃').click()");
        await Task.Delay(700);
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await AssertScriptAsync($"document.querySelector('[data-account-id=\"{extraClaude.Id}\"] .number').textContent==='—' && document.querySelector('[data-account-id=\"claude\"] .number').textContent==='70' && document.querySelector('[data-account-id=\"codex\"] .number').textContent==='88'",
            "Logging out an extra Claude account preserves both the original Claude and the Codex accounts");
        using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(secondClaudeHome, "settings.json"))))
            Check(settings.RootElement.GetProperty("statusLine").GetProperty("command").GetString() == "printf 'original'",
                "Claude disconnect restores only that account's original statusline configuration");
        Check(File.Exists(firstSnapshot) && !File.Exists(secondSnapshot) && File.Exists(Path.Combine(firstClaudeHome, "fixture-login"))
            && !File.Exists(Path.Combine(secondClaudeHome, "fixture-login")), "Claude logout and cache clearing remain local to the chosen account");
        await ScriptAsync($"window.chrome.webview.postMessage({{action:'removeProvider',provider:'claude'}})");
        await Task.Delay(250);
        Check(AiAccountPreferences.Read(directory, new()).Profiles.Single(p => p.Id == "claude") == new AiAccountProfile("claude", "claude", "Claude"),
            "Deleting the primary Claude account clears its saved tag and connection settings");
        Check(File.Exists(Path.Combine(firstClaudeHome, "fixture-login")) && File.Exists(firstSnapshot),
            "Deleting a Claude app entry preserves its CLI login and local history");
        using (var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(firstClaudeHome, "settings.json"))))
            Check(settings.RootElement.GetProperty("statusLine").GetProperty("command").GetString() == "printf 'original'",
                "Deleting a Claude account restores its original statusline");
        var expectedIds = DashboardPreferences.Read(directory, new()).ProviderIds;
        _window.Close();
        _window = new MainWindow(directory, testMode: true, codexFactory: CodexFactory, openCodexLogin: _ => { }, claudeFactory: ClaudeFactory);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Restored mixed AI React ready");
        await _window.RefreshAsync(true);
        await Task.Delay(1100);
        Check(await ScriptAsync("document.querySelectorAll('.provider').length") == expectedIds.Length.ToString(), "Mixed-service account selections and order survive restart");
        await AssertScriptAsync($"document.querySelector('[data-account-id=\"{extraClaude.Id}\"] .number').textContent==='—' && !document.querySelector('[data-account-id=\"claude\"]') && document.querySelector('[data-account-id=\"codex\"] .number').textContent==='88'",
            "Restart preserves deleted and logged-out Claude states without affecting Codex");
        var savedMetadata = File.ReadAllText(Path.Combine(directory, "account-profiles.json"));
        Check(!savedMetadata.Contains("@") && !savedMetadata.Contains("fixture-inherited"), "Shared account registry contains neither account emails nor credentials");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"계정 관리\"]').click()");
        await Task.Delay(400);
        await AssertScriptAsync("!document.querySelector('.account-box[data-account-id=\"claude\"]') && !document.querySelector('.account-restore')",
            "A deleted primary account leaves no saved tag or restore action after restart");
        var remainingCount = AiAccountPreferences.Read(directory, new()).Profiles.Length;
        await ScriptAsync("document.querySelector('[aria-label=\"AI 계정 추가\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"Claude 추가\"]').click()");
        await Task.Delay(300);
        Check(AiAccountPreferences.Read(directory, new()).Profiles.Length == remainingCount + 1
            && !DashboardPreferences.Read(directory, new()).ProviderIds.Contains("claude"),
            "Adding a sibling AI account does not restore the deleted primary account");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        var preferencesBeforeRename = AiAccountPreferences.Read(directory, new()).Profiles.Select(p => (p.Id, p.Service, p.Name, p.Enabled, p.UseSystem)).ToArray();
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"계정 관리\"]').click()");
        await Task.Delay(350);
        await ExpandAccountAsync("codex");
        await ScriptAsync("(()=>{const input=document.getElementById('nickname-codex');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'  개인용  ');input.dispatchEvent(new Event('input',{bubbles:true}));})()");
        await Task.Delay(100);
        await ScriptAsync("document.querySelector('.account-box[data-account-id=\"codex\"] .account-nickname button').click()");
        await Task.Delay(250);
        await ExpandAccountAsync(extraClaude.Id);
        await ScriptAsync($"(()=>{{const input=document.getElementById('nickname-{extraClaude.Id}');Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'업무용');input.dispatchEvent(new Event('input',{{bubbles:true}}));}})()");
        await Task.Delay(100);
        await ScriptAsync($"document.querySelector('.account-box[data-account-id=\"{extraClaude.Id}\"] .account-nickname button').click()");
        await Task.Delay(250);
        var renamed = AiAccountPreferences.Read(directory, new());
        Check(renamed.Profiles.Single(p => p.Id == "codex").Nickname == "개인용" && renamed.Profiles.Single(p => p.Id == extraClaude.Id).Nickname == "업무용",
            "Nickname editor saves trimmed independent aliases for Codex and Claude");
        Check(renamed.Profiles.Select(p => (p.Id, p.Service, p.Name, p.Enabled, p.UseSystem)).SequenceEqual(preferencesBeforeRename),
            "Saving aliases preserves account IDs, default names and connection settings");
        await CaptureAsync("account-nicknames.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await AssertScriptAsync($"document.querySelector('[data-account-id=\"codex\"] .provider-name').textContent.includes('개인용') && document.querySelector('[data-account-id=\"{extraClaude.Id}\"] .provider-name').textContent.includes('업무용') && document.querySelector('[data-account-id=\"codex\"] .number').textContent==='88'",
            "Detailed cards show saved nicknames without clearing their usage values");
        await ScriptAsync("document.querySelector('[aria-label=\"간략히 보기\"]').click()");
        await Task.Delay(950);
        await AssertScriptAsync("document.querySelector('.compact>[data-account-id=\"codex\"] .compact-name').textContent.includes('개인용') && document.querySelector('.compact>[data-account-id=\"codex\"] .compact-name').title.includes('Codex') && document.documentElement.scrollWidth<=innerWidth",
            "Compact cards show nicknames and retain default AI identity in the tooltip without overflow");
        await AssertScriptAsync($"document.querySelector('.compact>[data-account-id=\"codex\"] .account-service').textContent==='Codex' && document.querySelector('.compact>[data-account-id=\"{extraClaude.Id}\"] .account-service').textContent==='Claude 2' && document.querySelector('.compact>[data-account-id=\"{extraClaude.Id}\"] .account-alias').textContent==='업무용'",
            "Aliases remain beside visible Codex and Claude names instead of replacing service identity");
        await CaptureAsync("nickname-compact.png");
        _window.Close();
        _window = new MainWindow(directory, testMode: true, codexFactory: CodexFactory, openCodexLogin: _ => { }, claudeFactory: ClaudeFactory);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Nickname restart ready");
        await _window.RefreshAsync(true);
        await Task.Delay(1100);
        await AssertScriptAsync("document.querySelector('.compact>[data-account-id=\"codex\"] .compact-name').textContent.includes('개인용')",
            "Saved account nicknames survive an application restart");
        await ScriptAsync("window.chrome.webview.postMessage({action:'setAccountNickname',accountId:'codex',nickname:'    '})");
        await Task.Delay(200);
        Check(AiAccountPreferences.Read(directory, new()).Profiles.Single(p => p.Id == "codex").Nickname is null,
            "Empty nickname restores the preserved default account name");
        await AssertScriptAsync("document.querySelector('.compact>[data-account-id=\"codex\"] .compact-name').textContent.startsWith('Codex')",
            "Clearing a nickname immediately restores the default name in the card");
        await ScriptAsync("window.chrome.webview.postMessage({action:'setAccountNickname',accountId:'codex',nickname:'긴'.repeat(60)+'\\n'})");
        await Task.Delay(200);
        Check(AiAccountPreferences.Read(directory, new()).Profiles.Single(p => p.Id == "codex").Nickname?.Length == 24,
            "Native nickname validation enforces the maximum length even when UI limits are bypassed");
        await AssertScriptAsync("document.documentElement.scrollWidth<=innerWidth", "Long nicknames keep the compact layout inside the window");
        await AssertScriptAsync("(()=>{const label=document.querySelector('.compact>[data-account-id=\"codex\"] .compact-name');const service=label.querySelector('.account-service');return service.getBoundingClientRect().right<=label.getBoundingClientRect().right+1 && service.getBoundingClientRect().width>20 && label.querySelector('.account-alias').clientWidth<label.querySelector('.account-alias').scrollWidth;})()",
            "A long alias truncates while the actual Codex name stays fully visible");
        await ScriptAsync("window.chrome.webview.postMessage({action:'setAccountNickname',accountId:'codex',nickname:'개인용'})");
        await Task.Delay(200);
    }

    private static async Task VerifyMultipleCodexAsync()
    {
        _window?.Close();
        var profile = Path.Combine(_output, "multiple-codex-" + Guid.NewGuid().ToString("N"));
        var legacy = new ConnectionPreferences(CodexEnabled: true);
        var registry = CodexAccountPreferences.Read(profile, legacy);
        Check(registry.Profiles.Single().Id == "codex" && registry.Profiles.Single().Enabled,
            "Legacy Codex connection migrates to the original slot without losing its enabled state");
        Check(AccountProfiles.CodexHome(profile) == Path.Combine(profile, "Accounts", "Codex"),
            "Upgrade keeps the original Codex credential directory");
        registry = registry.Add(out var second);
        registry = registry.Update(second with { Enabled = true });
        registry = registry.Add(out var third);
        registry = registry.Update(third with { Enabled = true });
        WidgetStorage.Write(Path.Combine(profile, "codex-accounts.json"), registry);
        WidgetStorage.Write(Path.Combine(profile, "connections.json"), legacy);
        var ids = new[] { "codex", second.Id, third.Id };
        WidgetStorage.Write(Path.Combine(profile, "dashboard.json"), new DashboardPreferences(ids));
        Check(ids.Select(id => AccountProfiles.CodexHome(profile, id)).Distinct().Count() == 3,
            "Each Codex account has a distinct credential directory");
        foreach (var invalid in new[] { "codex-../../private", "codex-..", "codex-not-a-guid" })
        {
            try { AccountProfiles.CodexHome(profile, invalid); throw new Exception("Unsafe account path accepted"); }
            catch (ArgumentException) { Check(true, "Unsafe Codex profile ID rejected"); }
        }
        foreach (var id in ids)
        {
            var home = AccountProfiles.CodexHome(profile, id);
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "fixture-login"), "signed-in");
        }
        CodexClient Factory(CodexProfile account)
        {
            var home = AccountProfiles.CodexHome(profile, account.Id);
            Directory.CreateDirectory(home);
            return new(home, () =>
            {
                var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "UsageWidget.Verification.exe"));
                info.ArgumentList.Add("--rpc-fixture");
                info.ArgumentList.Add("--fixture-email");
                info.ArgumentList.Add(account.Id == "codex" ? "one@example.invalid" : account.Id == second.Id ? "two@example.invalid" : "three@example.invalid");
                info.ArgumentList.Add("--fixture-used");
                info.ArgumentList.Add(account.Id == "codex" ? "12" : account.Id == second.Id ? "37" : "68");
                return info;
            });
        }
        var openedLoginUrls = new List<string>();
        _window = new MainWindow(profile, testMode: true, codexFactory: Factory, openCodexLogin: openedLoginUrls.Add);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Multi-account React ready");
        await _window.RefreshAsync();
        await Task.Delay(1100);
        await AssertScriptAsync("[...document.querySelectorAll('.provider .number')].map(node=>node.textContent).join(',')==='88,63,32' && document.querySelectorAll('.provider.codex').length===3",
            "Three independent Codex CLI readers route their own quota values to three cards");
        await AssertScriptAsync("[...document.querySelectorAll('.card-foot span:first-child')].map(node=>node.textContent).join(',')==='one@example.invalid,two@example.invalid,three@example.invalid'",
            "Each usage card displays its own account identity");
        Check(DashboardPreferences.Read(profile, new()).ProviderIds.SequenceEqual(ids), "Multi-account card order persists");
        var homeSecond = AccountProfiles.CodexHome(profile, second.Id);
        File.WriteAllText(Path.Combine(homeSecond, "fixture-fail-usage"), "fail");
        await _window.RefreshAsync();
        await Task.Delay(200);
        await AssertScriptAsync($"document.querySelector('[data-account-id=\"{second.Id}\"] .status-tag').textContent.includes('연결 오류') && document.querySelector('[data-account-id=\"codex\"] .status-tag').textContent.includes('연결됨') && document.querySelector('[data-account-id=\"{third.Id}\"] .status-tag').textContent.includes('연결됨')",
            "One account's CLI failure does not interrupt the other accounts");
        File.Delete(Path.Combine(homeSecond, "fixture-fail-usage"));
        await _window.RefreshAsync();
        await ScriptAsync("document.querySelector('[aria-label=\"간략히 보기\"]').click()");
        await Task.Delay(1000);
        await AssertScriptAsync("document.querySelectorAll('.compact>div.codex').length===3 && [...document.querySelectorAll('.compact-name')].map(node=>node.textContent).join(',').includes('Codex 2') && document.documentElement.scrollWidth<=innerWidth",
            "Compact multi-account cards use the Codex theme, distinguish account names and fit horizontally");
        await CaptureAsync("multi-codex-compact.png");
        await ScriptAsync("document.querySelector('[aria-label=\"자세히 보기\"]').click()");
        await Task.Delay(900);
        await CaptureAsync("multi-codex-cards.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"계정 관리\"]').click()");
        await Task.Delay(400);
        await ExpandAccountAsync(second.Id);
        await AssertScriptAsync($"document.querySelectorAll('.account-box.codex').length===3 && document.querySelector('[aria-label=\"AI 계정 추가\"]')!==null && !document.querySelector('.account-box[data-account-id=\"{second.Id}\"]').textContent.includes('기존 CLI 계정 사용')",
            "Connection management exposes an add-account action and keeps extra accounts isolated from the system CLI");
        await CaptureAsync("multi-codex-accounts.png");
        // Drive the real UI/native OAuth path against an isolated CLI fixture; never open a real login browser.
        await ScriptAsync($"document.querySelector('.account-box[data-account-id=\"{second.Id}\"] .account-primary').click()");
        await Task.Delay(900);
        Check(openedLoginUrls.Count == 1 && AccountProfiles.IsOfficialCodexLoginUrl(openedLoginUrls[0]),
            "The extra account login button drives official OAuth for its own isolated CLI profile");
        await AssertScriptAsync($"document.querySelector('.account-box[data-account-id=\"{second.Id}\"] .account-summary-copy small').textContent==='two@example.invalid' && document.querySelector('.account-box[data-account-id=\"codex\"] .account-summary-copy small').textContent==='one@example.invalid'",
            "Completing a second account's OAuth preserves the original account identity");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        await _window.DisconnectAsync(second.Id);
        await Task.Delay(250);
        await AssertScriptAsync($"document.querySelector('[data-account-id=\"{second.Id}\"] .number').textContent==='—' && document.querySelector('[data-account-id=\"codex\"] .number').textContent==='88' && document.querySelector('[data-account-id=\"{third.Id}\"] .number').textContent==='32'",
            "Logging out one Codex slot keeps the other accounts' quotas and connections");
        var restored = CodexAccountPreferences.Read(profile, new());
        Check(!File.Exists(Path.Combine(homeSecond, "fixture-login")) && File.Exists(Path.Combine(AccountProfiles.CodexHome(profile), "fixture-login"))
            && File.Exists(Path.Combine(AccountProfiles.CodexHome(profile, third.Id), "fixture-login")),
            "Official logout RPC reaches only the selected account's isolated CLI process");
        Check(!restored.Profiles.Single(p => p.Id == second.Id).Enabled && restored.Profiles.Where(p => p.Id != second.Id).All(p => p.Enabled),
            "Logout persists only the selected slot's connection state");
        await ScriptAsync($"window.chrome.webview.postMessage({{action:'removeProvider',provider:'{third.Id}'}})");
        await Task.Delay(200);
        Check(!AiAccountPreferences.Read(profile, new()).Profiles.Any(p => p.Id == third.Id)
            && !CodexAccountPreferences.Read(profile, new()).Profiles.Any(p => p.Id == third.Id)
            && File.Exists(Path.Combine(AccountProfiles.CodexHome(profile, third.Id), "fixture-login")),
            "Deleting a Codex account removes current and legacy tags while preserving its CLI login files");
        await ScriptAsync($"window.chrome.webview.postMessage({{action:'showProvider',provider:'{third.Id}'}})");
        await Task.Delay(250);
        Check(!DashboardPreferences.Read(profile, new()).ProviderIds.Contains(third.Id), "A stale restore request cannot recreate a deleted Codex account");
        await ScriptAsync("document.querySelector('[aria-label=\"AI 추가\"]').click()");
        await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"Codex 추가\"]').click()");
        await Task.Delay(250);
        var added = CodexAccountPreferences.Read(profile, new()).Profiles.Last().Id;
        Check(added is not null && !CodexAccountPreferences.Read(profile, new()).Profiles.Single(p => p.Id == added).Enabled,
            "New Codex account persists immediately but never silently adopts a login");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()");
        for (var i = 0; i < 3; i++) await _window.AddCodexAccountAsync();
        await Task.Delay(1000);
        await AssertScriptAsync("document.querySelector('.cards').scrollHeight>document.querySelector('.cards').clientHeight && document.querySelector('footer').getBoundingClientRect().bottom<=innerHeight+2 && document.documentElement.scrollWidth<=innerWidth",
            "Many accounts scroll inside the card while the header and footer remain reachable");
        await CaptureAsync("multi-codex-scroll.png");
        var savedIds = DashboardPreferences.Read(profile, new()).ProviderIds;
        _window.Close();
        _window = new MainWindow(profile, testMode: true, codexFactory: Factory);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Restored multi-account React ready");
        await _window.RefreshAsync();
        await Task.Delay(1100);
        Check(await ScriptAsync("document.querySelectorAll('.provider').length") == savedIds.Length.ToString(),
            "All selected Codex cards survive application restart");
        await AssertScriptAsync($"document.querySelector('[data-account-id=\"{second.Id}\"] .number').textContent==='—' && document.querySelector('[data-account-id=\"codex\"] .number').textContent==='88'",
            "Restart restores logged-out and connected slots separately");
        var ledger = new DailyUsageTracker(Path.Combine(profile, "separate-ledger"));
        var now = DateTimeOffset.UtcNow;
        ProviderUsage Sample(string id, double used, DateTimeOffset at) => new(id, "ready", "fixture", [new("primary", "주간", used, now.AddDays(3).ToUnixTimeSeconds())], at);
        ledger.Observe(Sample("codex", 10, now), "first-account", now, TimeSpan.Zero);
        ledger.Observe(Sample(second.Id, 40, now), "second-account", now, TimeSpan.Zero);
        var firstTotal = ledger.Observe(Sample("codex", 15, now.AddMinutes(1)), "first-account", now.AddMinutes(1), TimeSpan.Zero);
        var secondTotal = ledger.Observe(Sample(second.Id, 49, now.AddMinutes(1)), "second-account", now.AddMinutes(1), TimeSpan.Zero);
        Check(firstTotal.Windows[0].Today?.UsedPercentagePoints == 5 && secondTotal.Windows[0].Today?.UsedPercentagePoints == 9,
            "Observed consumption accumulates independently for accounts with the same Codex quota keys");
    }

    private static async Task VerifyCornerAnchorsAsync(string profile, string settings, ProviderUsage codex, ProviderUsage claude)
    {
        var work = SystemParameters.WorkArea;
        foreach (var corner in Enum.GetValues<WidgetAnchorCorner>())
        {
            // Start with the opposite anchor, then exercise the real native drag-completion path.
            var opposite = 3 - (int)corner;
            await ScriptAsync($"window.chrome.webview.postMessage({{action:'dock',corner:{opposite}}})");
            await WaitAsync(() => _window!.AnchorCorner == (WidgetAnchorCorner)opposite && !_window.IsResizing, "opposite corner dock");
            var handle = new System.Windows.Interop.WindowInteropHelper(_window!).Handle;
            bool handled = false;
            _window!.WindowProc(handle, 0x0231, IntPtr.Zero, IntPtr.Zero, ref handled);
            var right = corner is WidgetAnchorCorner.TopRight or WidgetAnchorCorner.BottomRight;
            var bottom = corner is WidgetAnchorCorner.BottomLeft or WidgetAnchorCorner.BottomRight;
            _window.Left = right ? work.Right - _window.ActualWidth - 16 : work.Left + 16;
            _window.Top = bottom ? work.Bottom - _window.ActualHeight - 48 : work.Top + 48;
            _window.WindowProc(handle, 0x0232, IntPtr.Zero, IntPtr.Zero, ref handled);
            Check(_window.AnchorCorner == corner, $"Dragging to {corner} automatically switches the folding anchor");

            var fixedX = _window.Left + (right ? _window.ActualWidth : 0);
            var fixedY = _window.Top + (bottom ? _window.ActualHeight : 0);
            async Task<double> SampleTransition(string action)
            {
                await ScriptAsync($"window.chrome.webview.postMessage({{action:'{action}'}})");
                var watch = Stopwatch.StartNew();
                var drift = 0d;
                while (watch.ElapsedMilliseconds < 650)
                {
                    var x = _window!.Left + (right ? _window.ActualWidth : 0);
                    var y = _window.Top + (bottom ? _window.ActualHeight : 0);
                    drift = Math.Max(drift, Math.Max(Math.Abs(x - fixedX), Math.Abs(y - fixedY)));
                    await Task.Delay(16);
                }
                return drift;
            }
            var foldingDrift = Math.Max(await SampleTransition("toggleDetails"), await SampleTransition("toggleLogo"));
            Check(foldingDrift < 3 && _window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth <= 50,
                $"{corner} stays fixed throughout full-to-summary-to-logo folding");
            await CaptureAsync($"corner-{corner}-logo.png");
            var expandingDrift = Math.Max(await SampleTransition("toggleLogo"), await SampleTransition("toggleDetails"));
            Check(expandingDrift < 3 && _window.ViewMode == WidgetViewMode.Expanded && _window.ActualWidth == 304,
                $"{corner} stays fixed throughout logo-to-summary-to-full expansion");
        }
        await ScriptAsync("document.querySelector('.logo-toggle').click()");
        await Task.Delay(650);
        _window!.Close();
        _window = new MainWindow(profile, testMode: true, claudeSettings: settings);
        _window.Show();
        await WaitAsync(() => _window.Ready, "corner restore ready");
        Check(_window.AnchorCorner == WidgetAnchorCorner.BottomRight, "Screen corner anchor survives a widget restart");
        await AssertScriptAsync("document.querySelector('.logo-shell').title.includes('자세히 보기로 돌아가기')", "The full-card restoration choice survives restart while minimized");
        _window.SetFixture(codex, claude);
        await ScriptAsync("document.querySelector('.logo-shell').click()");
        await Task.Delay(650);
        Check(_window.ViewMode == WidgetViewMode.Expanded && _window.ActualWidth == 304, "Restarted logo restores the remembered full card");
        await ScriptAsync("window.chrome.webview.postMessage({action:'dock',corner:1})");
        await WaitAsync(() => _window.AnchorCorner == WidgetAnchorCorner.TopRight && !_window.IsResizing, "restore default corner after fixture checks");
    }

    private static void VerifyDailyUsage()
    {
        var directory = Path.Combine(_output, "daily-" + Guid.NewGuid().ToString("N"));
        var zone = TimeZoneInfo.CreateCustomTimeZone("TestKorea", TimeSpan.FromHours(9), "TestKorea", "TestKorea");
        var tracker = new DailyUsageTracker(directory, zone);
        var start = DateTimeOffset.Parse("2026-10-13T23:50:00+09:00");
        var reset = start.AddDays(2).ToUnixTimeSeconds();
        ProviderUsage Sample(double used, DateTimeOffset time, long? resets = null) => new("codex", "ready", "test",
            [new("primary", "5시간", used, resets ?? reset), new("secondary", "주간", used / 2, reset)], time);
        ProviderUsage Read(double used, int minute, long? resets = null) => tracker.Observe(Sample(used, start.AddMinutes(minute), resets), "account-a:plus", start.AddMinutes(minute));

        var baseline = Read(10, 0);
        Check(baseline.Windows.All(w => w.Today?.UsedPercentagePoints == 0), "First daily sample establishes a baseline without counting earlier usage");
        var consumed = Read(13, 1);
        Check(consumed.Windows[0].Today?.UsedPercentagePoints == 3 && consumed.Windows[1].Today?.UsedPercentagePoints == 1.5,
            "Daily consumption uses independent percentage-point bases for each quota");
        Check(Read(13, 1).Windows[0].Today?.UsedPercentagePoints == 3, "Repeated snapshots cannot double-count consumption");
        Read(12, 2);
        Check(Read(13.5, 3).Windows[0].Today?.UsedPercentagePoints == 3.5, "Quota corrections and rounding do not count the same consumption twice");
        tracker = new DailyUsageTracker(directory, zone);
        Check(Read(14, 4).Windows[0].Today?.UsedPercentagePoints == 4, "Same-day baseline and consumption survive a widget restart");
        var changedReset = reset + 3600;
        var refreshed = Read(1, 5, changedReset);
        Check(refreshed.Windows[0].Today is { UsedPercentagePoints: 0, CyclePercentagePoints: 0, RecentPercentagePoints: 0 },
            "Quota reset clears daily, recent and painted consumption together");
        Check(Read(4, 6, changedReset).Windows[0].Today is { UsedPercentagePoints: 3, CyclePercentagePoints: 3 },
            "New-cycle consumption accumulates without treating quota replenishment as damage");
        var old = tracker.Observe(Sample(99, start.AddMinutes(5), changedReset), "account-a:plus", start.AddMinutes(6));
        Check(old.Windows[0].Today is null && Read(4, 6, changedReset).Windows[0].Today?.UsedPercentagePoints == 3,
            "Out-of-order observations cannot contaminate the daily total");
        var tomorrow = start.AddMinutes(11);
        Check(tracker.Observe(Sample(4, start.AddMinutes(6), changedReset), "account-a:plus", tomorrow).Windows.All(w => w.Today is null),
            "Yesterday's cached snapshot is not attributed to the new local day");
        var newDay = tracker.Observe(Sample(8, tomorrow, changedReset), "account-a:plus", tomorrow);
        Check(newDay.Windows.All(w => w.Today?.UsedPercentagePoints == 0), "Daily total resets at midnight in the configured local timezone");
        var switched = tracker.Observe(Sample(80, tomorrow.AddMinutes(1)), "account-b:plus", tomorrow.AddMinutes(1));
        Check(switched.Windows.All(w => w.Today?.UsedPercentagePoints == 0), "Switching accounts establishes a separate baseline");
        var planChanged = tracker.Observe(Sample(90, tomorrow.AddMinutes(2)), "account-b:pro", tomorrow.AddMinutes(2));
        Check(planChanged.Windows.All(w => w.Today?.UsedPercentagePoints == 0), "Changing plan cannot compare percentages against a different allowance");
        var resizedQuota = Sample(95, tomorrow.AddMinutes(3)) with { Windows = [new("primary", "주간", 95, reset)] };
        Check(tracker.Observe(resizedQuota, "account-b:pro", tomorrow.AddMinutes(3)).Windows[0].Today?.UsedPercentagePoints == 0,
            "A changed quota duration establishes a fresh percentage basis");
        Check(!File.ReadAllText(Path.Combine(directory, "daily-usage.json")).Contains("account-b"), "Daily history stores a hashed identity rather than account identifiers");
    }

    private static void VerifyMeasurementSchedule()
    {
        var directory = Path.Combine(_output, "measurement-" + Guid.NewGuid().ToString("N"));
        var tracker = new DailyUsageTracker(directory, TimeZoneInfo.Utc);
        var start = DateTimeOffset.Parse("2026-10-13T09:00:00Z");
        ProviderUsage Sample(double used, DateTimeOffset time) => new("codex", "ready", "test", [new("primary", "주간", used, start.AddDays(7).ToUnixTimeSeconds())], time);
        ProviderUsage Read(double used, int minute, int window = 30) => tracker.Observe(Sample(used, start.AddMinutes(minute)), "measurement-account", start.AddMinutes(minute), TimeSpan.FromMinutes(30), window);
        Read(10, 0);
        Check(Read(13, 10).Windows[0].Today is { UsedPercentagePoints: 0, RecentPercentagePoints: 0 }, "Raw observations do not publish daily totals before the chosen measurement interval");
        Check(Read(15, 30).Windows[0].Today is { UsedPercentagePoints: 5, RecentPercentagePoints: 5 }, "Thirty-minute measurement publishes daily accumulation and rolling consumption together");
        var hourly = Read(17, 60);
        Check(hourly.Windows[0].Today is { UsedPercentagePoints: 7, RecentPercentagePoints: 2 }, "Rolling window excludes older observations while the daily total retains them");
        var changedWindow = tracker.Observe(Sample(17, start.AddMinutes(60)), "measurement-account", start.AddMinutes(65), TimeSpan.FromMinutes(30), 60).Windows[0].Today!;
        Check(changedWindow.RecentPercentagePoints == 7 && changedWindow.MeasuredAt == start.AddMinutes(60), "Comparison-window change reuses the last measured timestamp without advancing its schedule");
        tracker = new DailyUsageTracker(directory, TimeZoneInfo.Utc);
        Check(Read(18, 70, 60).Windows[0].Today?.UsedPercentagePoints == 7, "Restart retains pending raw observations and the measurement clock");
        var delayed = tracker.Observe(Sample(18, start.AddMinutes(70)), "measurement-account", start.AddMinutes(90), TimeSpan.FromMinutes(30), 60).Windows[0].Today!;
        Check(delayed.UsedPercentagePoints == 8 && delayed.SourceAt == start.AddMinutes(70) && delayed.MeasuredAt == start.AddMinutes(90), "Scheduled aggregation distinguishes measurement time from the source's receipt time");
        var stale = tracker.Observe(Sample(18, start.AddMinutes(70)), "measurement-account", start.AddMinutes(120), TimeSpan.FromMinutes(30), 60).Windows[0].Today!;
        Check(stale.MeasuredAt == delayed.MeasuredAt, "A stale source cannot masquerade as a fresh scheduled measurement");

        var daily = new DailyUsageTracker(Path.Combine(directory, "daily"), TimeZoneInfo.Utc);
        daily.Observe(Sample(10, start), "daily-account", start, TimeSpan.FromDays(1), 1440);
        daily.Observe(Sample(20, start.AddHours(14)), "daily-account", start.AddHours(14), TimeSpan.FromDays(1), 1440);
        var midnight = daily.Observe(Sample(21, start.AddHours(15).AddMinutes(10)), "daily-account", start.AddHours(15).AddMinutes(10), TimeSpan.FromDays(1), 1440).Windows[0].Today!;
        Check(!midnight.IsCurrentDay && midnight.NextMeasurementAt == start.AddDays(1), "Midnight resets the daily basis without postponing a one-day measurement indefinitely");
        var nextDay = daily.Observe(Sample(25, start.AddDays(1)), "daily-account", start.AddDays(1), TimeSpan.FromDays(1), 1440).Windows[0].Today!;
        Check(nextDay.IsCurrentDay && nextDay.UsedPercentagePoints == 4 && nextDay.RecentPercentagePoints == 14, "One-day comparison retains rolling history across midnight while today's total uses today's baseline");
        var reset = Sample(1, start.AddDays(1).AddMinutes(1)) with { Windows = [new("primary", "주간", 1, start.AddDays(8).ToUnixTimeSeconds())] };
        Check(daily.Observe(reset, "daily-account", reset.UpdatedAt!.Value, TimeSpan.FromDays(1), 1440).Windows[0].Today is
            { UsedPercentagePoints: 0, CyclePercentagePoints: 0, RecentPercentagePoints: 0, IsCurrentDay: true },
            "Quota replenishment immediately clears every consumption amount between measurements");
        Check(MeasurementPreferences.Read(directory) is { IntervalMinutes: 30, WindowMinutes: 30 }, "Fresh measurement settings retain the existing 30-minute default");
        WidgetStorage.Write(Path.Combine(directory, "measurement.json"), new MeasurementPreferences(1440, 720));
        Check(MeasurementPreferences.Read(directory) is { IntervalMinutes: 180, WindowMinutes: 720 }, "Legacy aggregation interval is clamped without resetting the user's comparison window");
        WidgetStorage.Write(Path.Combine(directory, "measurement.json"), new MeasurementPreferences(17, 1440));
        Check(MeasurementPreferences.Read(directory) is { IntervalMinutes: 17, WindowMinutes: 1440 }, "Custom minute aggregation and independent comparison window survive restore");
        var minuteTracker = new DailyUsageTracker(Path.Combine(directory, "minute"), TimeZoneInfo.Utc);
        minuteTracker.Observe(Sample(10, start), "minute-account", start, TimeSpan.FromMinutes(1));
        Check(minuteTracker.Observe(Sample(12, start.AddSeconds(59)), "minute-account", start.AddSeconds(59), TimeSpan.FromMinutes(1)).Windows[0].Today?.UsedPercentagePoints == 0,
            "One-minute aggregation holds pending observations before its boundary");
        Check(minuteTracker.Observe(Sample(13, start.AddMinutes(1)), "minute-account", start.AddMinutes(1), TimeSpan.FromMinutes(1)).Windows[0].Today?.UsedPercentagePoints == 3,
            "One-minute aggregation publishes accumulated changes at its boundary");
    }

    private static async Task VerifyAccountsAsync()
    {
        var profile = Path.Combine(_output, "oauth-protocol-" + Guid.NewGuid().ToString("N"));
        Check(DashboardPreferences.Read(profile, new()).ProviderIds.Length == 0, "Fresh dashboard has no providers selected");
        Check(DashboardPreferences.Read(profile, new(true, false, true, false)).ProviderIds.SequenceEqual(new[] { "codex" }), "Upgrade preserves only previously connected providers");
        WidgetStorage.Write(Path.Combine(profile, "dashboard.json"), new { providerIds = new[] { "claude", "claude", "unknown", "codex" } });
        Check(DashboardPreferences.Read(profile, new()).ProviderIds.SequenceEqual(new[] { "claude", "codex" }), "Saved provider list removes duplicates and unsupported entries while keeping order");
        Check(!AccountProfiles.ReadPreferences(profile).CodexEnabled && !AccountProfiles.ReadPreferences(profile).ClaudeEnabled,
            "Default connection preferences require an explicit user choice");
        foreach (var address in new[] { "http://auth.openai.com/", "https://auth.openai.com.evil.example/", "https://user:password@chatgpt.com/", "file:///C:/Windows" })
            Check(!AccountProfiles.IsOfficialCodexLoginUrl(address), "OAuth authorization host rejected: " + address.Split(':')[0]);
        Check(AccountProfiles.IsOfficialCodexLoginUrl("https://auth.openai.com/oauth/authorize") && AccountProfiles.IsOfficialCodexLoginUrl("https://chatgpt.com/oauth/authorize"), "Official Codex OAuth hosts allowed");
        using var codex = new CodexClient(profile, () =>
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "UsageWidget.Verification.exe"));
            info.ArgumentList.Add("--rpc-fixture");
            info.Environment["OPENAI_API_KEY"] = "test-inherited-key";
            return info;
        });
        var before = await codex.ReadAccountAsync(CancellationToken.None);
        Check(before.Status == "disconnected" && before.Source == "widget", "Isolated Codex starts unsigned-in");
        using (var env = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "test-environment.json"))))
            Check(env.RootElement.GetProperty("home").GetString() == profile && !env.RootElement.GetProperty("apiKeyInherited").GetBoolean(), "Codex child uses a separate CODEX_HOME without inherited API credentials");
        var url = await codex.BeginLoginAsync(CancellationToken.None);
        await codex.WaitForLoginAsync(CancellationToken.None);
        var account = await codex.ReadAccountAsync(CancellationToken.None);
        Check(AccountProfiles.IsOfficialCodexLoginUrl(url) && account.Email == "test@example.invalid", "OAuth matches its login ID and handles completion arriving before the start response");
        await codex.LogoutAsync(CancellationToken.None);
        Check((await codex.ReadAccountAsync(CancellationToken.None)).Status == "disconnected", "Widget OAuth logout clears the dedicated session");
        await codex.BeginLoginAsync(CancellationToken.None);
        await codex.CancelLoginAsync(CancellationToken.None);
        Check(true, "OAuth cancellation completes without affecting a system session");
        using var systemCodex = new CodexClient();
        try { await systemCodex.LogoutAsync(CancellationToken.None); throw new Exception("System logout was allowed."); }
        catch (InvalidOperationException) { Check(true, "Widget cannot log out the user's system Codex CLI"); }
        using var nativeCodex = new CodexClient(Path.Combine(profile, "NativeCodex"));
        Check((await nativeCodex.ReadAccountAsync(CancellationToken.None)).Status == "disconnected",
            "Actual Codex app-server confirms the fresh CODEX_HOME is not the system login");
        using var claude = new ClaudeAuthClient(Path.Combine(profile, "Claude"));
        var start = claude.StartInfo("auth", "login", "--claudeai");
        Check(start.Environment["CLAUDE_CONFIG_DIR"] == Path.Combine(profile, "Claude")
            && !start.Environment.ContainsKey("ANTHROPIC_API_KEY") && !start.Environment.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN")
            && start.ArgumentList.SequenceEqual(new[] { "auth", "login", "--claudeai" }), "Claude sign-in delegates to official CLI with an isolated profile");
        var claudeAccount = await claude.ReadAccountAsync(CancellationToken.None);
        Check(claudeAccount.Status == "disconnected", "Actual Claude CLI confirms the fresh widget profile is not the system login");
        using var systemClaude = new ClaudeAuthClient();
        try { await systemClaude.LogoutAsync(CancellationToken.None); throw new Exception("System logout was allowed."); }
        catch (InvalidOperationException) { Check(true, "Widget cannot log out the user's system Claude CLI"); }
    }

    private static async Task VerifyClaudeUsageControlAsync()
    {
        ProcessStartInfo Fixture(bool unsupported = false)
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "UsageWidget.Verification.exe"));
            info.ArgumentList.Add("--claude-usage-fixture");
            if (unsupported) info.ArgumentList.Add("--unsupported");
            return info;
        }
        var usage = await ClaudeUsageControl.ReadAsync(Fixture(), CancellationToken.None);
        Check(usage.Status == "ready" && usage.Windows.Select(w => w.RemainingPercent).SequenceEqual(new[] { 98d, 52d }), "Claude SDK control obtains both quota windows without a model message");
        Check(usage.Windows.All(w => w.ResetsAt == DateTimeOffset.Parse("2026-10-13T19:30:00+09:00").ToUnixTimeSeconds()), "Claude SDK ISO reset times retain their timezone and percentage basis");
        try { await ClaudeUsageControl.ReadAsync(Fixture(true), CancellationToken.None); Check(false, "Unsupported Claude control must fail"); }
        catch (IOException) { Check(true, "Older Claude CLI control error does not fabricate usage"); }
        foreach (var json in new[] { "{\"rate_limits_available\":false,\"rate_limits\":null}", "{\"rate_limits_available\":true,\"rate_limits\":{\"five_hour\":{\"utilization\":null},\"seven_day\":{\"utilization\":-1}}}" })
        {
            using var doc = JsonDocument.Parse(json);
            var missing = UsageParser.ClaudeControl(doc.RootElement);
            Check(missing.Status == "unavailable" && missing.Windows.Count == 0 && missing.UpdatedAt is null, "Absent Claude control quotas do not invent availability or freshness");
        }
        var folder = Path.Combine(_output, "control-snapshot");
        var bridge = new ClaudeBridge(folder, Path.Combine(folder, "settings.json"));
        bridge.SaveUsage(usage);
        using var snapshot = JsonDocument.Parse(File.ReadAllText(bridge.SnapshotFile));
        Check(snapshot.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "rate_limits", "source", "updatedAt" }), "Claude control persistence contains only quota fields, source and receipt time");
        Check(UsageParser.Claude(snapshot.RootElement).Windows.Select(w => w.RemainingPercent).SequenceEqual(new[] { 98d, 52d }), "Direct Claude quota snapshot remains compatible with statusLine reads");
    }

    private static int RunClaudeUsageFixture(bool unsupported)
    {
        var args = Environment.GetCommandLineArgs();
        string Option(string key, string fallback) => Array.IndexOf(args, key) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        var fiveHour = int.Parse(Option("--fixture-five-hour", "2"));
        var weekly = int.Parse(Option("--fixture-weekly", "48"));
        var reset = Option("--fixture-reset", "2026-10-13T19:30:00+09:00");
        var initialized = false;
        while (Console.ReadLine() is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "control_request") return 12;
            var id = root.GetProperty("request_id").GetString();
            var subtype = root.GetProperty("request").GetProperty("subtype").GetString();
            if (subtype == "initialize") initialized = true;
            else if (subtype != "get_usage" || !initialized) return 13;
            // Unrelated notifications/responses must not be mistaken for ours.
            Console.WriteLine("{\"type\":\"system\",\"subtype\":\"status\"}");
            Console.WriteLine("{\"type\":\"control_response\",\"response\":{\"request_id\":\"unrelated\",\"subtype\":\"success\",\"response\":{}}}");
            if (unsupported && subtype == "get_usage")
                Console.WriteLine(JsonSerializer.Serialize(new { type = "control_response", response = new { request_id = id, subtype = "error", error = "unsupported" } }));
            else
            {
                object result = subtype == "initialize" ? new { } : new { rate_limits_available = true, rate_limits = new {
                    five_hour = new { utilization = fiveHour, resets_at = reset },
                    seven_day = new { utilization = weekly, resets_at = reset } }, session = new { total_cost_usd = 0 } };
                Console.WriteLine(JsonSerializer.Serialize(new { type = "control_response", response = new { request_id = id, subtype = "success", response = result } }));
            }
        }
        return 0;
    }

    private static int RunClaudeAccountFixture()
    {
        var args = Environment.GetCommandLineArgs();
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!;
        Directory.CreateDirectory(home);
        var login = Path.Combine(home, "fixture-login");
        File.WriteAllText(Path.Combine(home, "fixture-environment.json"), JsonSerializer.Serialize(new
        {
            home, apiKeyInherited = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            oauthTokenInherited = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"))
        }));
        var auth = Array.IndexOf(args, "auth");
        if (auth < 0) return RunClaudeUsageFixture(false);
        var action = args[auth + 1];
        if (action == "login") { File.WriteAllText(login, "signed-in"); return 0; }
        if (action == "logout") { if (File.Exists(login)) File.Delete(login); return 0; }
        if (action != "status") return 12;
        var emailIndex = Array.IndexOf(args, "--fixture-email");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            loggedIn = File.Exists(login), authMethod = "claude.ai", email = args[emailIndex + 1],
            subscriptionType = "max", configDirectory = home
        }));
        return 0;
    }

    private static int RunRpcFixture()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME")!;
        var loginFile = Path.Combine(home, "fixture-login");
        bool signedIn = File.Exists(loginFile);
        var args = Environment.GetCommandLineArgs();
        string Option(string name, string fallback) => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        var email = Option("--fixture-email", "test@example.invalid");
        var used = int.Parse(Option("--fixture-used", "8"));
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var id)) continue;
            var number = id.GetInt32();
            var method = root.GetProperty("method").GetString();
            object result = new { };
            if (method == "initialize")
            {
                Directory.CreateDirectory(home);
                File.WriteAllText(Path.Combine(home, "test-environment.json"), JsonSerializer.Serialize(new { home, apiKeyInherited = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")) }));
            }
            if (method == "account/read") result = new { account = signedIn ? new { type = "chatgpt", email, planType = "prolite" } : null };
            if (method == "account/rateLimits/read")
            {
                if (File.Exists(Path.Combine(home, "fixture-fail-usage")))
                { Console.WriteLine(JsonSerializer.Serialize(new { id = number, error = new { code = -1, message = "fixture error" } })); continue; }
                result = new { rateLimits = new { primary = new { usedPercent = used, windowDurationMins = 10080, resetsAt = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() }, planType = "prolite" } };
            }
            if (method == "account/login/start")
            {
                signedIn = true;
                File.WriteAllText(loginFile, "signed-in");
                result = new { loginId = "test-login", authUrl = "https://auth.openai.com/oauth/authorize" };
                Console.WriteLine(JsonSerializer.Serialize(new { method = "account/login/completed", @params = new { loginId = "unrelated-login", success = false } }));
                Console.WriteLine(JsonSerializer.Serialize(new { method = "account/login/completed", @params = new { loginId = "test-login", success = true } }));
            }
            if (method is "account/logout" or "account/login/cancel") { signedIn = false; if (File.Exists(loginFile)) File.Delete(loginFile); }
            Console.WriteLine(JsonSerializer.Serialize(new { id = number, result }));
        }
        return 0;
    }

    private static async Task<string> InvokeBridgeAsync(ClaudeBridge bridge, string payload)
    {
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Path.GetDirectoryName(bridge.SnapshotFile)!, "claude-statusline.ps1") }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Check(process.ExitCode == 0 && string.IsNullOrWhiteSpace(await errors), "Bridge PowerShell exits cleanly");
        return await output;
    }
    private static Task<string> StartLogoSamplingAsync() => ScriptAsync("window.logoSamples=[]; window.logoSampling=true; function sampleLogo(){if(!window.logoSampling)return; let visible=0;let peakOpacity=0; for(const svg of document.querySelectorAll('.triangle')){let opacity=1;let shown=true;for(let node=svg;node;node=node.parentElement){const style=getComputedStyle(node);opacity*=Number(style.opacity);if(style.visibility==='hidden'||style.display==='none')shown=false;}if(shown&&opacity>.01){visible++;peakOpacity=Math.max(peakOpacity,opacity);}} const mark=document.querySelector('.transition-mark .triangle');const match=mark?.style.transform.match(/rotate\\(([-\\d.]+)deg\\)/); window.logoSamples.push({visible,peakOpacity,angle:mark?Number(match?.[1]??0):null,viewportWidth:innerWidth,viewportHeight:innerHeight});requestAnimationFrame(sampleLogo);} requestAnimationFrame(sampleLogo)");
    private static async Task<string> ScriptAsync(string script) => await _window!.WebView!.ExecuteScriptAsync(script);
    private static async Task ExpandAccountAsync(string id)
    {
        await ScriptAsync($"document.querySelector('.account-box[data-account-id=\"{id}\"] .account-summary[aria-expanded=\"false\"]')?.click()");
        await Task.Delay(150);
    }
    private static async Task AssertScriptAsync(string script, string message) => Check(await ScriptAsync(script) == "true", message);
    private static async Task CaptureAsync(string name)
    {
        using var file = File.Create(Path.Combine(_output, name));
        await _window!.WebView!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file).WaitAsync(TimeSpan.FromSeconds(15));
    }
    private static async Task WaitAsync(Func<bool> predicate, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException(message); await Task.Delay(80); }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Passed.Add(message); Console.WriteLine("PASS: " + message);
    }
}
