using System.IO;
using UsageWidget;

internal static partial class Program
{
    private static async Task VerifySettingsNavigationAsync()
    {
        _window?.Close();
        var profile = Path.Combine(_output, "navigation-" + Guid.NewGuid().ToString("N"));
        WidgetStorage.Write(Path.Combine(profile, "dashboard.json"), new DashboardPreferences(["codex", "claude"]));
        var settings = Path.Combine(profile, "claude-settings.json");
        File.WriteAllText(settings, "{}");
        _window = new MainWindow(profile, testMode: true, claudeSettings: settings);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Settings navigation ready");
        var now = DateTimeOffset.UtcNow;
        _window.SetFixture(new ProviderUsage("codex", "ready", "fixture", [new("primary", "주간", 20, now.AddDays(2).ToUnixTimeSeconds())], now),
            new ProviderUsage("claude", "ready", "fixture", [new("seven_day", "주간", 30, now.AddDays(2).ToUnixTimeSeconds())], now));
        await Task.Delay(650);

        async Task Click(string selector)
        {
            await ScriptAsync($"document.querySelector({System.Text.Json.JsonSerializer.Serialize(selector)}).click()");
            await Task.Delay(250);
        }
        async Task Route(string expected, string description)
        {
            await AssertScriptAsync($"window.aiUsageCaptureUI().settingsPath.join('/')==='{expected}'", description);
            await AssertScriptAsync("!document.querySelector('.settings-panel') || (document.activeElement.id==='settings-title' && document.querySelector('.settings-breadcrumbs [aria-current=page]').textContent===document.getElementById('settings-title').textContent)", "Navigation focuses the common page heading and marks the current breadcrumb");
        }

        await Click("[aria-label='설정']");
        await Route("settings", "Settings home records one visited level");
        await Click("[aria-label='사용량으로 돌아가기']");
        await Route("", "Back from settings home returns to the main card");

        foreach (var label in new[] { "앱 업데이트 설정", "계정 관리", "사용 기록", "사용량 측정", "화면 설정" })
        {
            await Click("[aria-label='설정']");
            await Click($".settings-link[aria-label='{label}']");
            await AssertScriptAsync("window.aiUsageCaptureUI().settingsPath.length===2 && !!document.querySelector('[aria-label=\"설정으로 돌아가기\"]')", $"{label} uses the same child depth and Back rule");
            await Click("[aria-label='설정으로 돌아가기']");
            await Route("settings", $"{label} returns to its settings entry");
            await Click("[aria-label='설정 닫기']");
        }

        await Click("[aria-label='관측 측정 설정']");
        await Route("measurement", "The measurement shortcut has no unvisited settings parent");
        await Click("[aria-label='그래프 보기']");
        await Route("measurement/trends", "A shortcut's graph retains measurement as its actual parent");
        await Task.Delay(450);
        await AssertScriptAsync("document.documentElement.scrollWidth<=innerWidth && document.querySelector('.settings-panel').getBoundingClientRect().bottom<=innerHeight+2", "The graph and its route fit the native card without clipping");
        await CaptureAsync("shortcut-graph-route.png");
        await Click("[aria-label='사용량 측정으로 돌아가기']");
        await Click("[aria-label='사용량으로 돌아가기']");
        await Route("", "Two Back actions unwind the shortcut to the main card");

        await Click("[aria-label='설정']");
        await Click("[aria-label='사용량 측정']");
        await Click("[aria-label='그래프 보기']");
        await Route("settings/measurement/trends", "The same graph retains settings when opened from settings home");
        await Task.Delay(450);
        await CaptureAsync("settings-graph-route.png");
        await Click(".settings-breadcrumbs li:nth-child(2) button");
        await Route("settings", "Selecting a breadcrumb removes all deeper navigation levels");
        await Click("[aria-label='계정 관리']");
        await Click("[aria-label='AI 계정 추가']");
        await Route("settings/accounts/providers", "Account creation follows the settings account-management route");
        await Click("[aria-label='계정 관리로 돌아가기']");
        await Route("settings/accounts", "Canceling account creation returns to the account list");
        await Click("[aria-label='AI 계정 추가']");
        await Click("[aria-label='Codex 추가']");
        await Route("settings/accounts", "Completing account creation removes the picker without duplicating account management");
        await Click("[aria-label='설정으로 돌아가기']");
        await Click("[aria-label='설정 닫기']");

        await Click("[aria-label='AI 추가']");
        await Route("providers", "The main add shortcut opens only the picker");
        await Click("[aria-label='사용량으로 돌아가기']");
        await Route("", "Canceling the main add shortcut returns directly to the main card");
        await Click("[aria-label='AI 추가']");
        await Click("[aria-label='Codex 추가']");
        await Route("accounts", "Completing the main add shortcut replaces the picker with account management");
        await Click("[aria-label='사용량으로 돌아가기']");
        await Route("", "Back after shortcut account creation does not reopen the picker");

        await Click("[aria-label='간략히 보기']");
        await Task.Delay(700);
        await Click("[aria-label='설정']");
        await Click("[aria-label='사용량 측정']");
        await Click("[aria-label='그래프 보기']");
        await Click("[aria-label='설정 닫기']");
        await Route("", "Close clears all settings levels even from a nested graph");
        await AssertScriptAsync("!!document.querySelector('.compact') && !document.querySelector('.settings-panel')", "Close preserves the compact main-card mode");
        await Click("[aria-label='설정']");
        await Route("settings", "Reopening settings starts at its home instead of a stale nested page");
        await Task.Delay(450);
        await CaptureAsync("settings-navigation-home.png");
        await Click(".settings-breadcrumbs li:first-child button");
        await Route("", "The usage breadcrumb closes the settings route");
    }
}
