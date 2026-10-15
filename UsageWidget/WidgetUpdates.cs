using System.IO;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace UsageWidget;

internal sealed record WidgetUpdateState(string Phase, string CurrentVersion, string Message,
    string? LatestVersion = null, string? ReleaseNotes = null, int Progress = 0,
    DateTimeOffset? LastCheckedAt = null, bool CanCheck = true, bool CanDownload = false,
    bool CanApply = false, string RetryAction = "check");

internal sealed record WidgetUpdateRelease(string Version, string Notes, object Package);

internal interface IWidgetUpdateBackend
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    WidgetUpdateRelease? Pending { get; }
    Task<WidgetUpdateRelease?> CheckAsync(CancellationToken token);
    Task DownloadAsync(WidgetUpdateRelease release, Action<int> progress, CancellationToken token);
    void QueueRestart(WidgetUpdateRelease release);
}

internal sealed class WidgetUpdater : IDisposable
{
    private readonly IWidgetUpdateBackend _backend;
    private readonly string _stateFile;
    private readonly CancellationTokenSource _lifetime = new();
    private WidgetUpdateRelease? _available, _pending;
    private bool _busy, _disposed;
    internal WidgetUpdateState State { get; private set; }
    internal event Action? Changed;

    internal WidgetUpdater(string directory, IWidgetUpdateBackend? backend = null)
    {
        _backend = backend ?? new VelopackUpdateBackend(directory);
        _stateFile = Path.Combine(directory, "update-state.json");
        DateTimeOffset? lastCheck = null;
        try
        {
            if (File.Exists(_stateFile)) lastCheck = JsonSerializer.Deserialize<CheckHistory>(File.ReadAllText(_stateFile), WidgetStorage.Json)?.LastCheckedAt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        _pending = _backend.Pending;
        State = new(_backend.IsInstalled ? "idle" : "unavailable", _backend.CurrentVersion,
            _backend.IsInstalled ? "새 버전을 확인할 수 있습니다." : "설치된 앱에서 업데이트를 사용할 수 있습니다.",
            LastCheckedAt: lastCheck, CanCheck: _backend.IsInstalled);
        if (_pending is not null) SetReady();
    }

    internal async Task CheckAsync()
    {
        if (_disposed || _busy || !_backend.IsInstalled) return;
        // A downloaded package is kept until the user explicitly applies it.
        if (_pending is not null) { SetReady(); return; }
        _busy = true;
        Set(State with { Phase = "checking", Message = "새 버전을 확인하고 있습니다.", CanCheck = false, CanDownload = false, CanApply = false });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var release = await _backend.CheckAsync(timeout.Token);
            if (_disposed) return;
            _available = release;
            var checkedAt = DateTimeOffset.UtcNow;
            try { WidgetStorage.Write(_stateFile, new CheckHistory(checkedAt)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WidgetStorage.Log("Update check time save failed", ex); }
            Set(State with { Phase = release is null ? "upToDate" : "available", LatestVersion = release?.Version,
                ReleaseNotes = release?.Notes, LastCheckedAt = checkedAt, CanCheck = true, CanDownload = release is not null,
                CanApply = false, Progress = 0, RetryAction = "check",
                Message = release is null ? "최신 버전을 사용하고 있습니다." : "새 버전을 다운로드할 수 있습니다." });
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { Fail("check", "업데이트를 확인하지 못했습니다. 연결 상태를 확인하고 다시 시도해주세요.", ex); }
        finally { _busy = false; }
    }

    internal async Task DownloadAsync()
    {
        if (_disposed || _busy || _available is null || _pending is not null) return;
        _busy = true;
        Set(State with { Phase = "downloading", Message = "업데이트를 다운로드하고 검증하고 있습니다.",
            Progress = 0, CanCheck = false, CanDownload = false, CanApply = false });
        var progress = new Progress<int>(value =>
        {
            value = Math.Clamp(value, 0, 100);
            if (!_disposed && State.Phase == "downloading" && State.Progress != value) Set(State with { Progress = value });
        });
        try
        {
            await _backend.DownloadAsync(_available, ((IProgress<int>)progress).Report, _lifetime.Token);
            if (_disposed) return;
            _pending = _available;
            SetReady();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { Fail("download", "다운로드 또는 파일 검증에 실패했습니다. 다시 시도해주세요.", ex); }
        finally { _busy = false; }
    }

    internal bool PrepareRestart()
    {
        if (_disposed || _busy || _pending is null) return false;
        _busy = true;
        Set(State with { Phase = "applying", Message = "앱을 종료한 뒤 업데이트를 적용합니다.", CanCheck = false, CanDownload = false, CanApply = false });
        try { _backend.QueueRestart(_pending); return true; }
        catch (Exception ex)
        {
            _busy = false;
            Fail("apply", "업데이트 적용을 시작하지 못했습니다. 다시 시도해주세요.", ex);
            return false;
        }
    }

    private void SetReady() => Set(State with { Phase = "ready", LatestVersion = _pending!.Version,
        ReleaseNotes = _pending.Notes, Progress = 100, CanCheck = false, CanDownload = false, CanApply = true,
        RetryAction = "apply", Message = "다운로드가 완료됐습니다. 재시작하면 업데이트가 적용됩니다." });

    private void Fail(string operation, string message, Exception exception)
    {
        if (_disposed) return;
        WidgetStorage.Log("Application update " + operation + " failed", exception);
        Set(State with { Phase = "error", Message = message, RetryAction = operation, CanCheck = true,
            CanDownload = operation == "download" && _available is not null, CanApply = _pending is not null });
    }

    private void Set(WidgetUpdateState state) { State = state; Changed?.Invoke(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
    private sealed record CheckHistory(DateTimeOffset LastCheckedAt);
}

internal sealed class VelopackUpdateBackend : IWidgetUpdateBackend
{
    internal const string Repository = "https://github.com/Mellow1213/AI_Usage_Viewer";
    internal const string PackageId = "Mellow.AIUsageViewer";
    private readonly UpdateManager _manager;
    private readonly string _expectedPackageId;
    private readonly Exception? _configurationError;
    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString()
        ?? typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.1.0";
    public WidgetUpdateRelease? Pending => _manager.UpdatePendingRestart is { } pending ? ToRelease(pending, pending) : null;

    internal VelopackUpdateBackend(string directory, UpdateManager? manager = null, string packageId = PackageId)
    {
        _expectedPackageId = packageId;
        if (manager is not null) { _manager = manager; return; }
        // Public installations use GitHub Releases without shipping credentials.
        var config = Path.Combine(directory, "update-source.json");
        string? source = null;
        try
        {
            if (File.Exists(config)) source = JsonSerializer.Deserialize<UpdateSourceSettings>(File.ReadAllText(config), WidgetStorage.Json)?.Source;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { _configurationError = ex; }
        try
        {
            _manager = new UpdateManager(CreateSource(source));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            _configurationError = ex;
            _manager = new UpdateManager(new GithubSource(Repository, null, false));
        }
    }

    internal static IUpdateSource CreateSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return new GithubSource(Repository, null, false);
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != "https") throw new InvalidDataException("Update sources require HTTPS.");
            var address = uri.AbsoluteUri.TrimEnd('/');
            if (address.Equals(Repository, StringComparison.OrdinalIgnoreCase) ||
                address.Equals(Repository + "/releases/latest/download", StringComparison.OrdinalIgnoreCase))
                return new GithubSource(Repository, null, false);
            return new SimpleWebSource(source);
        }
        return new SimpleFileSource(new DirectoryInfo(Path.GetFullPath(Environment.ExpandEnvironmentVariables(source))));
    }

    public async Task<WidgetUpdateRelease?> CheckAsync(CancellationToken token)
    {
        if (_configurationError is not null) throw new InvalidDataException("Invalid update source configuration.", _configurationError);
        var update = await _manager.CheckForUpdatesAsync().WaitAsync(token);
        if (update is null) return null;
        if (update.TargetFullRelease.PackageId != _expectedPackageId) throw new InvalidDataException("Unexpected update package.");
        return ToRelease(update.TargetFullRelease, update);
    }

    public Task DownloadAsync(WidgetUpdateRelease release, Action<int> progress, CancellationToken token) =>
        _manager.DownloadUpdatesAsync((UpdateInfo)release.Package, progress, token);

    public void QueueRestart(WidgetUpdateRelease release)
    {
        var asset = release.Package is UpdateInfo info ? info.TargetFullRelease : (VelopackAsset)release.Package;
        _manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true,
            restartArgs: Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    private static WidgetUpdateRelease ToRelease(VelopackAsset asset, object package) =>
        new(asset.Version.ToString(), asset.NotesMarkdown ?? "", package);
    private sealed record UpdateSourceSettings(string Source);
}
