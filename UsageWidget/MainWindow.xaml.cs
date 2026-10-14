using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace UsageWidget;

public partial class MainWindow : Window
{
    private const string WidgetUrl = "https://widget.local/index.html";
    private readonly string _widgetUrl;
    private readonly string _dataDirectory;
    private readonly DailyUsageTracker _daily;
    private ClaudeBridge _claude;
    private CodexAccountPreferences _codexProfiles;
    private readonly Dictionary<string, CodexSession> _codexSessions;
    private readonly Func<CodexProfile, CodexClient>? _codexFactory;
    private readonly Action<string> _openCodexLogin;
    private ClaudeAuthClient _claudeAuth;
    private ConnectionPreferences _connections;
    private DashboardPreferences _dashboard;
    private MeasurementPreferences _measurement;
    private AccountConnection _codexAccount { get => _codexSessions["codex"].Account; set => _codexSessions["codex"].Account = value; }
    private AccountConnection _claudeAccount = new("claude", "widget", "disconnected");
    private CancellationTokenSource? _loginCancellation;
    private string? _loginProvider;
    private string? _codexLoginUrl;
    private bool AuthBusy => _loginProvider is not null;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool _testMode;
    private ProviderUsage _codexUsage { get => _codexSessions["codex"].Usage; set => _codexSessions["codex"].Usage = value; }
    private ProviderUsage _claudeUsage = new("claude", "loading", "연결 상태를 확인하고 있습니다.", []);
    private DateTimeOffset _lastCodexAttempt;
    private DateTimeOffset _lastClaudeAttempt;
    private bool _busy;
    private bool _ready;
    private WidgetViewMode _viewMode;
    private WidgetViewMode _lastCardView = WidgetViewMode.Expanded;
    private NativeRect? _moveStart;
    private bool _closed;
    private readonly Stopwatch _resizeClock = new();
    private const double ResizeDurationMs = 380;
    private TimeSpan _lastResizeRenderTime = TimeSpan.MinValue;
    private double _resizeDpiX = 1, _resizeDpiY = 1;
    private NativeRect _lastResizeBounds;
    private bool _browserSizeFrozen;
    internal List<ResizeFrameSample> ResizeFrames { get; } = [];
    private double _resizeFromWidth, _resizeFromHeight, _resizeWidth, _resizeHeight;
    private int _resizeFromX, _resizeFromY, _resizeTargetX, _resizeTargetY;
    private bool _resizeAnchorRight, _resizeAnchorBottom;
    private WidgetAnchorCorner _anchorCorner = WidgetAnchorCorner.TopRight;
    private int _viewRevision;
    private int _logoRotation, _logoRotationStep = 120;
    private bool _reducedMotion;
    private bool _animateTransitions = true;
    private bool _applyingResize;
    private string _transitionPhase = "settled";
    private WidgetViewMode _transitionFrom;
    private double _expandedHeight = 460, _compactHeight = 160;
    internal bool IsResizing => _resizeClock.IsRunning;
    private string? _notice;
    private readonly HashSet<ulong> _blockedNavigations = [];
    internal CoreWebView2? WebView => Browser.CoreWebView2;
    internal bool Ready => _ready;
    internal ClaudeBridge Claude => _claude;
    internal ProviderUsage CodexUsage => _codexUsage;
    internal WidgetViewMode ViewMode => _viewMode;
    internal WidgetAnchorCorner AnchorCorner => _anchorCorner;
    internal string WidgetAddress => _widgetUrl;

