using System.IO;
using UsageWidget;
using Velopack.Sources;

internal static partial class Program
{
    private static async Task VerifyUpdatesAsync()
    {
        Check(VelopackUpdateBackend.CreateSource(null) is GithubSource { Prerelease: false }, "Stable installations use public GitHub Releases without beta updates or credentials");
        Check(VelopackUpdateBackend.IncludesPrereleases("1.2.1-beta.1") && !VelopackUpdateBackend.IncludesPrereleases("1.2.1"),
            "Only prerelease app versions opt into beta updates");
        Check(VelopackUpdateBackend.CreateSource(null, true) is GithubSource { Prerelease: true } &&
            VelopackUpdateBackend.CreateSource(VelopackUpdateBackend.Repository + "/releases/latest/download", true) is GithubSource { Prerelease: true },
            "Beta installations include prereleases even with the legacy migration URL");
        Check(VelopackUpdateBackend.CreateSource(VelopackUpdateBackend.Repository) is GithubSource &&
            VelopackUpdateBackend.CreateSource(VelopackUpdateBackend.Repository + "/releases/latest/download/") is GithubSource,
            "Repository and migration URLs both use the GitHub update provider");
        Check(VelopackUpdateBackend.CreateSource("https://example.invalid/feed") is SimpleWebSource &&
            VelopackUpdateBackend.CreateSource(Path.Combine(_output, "local-feed")) is SimpleFileSource,
            "Explicit HTTPS and local test feeds remain supported");
        var insecureRejected = false;
        try { VelopackUpdateBackend.CreateSource("http://example.invalid/feed"); }
        catch (InvalidDataException) { insecureRejected = true; }
        Check(insecureRejected, "Unencrypted HTTP update feeds are rejected");
        foreach (var invalidConfiguration in new[] { "{broken-json", "{\"source\":\"http://example.invalid/feed\"}" })
        {
            var invalidProfile = Path.Combine(_output, "invalid-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(invalidProfile);
            File.WriteAllText(Path.Combine(invalidProfile, "update-source.json"), invalidConfiguration);
            var invalidBackend = new VelopackUpdateBackend(invalidProfile);
            var rejected = false;
            try { await invalidBackend.CheckAsync(CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Invalid update configuration fails at check time without crashing app construction");
        }
        var profile = Path.Combine(_output, "profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var sentinel = Path.Combine(profile, "connections.json");
        var original = "{\"claudeEnabled\":true,\"useSystemClaude\":false}";
        File.WriteAllText(sentinel, original);
        var backend = new UpdateFixtureBackend();
        using (var updater = new WidgetUpdater(profile, backend))
        {
            await updater.CheckAsync();
            Check(updater.State.Phase == "upToDate" && updater.State.LastCheckedAt is not null, "No newer release reports current version and records a successful check");
            backend.NextCheckError = new IOException("offline");
            await updater.CheckAsync();
            Check(updater.State is { Phase: "error", RetryAction: "check", CanCheck: true, CanApply: false }, "Offline update check remains retryable without an install action");
            backend.Release = new("1.2.0", "새 기능\n<script>must stay text</script>", new object());
            var checkGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.CheckGate = checkGate.Task;
            var first = updater.CheckAsync();
            var calls = backend.CheckCalls;
            await updater.CheckAsync();
            Check(backend.CheckCalls == calls && updater.State.Phase == "checking", "Concurrent update checks are ignored while one check is running");
            checkGate.SetResult();
            await first;
            backend.CheckGate = null;
            Check(updater.State is { Phase: "available", LatestVersion: "1.2.0", CanDownload: true, CanApply: false }, "A newer release requires an explicit download before it can be applied");
            Check(!updater.PrepareRestart() && backend.ApplyCalls == 0, "Applying an update is blocked until package verification has completed");
            backend.NextDownloadError = new InvalidDataException("checksum mismatch");
            await updater.DownloadAsync();
            Check(updater.State is { Phase: "error", RetryAction: "download", CanDownload: true, CanApply: false }, "A failed download or checksum never enables restart and can be retried");
            await updater.DownloadAsync();
            Check(updater.State is { Phase: "ready", Progress: 100, CanDownload: false, CanApply: true }, "A verified download waits for the user's restart action");
            calls = backend.CheckCalls;
            await updater.CheckAsync();
            Check(backend.CheckCalls == calls && updater.State.Phase == "ready", "Checking again preserves a prepared update without replacing or applying it");
            backend.NextApplyError = new IOException("updater launch failed");
            Check(!updater.PrepareRestart() && updater.State is { Phase: "error", RetryAction: "apply", CanApply: true }, "Failure to launch the updater keeps the app open and the prepared update retryable");
            Check(updater.PrepareRestart() && backend.ApplyCalls == 2 && updater.State.Phase == "applying", "Explicit restart queues application of the prepared release");
        }
        using (var restored = new WidgetUpdater(profile, backend))
            Check(restored.State is { Phase: "ready", LatestVersion: "1.2.0", CanApply: true } && restored.State.LastCheckedAt is not null,
                "Restart restores a downloaded package and successful check history without auto-applying it");
        Check(File.ReadAllText(sentinel) == original, "Update checks and downloads leave account connection preferences byte-for-byte unchanged");
        using (var development = new WidgetUpdater(profile, new UpdateFixtureBackend { IsInstalled = false }))
            Check(development.State is { Phase: "unavailable", CanCheck: false }, "A development build clearly reports that installation is required for updates");
        var cancelledBackend = new UpdateFixtureBackend { CheckGate = new TaskCompletionSource().Task };
        var cancelled = new WidgetUpdater(profile, cancelledBackend);
        var pendingCheck = cancelled.CheckAsync();
        cancelled.Dispose();
        await pendingCheck.WaitAsync(TimeSpan.FromSeconds(2));
        Check(cancelled.State.Phase == "checking", "Closing the app cancels an in-flight check without publishing a late error");

        // Exercise the native message boundary and the actual React settings panel.
        var uiProfile = Path.Combine(_output, "ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uiProfile);
        var settings = Path.Combine(uiProfile, "claude-settings.json");
        File.WriteAllText(settings, "{}");
        backend = new UpdateFixtureBackend { Release = new("1.2.0", "업데이트 검증\n<script>must stay text</script>", new object()) };
        var uiUpdater = new WidgetUpdater(uiProfile, backend);
        _window = new MainWindow(uiProfile, testMode: true, claudeSettings: settings, updates: uiUpdater, checkUpdatesOnStartup: true);
        _window.Show();
        await WaitAsync(() => _window.Ready && uiUpdater.State.Phase == "available", "Startup update check");
        Check(backend.CheckCalls == 1, "Startup checks asynchronously after the native page is ready");
        await ScriptAsync("window.chrome.webview.postMessage({action:'ready'})");
        await Task.Delay(150);
        Check(backend.CheckCalls == 1, "A repeated ready handshake does not start another startup update check");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(800);
        await AssertScriptAsync("document.querySelector('.menu').getBoundingClientRect().bottom < innerHeight", "Settings still fits after adding the app update entry");
        await ScriptAsync("document.querySelector('[aria-label=\"앱 업데이트 설정\"]').click()");
        await Task.Delay(800);
        await AssertScriptAsync("document.querySelector('.update-versions').textContent.includes('1.2.0') && document.querySelector('[aria-label=\"다운로드 및 업데이트\"]') && !document.querySelector('[aria-label=\"재시작하여 적용\"]')", "Update settings show versions and the correct action before download");
        await AssertScriptAsync("document.querySelector('.update-notes pre').textContent.includes('<script>') && !document.querySelector('.update-notes script')", "Release notes are rendered as plain text rather than executable HTML");
        await CaptureAsync("updates-available.png");
        backend.NextDownloadError = new InvalidDataException("bad download");
        await ScriptAsync("document.querySelector('[aria-label=\"다운로드 및 업데이트\"]').click()");
        await WaitAsync(() => uiUpdater.State.Phase == "error", "Download failure UI");
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('.update-message').textContent.includes('파일 검증') && document.querySelector('[aria-label=\"다운로드 및 업데이트\"]').textContent.includes('다시 시도')", "The UI exposes a download retry after verification failure");
        await ScriptAsync("document.querySelector('[aria-label=\"다운로드 및 업데이트\"]').click()");
        await WaitAsync(() => uiUpdater.State.Phase == "ready", "Downloaded update UI");
        await Task.Delay(150);
        await AssertScriptAsync("document.querySelector('[aria-label=\"재시작하여 적용\"]') && !document.querySelector('[aria-label=\"다운로드 및 업데이트\"]')", "Only an explicit restart action is offered after a successful download");
        await CaptureAsync("updates-ready.png");
        await Task.Delay(1200);
        Check(backend.CheckCalls == 1 && backend.ApplyCalls == 0, "Keeping the app open does not poll for releases or auto-apply a downloaded update");
        await ScriptAsync("document.querySelector('[aria-label=\"재시작하여 적용\"]').click()");
        await WaitAsync(() => !_window.IsVisible, "Restart queues and closes gracefully");
        Check(backend.ApplyCalls == 1 && File.Exists(Path.Combine(uiProfile, "window-state.json")), "Restart passes through normal window shutdown and saves the window state");
        _window = null;
    }

    private sealed class UpdateFixtureBackend : IWidgetUpdateBackend
    {
        public bool IsInstalled { get; init; } = true;
        public string CurrentVersion => "1.1.0";
        public WidgetUpdateRelease? Pending { get; private set; }
        internal WidgetUpdateRelease? Release { get; set; }
        internal Task? CheckGate { get; set; }
        internal Exception? NextCheckError, NextDownloadError, NextApplyError;
        internal int CheckCalls, DownloadCalls, ApplyCalls;
        public async Task<WidgetUpdateRelease?> CheckAsync(CancellationToken token)
        {
            CheckCalls++;
            if (CheckGate is not null) await CheckGate.WaitAsync(token);
            if (NextCheckError is { } error) { NextCheckError = null; throw error; }
            return Release;
        }
        public Task DownloadAsync(WidgetUpdateRelease release, Action<int> progress, CancellationToken token)
        {
            DownloadCalls++; progress(50);
            if (NextDownloadError is { } error) { NextDownloadError = null; throw error; }
            progress(100); Pending = release; return Task.CompletedTask;
        }
        public void QueueRestart(WidgetUpdateRelease release)
        {
            ApplyCalls++;
            if (NextApplyError is { } error) { NextApplyError = null; throw error; }
        }
    }
}
