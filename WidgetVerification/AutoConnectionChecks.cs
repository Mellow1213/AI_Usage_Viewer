using System.Diagnostics;
using System.IO;
using UsageWidget;

internal static partial class Program
{
    private static async Task VerifyAutoConnectionAsync()
    {
        _window?.Close();
        var directory = Path.Combine(_output, "auto-connection-" + Guid.NewGuid().ToString("N"));
        var registry = AiAccountPreferences.Read(directory, new(CodexEnabled: true, ClaudeEnabled: true));
        registry = registry.Add("codex", out var unsigned);
        WidgetStorage.Write(Path.Combine(directory, "account-profiles.json"), registry);
        WidgetStorage.Write(Path.Combine(directory, "dashboard.json"), new DashboardPreferences(["codex", "claude", unsigned.Id]));
        var codexHome = AccountProfiles.CodexHome(directory);
        var claudeHome = AccountProfiles.ClaudeHome(directory);
        foreach (var home in new[] { codexHome, claudeHome })
        {
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "fixture-login"), "signed-in");
        }
        new ClaudeBridge(Path.Combine(claudeHome, "UsageBridge"), Path.Combine(claudeHome, "settings.json")).Install();
        var accountGate = Path.Combine(codexHome, "fixture-hold-account");
        var usageGate = Path.Combine(codexHome, "fixture-hold-usage");
        File.WriteAllText(accountGate, "wait"); File.WriteAllText(usageGate, "wait");
        ProcessStartInfo Fixture(string mode)
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "UsageWidget.Verification.exe"));
            foreach (var arg in new[] { mode, "--fixture-email", "saved@example.invalid" }) info.ArgumentList.Add(arg);
            return info;
        }
        CodexClient CodexFactory(CodexProfile profile) => new(AccountProfiles.CodexHome(directory, profile.Id), () => Fixture("--rpc-fixture"));
        ClaudeAuthClient ClaudeFactory(AiAccountProfile profile) => new(AccountProfiles.ClaudeHome(directory, profile.Id), arguments =>
        {
            var info = Fixture("--claude-account-fixture");
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
            return info;
        });
        _window = new MainWindow(directory, testMode: true, codexFactory: CodexFactory, claudeFactory: ClaudeFactory);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Auto-connection UI ready");
        await Task.Delay(2300);
        await AssertScriptAsync("['codex','claude'].every(id=>{const card=document.querySelector('.provider[data-account-id=\"'+id+'\"]');return card.querySelector('.status-tag').textContent==='자동 연결 중' && card.textContent.includes('자동 연결 중입니다…') && !card.querySelector('.connect')})",
            "Saved Codex and Claude accounts start connecting before any refresh and stay so across timer ticks");
        await AssertScriptAsync($"document.querySelector('.provider[data-account-id=\"{unsigned.Id}\"] .connect')!==null",
            "A new account without a saved connection still offers login");
        await CaptureAsync("auto-connection-cards.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()"); await Task.Delay(200);
        await ScriptAsync("document.querySelector('[aria-label=\"계정 관리\"]').click()"); await Task.Delay(200);
        await ExpandAccountAsync("codex");
        await AssertScriptAsync("(()=>{const box=document.querySelector('.account-box[data-account-id=\"codex\"]');return box.querySelector('.account-status').textContent==='자동 연결 중' && box.textContent.includes('자동 연결 중입니다…') && !box.textContent.includes('로그인이 필요합니다') && !box.querySelector('.account-primary')})()",
            "Account settings distinguish automatic connection from a missing login and hide login actions while connecting");
        await CaptureAsync("auto-connection-settings.png");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click()"); await Task.Delay(250);
        await ScriptAsync("document.querySelector('[aria-label=\"간략히 보기\"]').click()"); await Task.Delay(950);
        await AssertScriptAsync("document.querySelector('.compact>[data-account-id=\"codex\"] .compact-label').textContent==='자동 연결 중' && document.documentElement.scrollWidth<=innerWidth",
            "Compact cards show automatic connection without overflowing");
        await ScriptAsync("document.querySelector('[aria-label=\"자세히 보기\"]').click()"); await Task.Delay(950);
        var refresh = _window.RefreshAsync(true);
        await WaitAsync(() => _window.Claude.Read().Status == "ready", "Independent Claude connection completes");
        await Task.Delay(200);
        await AssertScriptAsync("!document.querySelector('.provider[data-account-id=\"codex\"] .connect') && document.querySelector('.provider[data-account-id=\"claude\"] .status-tag').textContent==='연결됨'",
            "Each completed account is published while another account is still connecting");
        File.Delete(accountGate);
        await WaitAsync(() => _window.CodexUsage.Status == "loading", "Account confirmed, usage pending");
        await Task.Delay(2300);
        await AssertScriptAsync("document.querySelector('.provider[data-account-id=\"codex\"] .status-tag').textContent==='연결 중' && !document.querySelector('.provider[data-account-id=\"codex\"] .connect')",
            "Confirmed authentication awaiting usage never flashes a login prompt");
        File.Delete(usageGate); await refresh; await Task.Delay(200);
        Check(_window.CodexUsage.Status == "ready", "Automatic connection completes with quota data");
        await ScriptAsync("window.chrome.webview.postMessage({action:'ready'})"); await Task.Delay(200);
        Check(_window.CodexUsage.Status == "ready", "A repeated UI handshake preserves the connected quota instead of resetting it to login required");
        File.WriteAllText(accountGate, "wait");
        refresh = _window.RefreshAsync(); await Task.Delay(200);
        Check(_window.CodexUsage.Status == "ready" && _window.CodexUsage.Windows.Count > 0,
            "Routine refresh preserves a connected account and its existing quota");
        File.Delete(accountGate); await refresh;
        File.Delete(Path.Combine(codexHome, "fixture-login"));
        await _window.RefreshAsync(); await Task.Delay(200);
        await AssertScriptAsync("document.querySelector('.provider[data-account-id=\"codex\"] .status-tag').textContent==='로그인 필요' && document.querySelector('.provider[data-account-id=\"codex\"] .connect')!==null",
            "A confirmed missing CLI login becomes disconnected after the check completes");
        var failure = Path.Combine(codexHome, "fixture-fail-account"); File.WriteAllText(failure, "fail");
        await _window.RefreshAsync(); await Task.Delay(2300);
        Check(_window.CodexUsage.Status == "error", "A failed connection leaves pending state and stays an error across snapshot ticks");
        File.Delete(failure); File.WriteAllText(Path.Combine(codexHome, "fixture-login"), "signed-in");
        File.WriteAllText(accountGate, "wait"); refresh = _window.RefreshAsync();
        Check(_window.CodexUsage.Status == "connecting", "Retrying a failed connection returns to automatic connection");
        File.Delete(accountGate); await refresh;
        Check(_window.CodexUsage.Status == "ready", "A successful retry resolves the pending state");
        await _window.DisconnectAsync("codex");
        Check(_window.CodexUsage.Status == "disconnected", "Explicit logout stays disconnected");
        _window.Close(); _window = null;
    }
}