    public MainWindow() : this(GetDataDirectory()) { }
    internal MainWindow(string dataDirectory, bool testMode = false, string? claudeSettings = null, Func<CodexProfile, CodexClient>? codexFactory = null, Action<string>? openCodexLogin = null)
    {
        InitializeComponent();
        _dataDirectory = dataDirectory;
        _daily = new(dataDirectory);
        _testMode = testMode;
        _codexFactory = codexFactory;
        _openCodexLogin = openCodexLogin ?? AccountProfiles.OpenCodexLogin;
        // A new UI build must not reuse a cached index page from an older release.
        _widgetUrl = WidgetUrl + "?v=" + File.GetLastWriteTimeUtc(Path.Combine(AppContext.BaseDirectory, "Web", "index.html")).Ticks;
        _connections = AccountProfiles.ReadPreferences(dataDirectory);
        _codexProfiles = CodexAccountPreferences.Read(dataDirectory, _connections);
        _codexSessions = _codexProfiles.Profiles.ToDictionary(profile => profile.Id, profile => new CodexSession(dataDirectory, profile, _codexFactory));
        _dashboard = DashboardPreferences.Read(dataDirectory, _connections);
        _measurement = MeasurementPreferences.Read(dataDirectory);
        _claudeAuth = new(_connections.UseSystemClaude ? null : AccountProfiles.ClaudeHome(dataDirectory));
        _claude = claudeSettings is not null ? new(dataDirectory, claudeSettings) : CreateClaudeBridge(_connections.UseSystemClaude);
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 19, 22);
        SourceInitialized += (_, _) =>
        {
            var round = 2;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProc);
            RestorePosition();
        };
        SizeChanged += (_, _) => Surface.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 22, 22);
        Loaded += async (_, _) => await InitializeAsync();
        _timer.Tick += async (_, _) =>
        {
            if (_closed || !_ready) return;
            if (!AuthBusy && _dashboard.ProviderIds.Contains("claude")) _claudeUsage = _claudeAccount.Status == "connected" ? ReadClaudeSnapshot() : NotConnected("claude");
            Publish();
            if (!_testMode && _dashboard.ProviderIds.Length > 0 && !_busy && !AuthBusy && DateTimeOffset.UtcNow - _lastCodexAttempt >= TimeSpan.FromSeconds(60)) await RefreshAsync();
        };
        Closing += (_, _) =>
        {
            _closed = true;
            StopResize();
            _timer.Stop();
            _lifetime.Cancel();
            foreach (var session in _codexSessions.Values) session.Dispose();
            _claudeAuth.Dispose();
            SavePosition();
            Browser.Dispose();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { ToggleLogo(); e.Handled = true; }
            if (e.Key == Key.F5) { _ = RefreshAsync(true); e.Handled = true; }
        };
    }

    internal static string GetDataDirectory()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--data-dir");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : WidgetStorage.DataDirectory;
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_dataDirectory, "WebView"));
            if (_closed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var web = WebView!;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDevToolsEnabled = _testMode;
            web.Settings.AreHostObjectsAllowed = false;
            web.Settings.IsStatusBarEnabled = false;
            web.Settings.IsZoomControlEnabled = false;
            web.Settings.AreBrowserAcceleratorKeysEnabled = false;
            web.Settings.IsGeneralAutofillEnabled = false;
            web.Settings.IsPasswordAutosaveEnabled = false;
            // Let WebView2 route caption dragging directly to the native host window.
            // WPF's mouse device does not track input received by WebView2's child HWND.
            web.Settings.IsNonClientRegionSupportEnabled = true;
            web.SetVirtualHostNameToFolderMapping("widget.local", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.DenyCors);
            web.NavigationStarting += (_, e) =>
            {
                if (e.Uri != _widgetUrl) { _blockedNavigations.Add(e.NavigationId); e.Cancel = true; }
            };
            web.NewWindowRequested += (_, e) => e.Handled = true;
            web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.WebMessageReceived += WebMessageReceived;
            web.ProcessFailed += (_, _) => ShowError("화면 엔진이 종료됐습니다. 앱을 다시 실행해주세요.");
            web.NavigationCompleted += (_, e) =>
            {
                if (_blockedNavigations.Remove(e.NavigationId) || _closed) return;
                if (!e.IsSuccess) ShowError("카드 화면을 불러오지 못했습니다. 배포 폴더 전체가 있는지 확인해주세요.");
            };
            if (_testMode)
                await web.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", "{\"features\":[{\"name\":\"prefers-reduced-motion\",\"value\":\"no-preference\"}]}");
            web.Navigate(_widgetUrl);
        }
        catch (Exception ex)
        {
            WidgetStorage.Log("WebView initialization failed", ex);
            ShowError("WebView2 초기화에 실패했습니다. Edge WebView2 Runtime 설치 상태를 확인해주세요.");
        }
    }

    private async void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || e.Source != _widgetUrl) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("action", out var property) || property.ValueKind != JsonValueKind.String) return;
            switch (property.GetString())
            {
                case "ready":
                    _ready = true;
                    Startup.Visibility = Visibility.Collapsed;
                    Browser.Visibility = Visibility.Visible;
                    _codexUsage = NotConnected("codex");
                    _claudeUsage = NotConnected("claude");
                    Publish();
                    _timer.Start();
                    if (!_testMode && _dashboard.ProviderIds.Length > 0) await RefreshAsync();
                    break;
                case "refresh": await RefreshAsync(true); break;
                case "setMeasurementInterval":
                case "setMeasurementWindow":
                    if (root.TryGetProperty("minutes", out var intervalValue) && intervalValue.TryGetInt32(out var minutes)
                        && (property.GetString() == "setMeasurementWindow"
                            ? MeasurementPreferences.AllowedWindowMinutes.Contains(minutes) : MeasurementPreferences.ValidInterval(minutes)))
                    {
                        var preference = property.GetString() == "setMeasurementWindow" ? _measurement with { WindowMinutes = minutes }
                            : _measurement with { IntervalMinutes = minutes };
                        WidgetStorage.Write(Path.Combine(_dataDirectory, "measurement.json"), preference);
                        _measurement = preference;
                        Publish();
                    }
                    break;
                case "addProvider":
                    if (root.TryGetProperty("provider", out var added) && added.ValueKind == JsonValueKind.String)
                    {
                        var id = added.GetString()!;
                        if (id == "codex" && _dashboard.ProviderIds.Contains("codex")) await AddCodexAccountAsync();
                        else await SetProviderVisibilityAsync(id, true);
                    }
                    break;
                case "addCodexAccount": await AddCodexAccountAsync(); break;
                case "showProvider":
                    if (root.TryGetProperty("provider", out var shown) && shown.ValueKind == JsonValueKind.String)
                        await SetProviderVisibilityAsync(shown.GetString()!, true);
                    break;
                case "removeProvider":
                    if (root.TryGetProperty("provider", out var removed) && removed.ValueKind == JsonValueKind.String)
                        await SetProviderVisibilityAsync(removed.GetString()!, false);
                    break;
                case "pin": Topmost = !Topmost; SavePosition(); Publish(); break;
                case "toggleMotion":
                    _animateTransitions = !_animateTransitions;
                    _reducedMotion = !_animateTransitions;
                    if (!_animateTransitions && IsResizing) { StopResize(); ApplyResize(1); }
                    if (!_animateTransitions && _transitionPhase != "settled")
                    {
                        _transitionPhase = "settled";
                        ResizeTo(_viewMode == WidgetViewMode.Logo ? 48 : 304, TargetHeight());
                    }
                    SavePosition(); Publish(); break;
                case "viewHidden":
                    if (_transitionPhase == "hiding" && root.TryGetProperty("viewRevision", out var hiddenRevision)
                        && hiddenRevision.TryGetInt32(out var hiddenValue) && hiddenValue == _viewRevision)
                    {
                        _transitionPhase = "resizing";
                        Publish();
                        ResizeTo(_viewMode == WidgetViewMode.Logo ? 48 : 304, TargetHeight());
                    }
                    break;
                case "collapse": SetViewMode(WidgetViewMode.Logo); break;
                case "expand": SetViewMode(_viewMode == WidgetViewMode.Logo ? _lastCardView : WidgetViewMode.Expanded); break;
                case "toggleLogo": ToggleLogo(); break;
                case "toggleDetails": SetViewMode(_viewMode == WidgetViewMode.Expanded ? WidgetViewMode.Compact : WidgetViewMode.Expanded); break;
                case "showFull": SetViewMode(WidgetViewMode.Expanded); break;
                case "dock":
                    var corner = WidgetAnchorCorner.TopRight;
                    if (root.TryGetProperty("corner", out var requestedCorner))
                    {
                        if (!requestedCorner.TryGetInt32(out var cornerNumber) || cornerNumber is < 0 or > 3) break;
                        corner = (WidgetAnchorCorner)cornerNumber;
                    }
                    if (IsResizing) { StopResize(); ApplyResize(1); }
                    DockCorner(corner); SavePosition(); Publish(); break;
                case "close": Close(); break;
                case "height":
                    // Ignore measurements from a view that is still fading out.
                    if (!root.TryGetProperty("viewMode", out var measuredMode) || !measuredMode.TryGetInt32(out var modeNumber) || modeNumber != (int)_viewMode
                        || !root.TryGetProperty("viewRevision", out var revision) || !revision.TryGetInt32(out var measuredRevision) || measuredRevision != _viewRevision) break;
                    _reducedMotion = root.TryGetProperty("reducedMotion", out var reduced) && reduced.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("height", out var height) && height.TryGetDouble(out var number) && double.IsFinite(number))
                    {
                        var next = _viewMode == WidgetViewMode.Logo ? 48 : Math.Clamp(number + 2, 140, MaximumCardHeight());
                        if (_viewMode == WidgetViewMode.Expanded) _expandedHeight = next;
                        if (_viewMode == WidgetViewMode.Compact) _compactHeight = next;
                        if (_transitionPhase == "hiding") break;
                        ResizeTo(_viewMode == WidgetViewMode.Logo ? 48 : 304, next);
                    }
                    break;
                case "connectClaude":
                    await SelectExistingAsync("claude");
                    break;
                case "disconnectClaude":
                    await DisconnectAsync("claude");
                    break;
                case "loginCodex": await LoginAsync(CodexTarget(root)); break;
                case "loginClaude": await LoginAsync("claude"); break;
                case "useExistingCodex": await SelectExistingAsync(CodexTarget(root)); break;
                case "useExistingClaude": await SelectExistingAsync("claude"); break;
                case "logoutCodex": await DisconnectAsync(CodexTarget(root)); break;
                case "logoutClaude": await DisconnectAsync("claude"); break;
                case "cancelLogin": _loginCancellation?.Cancel(); break;
                case "reopenLogin": if (_codexLoginUrl is not null) _openCodexLogin(_codexLoginUrl); break;
                case "launchClaudeCode":
                    if (_claudeAccount.Status != "connected") throw new InvalidOperationException("Claude is not connected.");
                    var folder = new Microsoft.Win32.OpenFolderDialog { Title = "이 계정으로 작업할 프로젝트 폴더 선택" };
                    if (folder.ShowDialog(this) == true) _claudeAuth.LaunchCode(folder.FolderName);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or TimeoutException)
        {
            WidgetStorage.Log("Widget action failed", ex);
            _notice = "설정을 적용하지 못했습니다. 파일 권한이나 Claude 설정 형식을 확인해주세요.";
            Publish();
        }
    }

    internal async Task RefreshAsync(bool forceClaude = false)
    {
        if (_busy || _closed || AuthBusy) return;
        _busy = true;
        _lastCodexAttempt = DateTimeOffset.UtcNow;
        Publish();
        try
        {
            await ReadAccountsAsync();
            // Limit concurrent CLI readers, while one failed account cannot block another card.
            using var readers = new SemaphoreSlim(3);
            var codexTasks = _codexSessions.Values.Where(session => _dashboard.ProviderIds.Contains(session.Profile.Id))
                .Select(async session =>
                {
                    await readers.WaitAsync(_lifetime.Token);
                    try { await RefreshCodexAsync(session); }
                    finally { readers.Release(); }
                }).ToArray();
            var claudeTask = _dashboard.ProviderIds.Contains("claude") && _claudeAccount.Status == "connected"
                ? ReadClaudeUsageAsync(forceClaude) : Task.FromResult(NotConnected("claude"));
            await Task.WhenAll(codexTasks.Append(claudeTask));
            _claudeUsage = await claudeTask;
        }
        catch (OperationCanceledException) when (_closed) { return; }
        catch (Exception ex)
        {
            WidgetStorage.Log("Usage refresh failed", ex);
            _notice = "사용량 갱신을 완료하지 못했습니다. 연결 상태를 확인해주세요.";
        }
        finally
        {
            _busy = false;
            if (!_closed) Publish();
        }
    }

    private ClaudeBridge CreateClaudeBridge(bool system) => system
        ? new(Path.Combine(_dataDirectory, "SystemClaudeBridge"))
        : new(Path.Combine(AccountProfiles.ClaudeHome(_dataDirectory), "UsageBridge"), Path.Combine(AccountProfiles.ClaudeHome(_dataDirectory), "settings.json"));

    private ProviderUsage ReadClaudeSnapshot()
    {
        var snapshot = _claude.Read();
        // Retain a useful query error while the fallback has no data yet.
        return snapshot.Status != "ready" && _claudeUsage.Status == "error" ? _claudeUsage : snapshot;
    }

    private async Task<ProviderUsage> ReadClaudeUsageAsync(bool force = false)
    {
        if (_testMode || !force && DateTimeOffset.UtcNow - _lastClaudeAttempt < TimeSpan.FromMinutes(5)) return ReadClaudeSnapshot();
        _lastClaudeAttempt = DateTimeOffset.UtcNow;
        try
        {
            var usage = await _claudeAuth.ReadUsageAsync(_lifetime.Token);
            _claude.SaveUsage(usage);
            return usage;
        }
        catch (OperationCanceledException) when (_closed) { throw; }
        catch (Exception ex)
        {
            WidgetStorage.Log("Claude usage query failed", ex);
            var cached = _claude.Read();
            // Older CLIs can still supply documented statusLine snapshots.
            return cached.Status == "ready" ? cached : new("claude", "error", "자동 조회 실패 · Claude Code 업데이트와 로그인을 확인해주세요. Claude Code 응답 수신도 사용할 수 있습니다.", []);
        }
    }

    private void SaveConnections()
    {
        var primary = _codexSessions["codex"].Profile;
        _connections = _connections with { CodexEnabled = primary.Enabled, UseSystemCodex = primary.UseSystem };
        WidgetStorage.Write(Path.Combine(_dataDirectory, "codex-accounts.json"), _codexProfiles);
        WidgetStorage.Write(Path.Combine(_dataDirectory, "connections.json"), _connections);
    }

    private string CodexTarget(JsonElement message)
    {
        var id = message.TryGetProperty("accountId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "codex";
        if (id is null || !_codexSessions.ContainsKey(id)) throw new InvalidOperationException("Unknown Codex account.");
        return id;
    }

    private void UpdateCodexProfile(CodexSession session, bool enabled, bool system)
    {
        session.Profile = session.Profile with { Enabled = enabled, UseSystem = system };
        _codexProfiles = _codexProfiles.Update(session.Profile);
    }

    private async Task RefreshCodexAsync(CodexSession session)
    {
        var id = session.Profile.Id;
        if (!session.Profile.Enabled) { session.Usage = NotConnected(id); return; }
        try
        {
            var account = (await session.Client.ReadAccountAsync(_lifetime.Token)) with { Id = id };
            if (!string.Equals(account.Email, session.Account.Email, StringComparison.OrdinalIgnoreCase)) session.Usage = NotConnected(id);
            session.Account = account;
            session.Usage = account.Status == "connected" ? (await session.Client.ReadAsync(_lifetime.Token)) with { Id = id } : NotConnected(id);
        }
        catch (OperationCanceledException) when (_closed) { throw; }
        catch (Exception ex)
        {
            WidgetStorage.Log("Codex account usage read failed", ex);
            if (session.Account.Status != "connected") session.Account = new(id, session.Profile.UseSystem ? "system" : "widget", "error");
            session.Usage = session.Usage with { Status = "error", Message = ex is FileNotFoundException
                ? "Codex CLI를 설치한 뒤 로그인해주세요." : "Codex 조회 실패 · CLI 로그인과 네트워크를 확인해주세요." };
        }
    }
    private ProviderUsage NotConnected(string provider) => new(provider, "disconnected",
        CodexAccountPreferences.IsCodexId(provider) ? "ChatGPT 계정으로 로그인해주세요." : "Claude Code 계정으로 로그인해주세요.", []);

    private async Task ReadAccountsAsync()
    {
        if (_connections.ClaudeEnabled && _dashboard.ProviderIds.Contains("claude"))
        {
            try
            {
                _claudeAccount = await _claudeAuth.ReadAccountAsync(_lifetime.Token);
                if (_claudeAccount.Status == "connected") BindClaudeSnapshot();
            }
            catch (OperationCanceledException) when (_closed) { throw; }
            catch (Exception ex)
            {
                WidgetStorage.Log("Claude account check failed", ex);
                _claudeAccount = new("claude", _connections.UseSystemClaude ? "system" : "widget", "error", Message: "Claude Code 설치와 로그인 상태를 확인해주세요.");
            }
        }
    }

    private void ClearClaudeSnapshot()
    {
        if (File.Exists(_claude.SnapshotFile)) File.Delete(_claude.SnapshotFile);
    }
    private void BindClaudeSnapshot()
    {
        var folder = Path.GetDirectoryName(_claude.SnapshotFile)!;
        var identityFile = Path.Combine(folder, "usage-account.json");
        string? previous = null;
        if (File.Exists(identityFile))
        {
            try { using var doc = JsonDocument.Parse(File.ReadAllText(identityFile)); previous = doc.RootElement.GetProperty("email").GetString(); }
            catch (JsonException) { }
        }
        if (!string.Equals(previous, _claudeAccount.Email, StringComparison.OrdinalIgnoreCase) || !File.Exists(identityFile))
        {
            ClearClaudeSnapshot();
            _lastClaudeAttempt = default;
        }
        WidgetStorage.Write(identityFile, new { email = _claudeAccount.Email });
    }

    internal async Task SelectExistingAsync(string provider)
    {
        if (_busy || AuthBusy || _closed) return;
        if (_codexSessions.TryGetValue(provider, out var codex))
        {
            if (provider != "codex") throw new InvalidOperationException("Only the original slot may follow the system CLI.");
            UpdateCodexProfile(codex, true, true);
            codex.RestartClient();
            codex.Account = new(provider, "system", "checking");
            codex.Usage = NotConnected(provider);
        }
        else if (provider == "claude")
        {
            if (_claude.Installed) _claude.Uninstall();
            _claudeAuth.Dispose(); _claudeAuth = new();
            _claude = CreateClaudeBridge(true);
            _connections = _connections with { UseSystemClaude = true, ClaudeEnabled = true };
            _claudeAccount = new("claude", "system", "checking");
            ClearClaudeSnapshot();
            _claudeUsage = NotConnected("claude");
        }
        else throw new InvalidOperationException("Unknown provider.");
        SaveConnections();
        await RefreshAsync(true);
        if (provider == "claude" && _claudeAccount.Status == "connected")
        {
            _claude.Install();
            _claudeUsage = _claude.Read();
        }
        _notice = "기존 CLI 계정 사용을 선택했습니다. CLI 계정이 바뀌면 카드도 따라갑니다.";
        Publish();
    }

    private async Task LoginAsync(string provider)
    {
        if (_busy || AuthBusy || _closed) return;
        _loginProvider = provider;
        _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _loginCancellation.Token;
        _notice = "공식 로그인 화면에서 직접 로그인해주세요. 5분 안에 완료하거나 취소할 수 있습니다.";
        try
        {
            if (_codexSessions.TryGetValue(provider, out var codex))
            {
                UpdateCodexProfile(codex, true, false);
                codex.RestartClient();
                codex.Account = new(provider, "widget", "signingIn");
                codex.Usage = new(provider, "signingIn", "브라우저에서 ChatGPT 로그인을 완료해주세요.", []);
                SaveConnections(); Publish();
                await codex.Client.LogoutAsync(token);
                _codexLoginUrl = await codex.Client.BeginLoginAsync(token);
                _openCodexLogin(_codexLoginUrl);
                await codex.Client.WaitForLoginAsync(token);
                codex.Account = (await codex.Client.ReadAccountAsync(token)) with { Id = provider };
                if (codex.Account.Status != "connected") throw new IOException("Codex login is incomplete.");
                codex.Usage = (await codex.Client.ReadAsync(token)) with { Id = provider };
            }
            else if (provider == "claude")
            {
                if (_claude.Installed) _claude.Uninstall();
                _claudeAuth.Dispose(); _claudeAuth = new(AccountProfiles.ClaudeHome(_dataDirectory));
                _claude = CreateClaudeBridge(false);
                _connections = _connections with { UseSystemClaude = false, ClaudeEnabled = true };
                _claudeAccount = new("claude", "widget", "signingIn");
                _claudeUsage = new("claude", "signingIn", "공식 Claude Code 로그인 창에서 완료해주세요.", []);
                ClearClaudeSnapshot(); SaveConnections(); Publish();
                await _claudeAuth.LogoutAsync(token);
                await _claudeAuth.LoginAsync(token);
                _claudeAccount = await _claudeAuth.ReadAccountAsync(token);
                if (_claudeAccount.Status != "connected") throw new IOException("Claude login is incomplete.");
                _claude.Install(); BindClaudeSnapshot();
                _claudeUsage = new("claude", "loading", "사용량을 조회하고 있습니다.", []);
                Publish();
                _claudeUsage = await ReadClaudeUsageAsync(true);
            }
            else throw new InvalidOperationException("Unknown provider.");
            _notice = "위젯 전용 계정으로 로그인했습니다.";
        }
        catch (Exception ex)
        {
            if (_closed) return;
            if (_codexSessions.TryGetValue(provider, out var codex))
            {
                try { await codex.Client.CancelLoginAsync(_lifetime.Token); } catch (Exception error) { WidgetStorage.Log("Login cancellation failed", error); }
                codex.Account = new(provider, "widget", "disconnected");
                codex.Usage = NotConnected(provider);
            }
            else { _claudeAuth.CancelLogin(); _claudeAccount = new("claude", "widget", "disconnected"); _claudeUsage = NotConnected("claude"); }
            WidgetStorage.Log("Account login ended", ex);
            _notice = ex is OperationCanceledException ? "로그인을 취소했습니다." : "로그인이 완료되지 않았습니다. CLI 설치와 공식 로그인 창을 확인해주세요.";
        }
        finally
        {
            _codexLoginUrl = null; _loginProvider = null;
            _loginCancellation.Dispose(); _loginCancellation = null;
            if (!_closed) Publish();
        }
    }

    internal async Task DisconnectAsync(string provider)
    {
        if (_busy || AuthBusy || _closed) return;
        _busy = true; Publish();
        try
        {
            if (_codexSessions.TryGetValue(provider, out var codex))
            {
                // Disconnect just this slot. Other profile directories and readers are untouched.
                if (!codex.Profile.UseSystem && codex.Profile.Enabled) await codex.Client.LogoutAsync(_lifetime.Token);
                UpdateCodexProfile(codex, false, false);
                codex.RestartClient();
                codex.Account = new(provider, "widget", "disconnected"); codex.Usage = NotConnected(provider);
            }
            else if (provider == "claude")
            {
                if (!_connections.UseSystemClaude) await _claudeAuth.LogoutAsync(_lifetime.Token);
                if (_claude.Installed) _claude.Uninstall();
                ClearClaudeSnapshot();
                _claudeAuth.Dispose(); _claudeAuth = new(AccountProfiles.ClaudeHome(_dataDirectory));
                _claude = CreateClaudeBridge(false);
                _connections = _connections with { UseSystemClaude = false, ClaudeEnabled = false };
                _claudeAccount = new("claude", "widget", "disconnected"); _claudeUsage = NotConnected("claude");
            }
            else throw new InvalidOperationException("Unknown provider.");
            SaveConnections(); _notice = "위젯 연결을 해제했습니다. 기존 CLI의 로그인은 유지됩니다.";
        }
        finally { _busy = false; if (!_closed) Publish(); }
    }

    private void Publish()
    {
        if (!_ready || _closed || WebView is null) return;
        string? Identity(AccountConnection account, ProviderUsage usage) => _testMode ? "verification" : account.Status == "connected" && account.Email is not null
            ? $"{account.Source}:{account.Email.ToLowerInvariant()}:{usage.Plan ?? account.Plan}" : null;
        var now = DateTimeOffset.UtcNow;
        // UI fixtures update immediately; interval behavior is verified with explicit ledger clocks.
        var interval = _testMode ? TimeSpan.Zero : TimeSpan.FromMinutes(_measurement.IntervalMinutes);
        var codex = _codexSessions.Values.ToDictionary(session => session.Profile.Id, session =>
        {
            var usage = session.Usage with { Id = session.Profile.Id, Name = session.Profile.Name };
            return _dashboard.ProviderIds.Contains(session.Profile.Id)
                ? _daily.Observe(usage, Identity(session.Account, usage), now, interval, _measurement.WindowMinutes) : usage;
        });
        var claude = _dashboard.ProviderIds.Contains("claude") ? _daily.Observe(_claudeUsage, Identity(_claudeAccount, _claudeUsage), now, interval, _measurement.WindowMinutes) : _claudeUsage;
        WebView.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            busy = _busy, pinned = Topmost, collapsed = _viewMode != WidgetViewMode.Expanded, viewMode = (int)_viewMode, viewRevision = _viewRevision,
            bridgeInstalled = _claude.Installed, notice = _notice,
            authBusy = AuthBusy, loginProvider = _loginProvider,
            animateTransitions = _animateTransitions,
            transitionPhase = _transitionPhase, transitionFrom = (int)_transitionFrom,
            logoRotation = _logoRotation, logoRotationStep = _logoRotationStep,
            anchorCorner = (int)_anchorCorner,
            lastCardView = (int)_lastCardView,
            measurementIntervalMinutes = _measurement.IntervalMinutes,
            measurementWindowMinutes = _measurement.WindowMinutes,
            maxCardHeight = MaximumCardHeight(),
            accounts = _codexSessions.Values.Select(session => session.Account with { Name = session.Profile.Name }).Append(_claudeAccount).ToArray(),
            providers = _dashboard.ProviderIds.Select(id => id == "claude" ? claude : codex[id]).ToArray()
        }, WidgetStorage.Json));
    }

    internal void SetFixture(ProviderUsage codex, ProviderUsage claude, bool busy = false)
    {
        if (!_testMode) throw new InvalidOperationException("Fixtures are only available in verification mode.");
        _timer.Stop();
        _codexUsage = codex; _claudeUsage = claude; _busy = busy;
        // Fixtures represent account state too, without changing persisted login preferences.
        _codexAccount = new("codex", "widget", codex.Status is "disconnected" or "signingIn" ? "disconnected" : "connected");
        _claudeAccount = new("claude", "widget", claude.Status is "disconnected" or "signingIn" ? "disconnected" : "connected");
        Publish();
    }

    internal void SetCodexFixture(string id, ProviderUsage usage, string? email = null)
    {
        if (!_testMode || !_codexSessions.TryGetValue(id, out var session)) throw new InvalidOperationException("Unknown test account.");
        session.Usage = usage with { Id = id };
        session.Account = new(id, "widget", usage.Status == "disconnected" ? "disconnected" : "connected", Email: email);
        Publish();
    }

    internal async Task<string?> AddCodexAccountAsync()
    {
        if (_busy || AuthBusy || _closed) return null;
        var updated = _codexProfiles.Add(out var profile);
        WidgetStorage.Write(Path.Combine(_dataDirectory, "codex-accounts.json"), updated);
        _codexProfiles = updated;
        _codexSessions.Add(profile.Id, new(_dataDirectory, profile, _codexFactory));
        await SetProviderVisibilityAsync(profile.Id, true);
        _notice = $"{profile.Name} 카드를 추가했습니다. 계정 연결에서 ChatGPT로 로그인해주세요.";
        Publish();
        return profile.Id;
    }

    private async Task SetProviderVisibilityAsync(string id, bool visible)
    {
        if (!DashboardPreferences.Supported(id) || id != "claude" && !_codexSessions.ContainsKey(id) || AuthBusy || _busy) return;
        var ids = _dashboard.ProviderIds.ToList();
        if (visible ? ids.Contains(id) : !ids.Contains(id)) return;
        if (visible) ids.Add(id); else ids.Remove(id);
        _dashboard = new(ids.ToArray());
        // Persist selection immediately, even before login. LocalApplicationData
        // keeps this user's dashboard independent of the executable/update folder.
        WidgetStorage.Write(Path.Combine(_dataDirectory, "dashboard.json"), _dashboard);
        if (!visible && _codexSessions.TryGetValue(id, out var session))
        {
            // Stop the owned reader while preserving official CLI credentials.
            session.RestartClient();
        }
        Publish();
        if (visible && !_testMode && (id == "claude" ? _connections.ClaudeEnabled : _codexSessions[id].Profile.Enabled)) await RefreshAsync();
    }

    private double MaximumCardHeight()
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var handle = new WindowInteropHelper(this).Handle;
        return GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)
            ? Math.Clamp((info.Work.Bottom - info.Work.Top) / VisualTreeHelper.GetDpi(this).DpiScaleY - 32, 140, 880) : 880;
    }
    internal void ShowError(string message)
    {
        if (_viewMode == WidgetViewMode.Logo) SetViewMode(WidgetViewMode.Expanded);
        Browser.Visibility = Visibility.Hidden;
        Startup.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        ExitButton.Visibility = Visibility.Visible;
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void RestorePosition()
    {
        DockRight();
        try
        {
            var path = Path.Combine(_dataDirectory, "window-state.json");
            if (!File.Exists(path)) return;
            var state = JsonSerializer.Deserialize<SavedWindow>(File.ReadAllText(path), WidgetStorage.Json);
            if (state is null || Math.Abs((long)state.Left) > 100000 || Math.Abs((long)state.Top) > 100000) return;
            _viewMode = state.ViewMode is >= 0 and <= 2 ? (WidgetViewMode)state.ViewMode.Value
                : state.Collapsed ? WidgetViewMode.Compact : WidgetViewMode.Expanded;
            _lastCardView = _viewMode != WidgetViewMode.Logo ? _viewMode
                : state.LastCardView is >= 0 and <= 1 ? (WidgetViewMode)state.LastCardView.Value : WidgetViewMode.Compact;
            if (_viewMode == WidgetViewMode.Logo) { Width = 48; Height = 48; }
            else if (_viewMode == WidgetViewMode.Compact) Height = 160;
            Topmost = state.Pinned;
            _animateTransitions = state.AnimateTransitions ?? true;
            if (state.AnchorCorner is >= 0 and <= 3) _anchorCorner = (WidgetAnchorCorner)state.AnchorCorner.Value;
            _reducedMotion = !_animateTransitions;
            var rect = new NativeRect { Left = state.Left, Top = state.Top, Right = state.Left + 304, Bottom = state.Top + 600 };
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromRect(ref rect, 2), ref info)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var left = Math.Clamp(state.Left, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - (int)(Width * dpi.DpiScaleX)));
            var top = Math.Clamp(state.Top, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - (int)(Height * dpi.DpiScaleY)));
            SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, left, top, 0, 0, 0x15);
            if (state.AnchorCorner is not (>= 0 and <= 3)) UpdateAnchorFromPosition();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { WidgetStorage.Log("Window position restore failed", ex); }
    }

    private void DockRight() => DockCorner(WidgetAnchorCorner.TopRight);

    private void DockCorner(WidgetAnchorCorner corner)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _anchorCorner = corner;
        var right = corner is WidgetAnchorCorner.TopRight or WidgetAnchorCorner.BottomRight;
        var bottom = corner is WidgetAnchorCorner.BottomLeft or WidgetAnchorCorner.BottomRight;
        var width = (int)Math.Round((ActualWidth > 0 ? ActualWidth : Width) * dpi.DpiScaleX);
        var height = (int)Math.Round((ActualHeight > 0 ? ActualHeight : Height) * dpi.DpiScaleY);
        var x = right ? info.Work.Right - width - (int)(16 * dpi.DpiScaleX) : info.Work.Left + (int)(16 * dpi.DpiScaleX);
        var y = bottom ? info.Work.Bottom - height - (int)(48 * dpi.DpiScaleY) : info.Work.Top + (int)(48 * dpi.DpiScaleY);
        x = Math.Clamp(x, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        y = Math.Clamp(y, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x15);
    }

    private void UpdateAnchorFromPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetWindowRect(handle, out var bounds) || !GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return;
        // Pick the nearest corner of this monitor's work area only after the user moves the window.
        // Keeping this choice stable while resizing prevents a fold from flipping its own anchor.
        var right = (long)bounds.Left + bounds.Right >= (long)info.Work.Left + info.Work.Right;
        var bottom = (long)bounds.Top + bounds.Bottom >= (long)info.Work.Top + info.Work.Bottom;
        _anchorCorner = bottom ? right ? WidgetAnchorCorner.BottomRight : WidgetAnchorCorner.BottomLeft
            : right ? WidgetAnchorCorner.TopRight : WidgetAnchorCorner.TopLeft;
    }
    private void SavePosition()
    {
        try
        {
            if (GetWindowRect(new WindowInteropHelper(this).Handle, out var rect))
                WidgetStorage.Write(Path.Combine(_dataDirectory, "window-state.json"), new SavedWindow(rect.Left, rect.Top, Topmost, _viewMode != WidgetViewMode.Expanded, (int)_viewMode, _animateTransitions, (int)_anchorCorner, (int)_lastCardView));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { WidgetStorage.Log("Window position save failed", ex); }
    }

    private void ToggleLogo() => SetViewMode(_viewMode == WidgetViewMode.Logo ? _lastCardView : WidgetViewMode.Logo);

    private void SetViewMode(WidgetViewMode mode)
    {
        if (_closed || mode == _viewMode) return;
        StopResize();
        _transitionFrom = _viewMode;
        // Hiding is independent of the card's detail level; remember either card for restoration.
        if (mode != WidgetViewMode.Logo) _lastCardView = mode;
        else if (_viewMode != WidgetViewMode.Logo) _lastCardView = _viewMode;
        // Collapse turns clockwise; expansion unwinds by the same eased 120-degree step.
        _logoRotationStep = mode > _viewMode ? 120 : -120;
        _logoRotation += _logoRotationStep;
        _viewMode = mode;
        _viewRevision++;
        _transitionPhase = _animateTransitions ? "hiding" : "settled";
        if (!_animateTransitions) ResizeTo(mode == WidgetViewMode.Logo ? 48 : 304, TargetHeight());
        Publish();
    }

    private double TargetHeight() => _viewMode == WidgetViewMode.Logo ? 48 : _viewMode == WidgetViewMode.Compact ? _compactHeight : _expandedHeight;

    private void CompleteViewTransition()
    {
        if (_transitionPhase != "resizing") return;
        _transitionPhase = "settled";
        Publish();
    }

    private void ResizeTo(double width, double height)
    {
        if (_applyingResize) { Dispatcher.BeginInvoke(() => ResizeTo(width, height), DispatcherPriority.Render); return; }
        if (_closed) return;
        if (IsResizing && Math.Abs(_resizeWidth - width) < 1 && Math.Abs(_resizeHeight - height) < 1 && !_reducedMotion) return;
        if (!IsResizing && Math.Abs(ActualWidth - width) < 1 && Math.Abs(ActualHeight - height) < 1) { CompleteViewTransition(); return; }
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var previous)) return;
        StopResize();
        _resizeFromWidth = ActualWidth; _resizeFromHeight = ActualHeight;
        _resizeWidth = width; _resizeHeight = height;
        _resizeAnchorRight = _anchorCorner is WidgetAnchorCorner.TopRight or WidgetAnchorCorner.BottomRight;
        _resizeAnchorBottom = _anchorCorner is WidgetAnchorCorner.BottomLeft or WidgetAnchorCorner.BottomRight;
        _resizeFromX = _resizeTargetX = _resizeAnchorRight ? previous.Right : previous.Left;
        _resizeFromY = _resizeTargetY = _resizeAnchorBottom ? previous.Bottom : previous.Top;
        var dpi = VisualTreeHelper.GetDpi(this);
        _resizeDpiX = dpi.DpiScaleX; _resizeDpiY = dpi.DpiScaleY;
        _lastResizeBounds = previous;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(MonitorFromWindow(handle, 2), ref info))
        {
            var targetWidth = (int)Math.Round(width * dpi.DpiScaleX);
            var targetHeight = (int)Math.Round(height * dpi.DpiScaleY);
            _resizeTargetX = _resizeAnchorRight
                ? Math.Clamp(_resizeFromX, info.Work.Left + targetWidth, Math.Max(info.Work.Left + targetWidth, info.Work.Right))
                : Math.Clamp(_resizeFromX, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - targetWidth));
            _resizeTargetY = _resizeAnchorBottom
                ? Math.Clamp(_resizeFromY, info.Work.Top + targetHeight, Math.Max(info.Work.Top + targetHeight, info.Work.Bottom))
                : Math.Clamp(_resizeFromY, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - targetHeight));
        }
        if (!_animateTransitions || _reducedMotion)
        {
            ApplyResize(1); SavePosition(); CompleteViewTransition(); return;
        }
        if (_transitionPhase == "resizing")
        {
            // Card contents are hidden during a view morph. Keep the WebView viewport stable
            // and let the native parent clip it, instead of rerasterizing the page every frame.
            _browserSizeFrozen = true;
            Browser.HorizontalAlignment = HorizontalAlignment.Left;
            Browser.VerticalAlignment = VerticalAlignment.Top;
            Browser.Width = Math.Max(ActualWidth, width) - 2;
            Browser.Height = Math.Max(ActualHeight, height) - 2;
            Browser.UpdateLayout();
        }
        _lastResizeRenderTime = TimeSpan.MinValue;
        _resizeClock.Restart();
        CompositionTarget.Rendering += ResizeFrame;
    }

    private void ResizeFrame(object? sender, EventArgs e)
    {
        // Native resize can pump messages and re-enter WPF Rendering. An older frame
        // must never overwrite the final geometry applied by a nested frame.
        if (_applyingResize || !IsResizing) return;
        // WPF can raise Rendering more than once for the same composition frame.
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == _lastResizeRenderTime) return;
            _lastResizeRenderTime = frame.RenderingTime;
        }
        _applyingResize = true;
        try
        {
            var elapsed = _resizeClock.Elapsed.TotalMilliseconds;
            var progress = Math.Clamp(elapsed / ResizeDurationMs, 0, 1);
            var workStart = _testMode ? Stopwatch.GetTimestamp() : 0;
            ApplyResize((1 - Math.Cos(Math.PI * progress)) / 2);
            if (_testMode) ResizeFrames.Add(new(_viewRevision, elapsed, Stopwatch.GetElapsedTime(workStart).TotalMilliseconds, ActualWidth, ActualHeight,
                _browserSizeFrozen, Browser.ActualWidth, Browser.ActualHeight));
            if (progress >= 1) { StopResize(); SavePosition(); CompleteViewTransition(); }
        }
        finally { _applyingResize = false; }
    }

    private void ApplyResize(double progress)
    {
        var width = _resizeFromWidth + (_resizeWidth - _resizeFromWidth) * progress;
        var height = _resizeFromHeight + (_resizeHeight - _resizeFromHeight) * progress;
        var pixelsWide = (int)Math.Round(width * _resizeDpiX);
        var pixelsHigh = (int)Math.Round(height * _resizeDpiY);
        var x = (int)Math.Round(_resizeFromX + (_resizeTargetX - _resizeFromX) * progress) - (_resizeAnchorRight ? pixelsWide : 0);
        var y = (int)Math.Round(_resizeFromY + (_resizeTargetY - _resizeFromY) * progress) - (_resizeAnchorBottom ? pixelsHigh : 0);
        if (_lastResizeBounds.Left == x && _lastResizeBounds.Top == y
            && _lastResizeBounds.Right - x == pixelsWide && _lastResizeBounds.Bottom - y == pixelsHigh) return;
        // One atomic native resize gives WPF a single size notification per frame.
        // Setting WPF Width and Height first would cause two extra native resizes and layouts.
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, pixelsWide, pixelsHigh, 0x14);
        _lastResizeBounds = new() { Left = x, Top = y, Right = x + pixelsWide, Bottom = y + pixelsHigh };
    }

    private void StopResize()
    {
        _resizeClock.Stop();
        CompositionTarget.Rendering -= ResizeFrame;
        if (_browserSizeFrozen && !_closed)
        {
            _browserSizeFrozen = false;
            Browser.Width = double.NaN;
            Browser.Height = double.NaN;
            Browser.HorizontalAlignment = HorizontalAlignment.Stretch;
            Browser.VerticalAlignment = VerticalAlignment.Stretch;
            // Restore the final viewport before revealing the card or handing off to its logo.
            Browser.UpdateLayout();
        }
    }

    internal IntPtr WindowProc(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0231 && GetWindowRect(handle, out var bounds)) { StopResize(); _moveStart = bounds; } // WM_ENTERSIZEMOVE
        if (message == 0x0232) // WM_EXITSIZEMOVE
        {
            var start = _moveStart; _moveStart = null;
            if (_viewMode == WidgetViewMode.Logo && start is { } before && GetWindowRect(handle, out var after)
                && Math.Abs(before.Left - after.Left) <= 3 && Math.Abs(before.Top - after.Top) <= 3)
                Dispatcher.BeginInvoke(() => SetViewMode(_lastCardView));
            else
            {
                UpdateAnchorFromPosition();
                ResizeTo(_viewMode == WidgetViewMode.Logo ? 48 : 304, _viewMode == WidgetViewMode.Logo ? 48 : _viewMode == WidgetViewMode.Compact ? _compactHeight : _expandedHeight);
                SavePosition();
                Publish();
            }
        }
        // A logo double-click should restore the card, never maximize a 48px widget.
        if (_viewMode == WidgetViewMode.Logo && message == 0x00A3)
        {
            handled = true;
            Dispatcher.BeginInvoke(() => SetViewMode(_lastCardView));
        }
        return IntPtr.Zero;
    }

    private sealed record SavedWindow(int Left, int Top, bool Pinned, bool Collapsed, int? ViewMode = null, bool? AnimateTransitions = null, int? AnchorCorner = null, int? LastCardView = null);
    internal sealed record ResizeFrameSample(int Revision, double ElapsedMs, double WorkMs, double Width, double Height,
        bool ViewportFrozen, double ViewportWidth, double ViewportHeight);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
