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
    private readonly UsageHistory _history;
    private bool _historyVisible;
    private AiAccountPreferences _accountProfiles;
    private readonly Dictionary<string, IAccountSession> _sessions;
    private readonly Func<CodexProfile, CodexClient>? _codexFactory;
    private readonly Func<AiAccountProfile, ClaudeAuthClient>? _claudeFactory;
    private readonly string? _claudeSettingsOverride;
    private readonly Action<string> _openCodexLogin;
    private ConnectionPreferences _connections;
    private DashboardPreferences _dashboard;
    private MeasurementPreferences _measurement;
    private readonly WidgetUpdater _updates;
    private bool _startupUpdateCheckStarted;
    private readonly bool _checkUpdatesOnStartup;
    private CancellationTokenSource? _loginCancellation;
    private string? _loginProvider;
    private string? _codexLoginUrl;
    private bool AuthBusy => _loginProvider is not null;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly bool _testMode;
    private DateTimeOffset _lastRefreshAttempt;
    private bool _busy;
    private bool _ready;
    private string? _lastPublishedState;
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
    internal ClaudeBridge Claude => ((ClaudeSession)_sessions["claude"]).Bridge;
    internal ProviderUsage CodexUsage => _sessions["codex"].Usage;
    internal WidgetViewMode ViewMode => _viewMode;
    internal WidgetAnchorCorner AnchorCorner => _anchorCorner;
    internal string WidgetAddress => _widgetUrl;

    public MainWindow() : this(GetDataDirectory()) { }
    internal MainWindow(string dataDirectory, bool testMode = false, string? claudeSettings = null, Func<CodexProfile, CodexClient>? codexFactory = null, Action<string>? openCodexLogin = null, Func<AiAccountProfile, ClaudeAuthClient>? claudeFactory = null, WidgetUpdater? updates = null, bool? checkUpdatesOnStartup = null, Func<IntPtr>? foregroundWindow = null)
    {
        InitializeComponent();
        _dataDirectory = dataDirectory;
        WidgetStorage.UseDataDirectory(dataDirectory);
        _daily = new(dataDirectory);
        _history = new(dataDirectory);
        _testMode = testMode;
        _checkUpdatesOnStartup = checkUpdatesOnStartup ?? !testMode;
        _foregroundWindow = foregroundWindow ?? GetForegroundWindow;
        _codexFactory = codexFactory;
        _claudeFactory = claudeFactory;
        _claudeSettingsOverride = claudeSettings;
        _openCodexLogin = openCodexLogin ?? AccountProfiles.OpenCodexLogin;
        // A new UI build must not reuse a cached index page from an older release.
        _widgetUrl = WidgetUrl + "?v=" + File.GetLastWriteTimeUtc(Path.Combine(AppContext.BaseDirectory, "Web", "index.html")).Ticks;
        _connections = AccountProfiles.ReadPreferences(dataDirectory);
        _accountProfiles = AiAccountPreferences.Read(dataDirectory, _connections);
        _sessions = _accountProfiles.Profiles.ToDictionary(profile => profile.Id, CreateSession);
        _dashboard = DashboardPreferences.Read(dataDirectory, _connections);
        foreach (var session in _sessions.Values.Where(s => _dashboard.ProviderIds.Contains(s.Profile.Id)))
            BeginAutoConnection(session);
        _measurement = MeasurementPreferences.Read(dataDirectory);
        _updates = updates ?? new WidgetUpdater(dataDirectory);
        _updates.Changed += () => { if (!_closed) Publish(); };
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 19, 22);
        SourceInitialized += (_, _) =>
        {
            var round = 2;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, sizeof(int));
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowProc);
            RestorePosition();
            InitializePresence();
        };
        SizeChanged += (_, _) => Surface.Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 22, 22);
        Loaded += async (_, _) => await InitializeAsync();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _timer.Tick += async (_, _) =>
        {
            if (_closed || !_ready) return;
            if (!AuthBusy && !_busy)
                foreach (var session in _sessions.Values.OfType<ClaudeSession>().Where(s => _dashboard.ProviderIds.Contains(s.Profile.Id)))
                    if (session.Account.Status == "connected") session.Usage = session.ReadSnapshot() with { Id = session.Profile.Id };
            Publish();
            if (!_testMode && _dashboard.ProviderIds.Length > 0 && !_busy && !AuthBusy && DateTimeOffset.UtcNow - _lastRefreshAttempt >= TimeSpan.FromSeconds(60)) await RefreshAsync();
        };
        Closing += (_, _) =>
        {
            _closed = true;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            StopResize();
            _timer.Stop();
            DisposePresence();
            _lifetime.Cancel();
            _updates.Dispose();
            foreach (var session in _sessions.Values) session.Dispose();
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

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume && !_closed)
            Dispatcher.BeginInvoke(() => { if (!_closed) _history.Interrupt(); });
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
            web.ContextMenuRequested += (_, e) =>
            {
                if (_viewMode != WidgetViewMode.Logo) return;
                e.Handled = true;
                Dispatcher.BeginInvoke(ShowLogoMenu);
            };
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
                    // A UI handshake must preserve pending connections and already received usage.
                    Publish(force: true);
                    _timer.Start();
                    UpdatePresence();
                    if (_checkUpdatesOnStartup && !_startupUpdateCheckStarted)
                    {
                        _startupUpdateCheckStarted = true;
                        _ = Dispatcher.BeginInvoke(async () => await _updates.CheckAsync(), DispatcherPriority.ApplicationIdle);
                    }
                    if (!_testMode && _dashboard.ProviderIds.Length > 0) await RefreshAsync();
                    break;
                case "refresh": await RefreshAsync(true); break;
                case "setHistoryVisible":
                    _historyVisible = root.TryGetProperty("visible", out var historyVisible) && historyVisible.ValueKind == JsonValueKind.True;
                    Publish(); break;
                case "checkUpdates": await _updates.CheckAsync(); break;
                case "downloadUpdate": await _updates.DownloadAsync(); break;
                case "applyUpdate":
                    if (AuthBusy) { _notice = "로그인을 완료하거나 취소한 뒤 업데이트를 적용해주세요."; Publish(); }
                    else if (_updates.PrepareRestart()) Close();
                    break;
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
                        if (AiServices.Catalog.Any(service => service.Id == id) && (_dashboard.ProviderIds.Contains(id)
                            || _accountProfiles.Profiles.Any(profile => profile.Service == id && (profile.Enabled || profile.Id != id)))) await AddAccountAsync(id);
                        else await ShowProviderAsync(id);
                    }
                    break;
                case "addCodexAccount": await AddCodexAccountAsync(); break;
                case "addAccount":
                    if (root.TryGetProperty("service", out var serviceValue) && serviceValue.ValueKind == JsonValueKind.String)
                        await AddAccountAsync(serviceValue.GetString()!);
                    break;
                case "showProvider":
                    if (root.TryGetProperty("provider", out var shown) && shown.ValueKind == JsonValueKind.String)
                        await ShowProviderAsync(shown.GetString()!);
                    break;
                case "removeProvider":
                    if (root.TryGetProperty("provider", out var removed) && removed.ValueKind == JsonValueKind.String)
                        RemoveAccount(removed.GetString()!);
                    break;
                case "setAccountNickname":
                    if (root.TryGetProperty("nickname", out var nicknameValue) && nicknameValue.ValueKind == JsonValueKind.String)
                        SetAccountNickname(AccountTarget(root), nicknameValue.GetString());
                    break;
                case "pin": ToggleCardPin(); break;
                case "toggleLogoPin": ToggleLogoPin(); break;
                case "toggleFullscreenHide": ToggleFullscreenHide(); break;
                case "logoMenu": if (_viewMode == WidgetViewMode.Logo) ShowLogoMenu(); break;
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
                case "loginCodex": await LoginAsync(AccountTarget(root, "codex")); break;
                case "loginClaude": await LoginAsync(AccountTarget(root, "claude")); break;
                case "loginAccount": await LoginAsync(AccountTarget(root)); break;
                case "useExistingCodex": await SelectExistingAsync(AccountTarget(root, "codex")); break;
                case "useExistingClaude": await SelectExistingAsync(AccountTarget(root, "claude")); break;
                case "useExistingAccount": await SelectExistingAsync(AccountTarget(root)); break;
                case "logoutCodex": await DisconnectAsync(AccountTarget(root, "codex")); break;
                case "logoutClaude": await DisconnectAsync(AccountTarget(root, "claude")); break;
                case "logoutAccount": await DisconnectAsync(AccountTarget(root)); break;
                case "cancelLogin": _loginCancellation?.Cancel(); break;
                case "reopenLogin": if (_codexLoginUrl is not null) _openCodexLogin(_codexLoginUrl); break;
                case "launchClaudeCode":
                    var targetClaude = (ClaudeSession)_sessions[AccountTarget(root, "claude")];
                    if (targetClaude.Account.Status != "connected") throw new InvalidOperationException("Claude is not connected.");
                    var folder = new Microsoft.Win32.OpenFolderDialog { Title = "이 계정으로 작업할 프로젝트 폴더 선택" };
                    if (folder.ShowDialog(this) == true) targetClaude.Client.LaunchCode(folder.FolderName);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or TimeoutException)
        {
            WidgetStorage.Log("Widget action failed", ex);
            _notice = "설정을 적용하지 못했습니다. 파일 권한과 계정 연결 상태를 확인해주세요.";
            Publish();
        }
    }

    internal async Task RefreshAsync(bool forceClaude = false)
    {
        if (_busy || _closed || AuthBusy) return;
        var sessions = _sessions.Values.Where(session => _dashboard.ProviderIds.Contains(session.Profile.Id)).ToArray();
        // Mark every saved account before publishing, including readers waiting for a concurrency slot.
        foreach (var session in sessions) BeginAutoConnection(session);
        _busy = true; _lastRefreshAttempt = DateTimeOffset.UtcNow; Publish();
        try
        {
            using var readers = new SemaphoreSlim(3);
            var tasks = sessions
                .Select(async session =>
                {
                    await readers.WaitAsync(_lifetime.Token);
                    try { await RefreshAccountAsync(session, forceClaude); }
                    finally { readers.Release(); }
                }).ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (_closed) { }
        finally { _busy = false; if (!_closed) Publish(); }
    }

    private async Task RefreshAccountAsync(IAccountSession session, bool force)
    {
        var id = session.Profile.Id;
        if (!session.Profile.Enabled) { session.Usage = NotConnected(id); return; }
        try
        {
            var account = (await session.ReadAccountAsync(_lifetime.Token)) with { Id = id };
            if (account.Status == "connected" && (session.Usage.Status == "connecting"
                || !string.Equals(account.Email, session.Account.Email, StringComparison.OrdinalIgnoreCase)))
                session.Usage = new(id, "loading", "사용량을 조회하고 있습니다.", []);
            session.Account = account;
            session.Usage = account.Status == "connected"
                ? (await session.ReadUsageAsync(force, _testMode && _claudeFactory is null && session is ClaudeSession, _lifetime.Token)) with { Id = id }
                : NotConnected(id);
        }
        catch (OperationCanceledException) when (_closed) { throw; }
        catch (Exception ex)
        {
            WidgetStorage.Log("AI account usage read failed", ex);
            if (session.Account.Status != "connected") session.Account = new(id, session.Profile.UseSystem ? "system" : "widget", "error");
            session.Usage = session.Usage with { Status = "error", Message = ex is FileNotFoundException
                ? $"{AiServices.Name(session.Profile.Service)} CLI를 설치한 뒤 로그인해주세요." : "조회 실패 · CLI 로그인과 네트워크를 확인해주세요." };
        }
        finally { session.ReleaseIdleResources(); if (!_closed) Publish(); }
    }

    private static void BeginAutoConnection(IAccountSession session)
    {
        if (!session.Profile.Enabled || session.Account.Status == "connected") return;
        session.Account = session.Account with { Status = "connecting", Message = "자동 연결 중입니다…" };
        session.Usage = session.Usage with { Status = "connecting", Message = "자동 연결 중입니다…" };
    }

    private IAccountSession CreateSession(AiAccountProfile profile) => profile.Service switch
    {
        "codex" => new CodexSession(_dataDirectory, profile, _codexFactory),
        "claude" => new ClaudeSession(_dataDirectory, profile, profile.Id == "claude" ? _claudeSettingsOverride : null, _claudeFactory),
        _ => throw new InvalidOperationException("Unsupported AI service.")
    };

    private void SaveConnections() => _connections = _accountProfiles.Save(_dataDirectory);

    private string AccountTarget(JsonElement message, string? service = null)
    {
        var id = message.TryGetProperty("accountId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : service;
        if (id is null || !_sessions.TryGetValue(id, out var session) || service is not null && session.Profile.Service != service)
            throw new InvalidOperationException("Unknown AI account.");
        return id;
    }
    private void UpdateProfile(IAccountSession session, bool enabled, bool system)
    {
        session.Profile = session.Profile with { Enabled = enabled, UseSystem = system };
        _accountProfiles = _accountProfiles.Update(session.Profile);
    }
    private void SetAccountNickname(string id, string? nickname)
    {
        if (_closed || !_sessions.TryGetValue(id, out var session)) return;
        var profile = session.Profile with { Nickname = AiAccountProfile.NormalizeNickname(nickname) };
        var updated = _accountProfiles.Update(profile);
        // Naming never changes the account ID, credentials, connection state or consumption ledger.
        WidgetStorage.Write(Path.Combine(_dataDirectory, "account-profiles.json"), updated);
        _accountProfiles = updated;
        session.Profile = profile;
        _notice = profile.Nickname is null ? "기본 계정 이름으로 표시합니다." : "별명을 저장했습니다.";
        Publish();
    }
    private ProviderUsage NotConnected(string id) => new(id, "disconnected",
        AiServices.ServiceOf(id) == "codex" ? "ChatGPT 계정으로 로그인해주세요." : "Claude Code 계정으로 로그인해주세요.", []);

    internal async Task SelectExistingAsync(string id)
    {
        if (_busy || AuthBusy || _closed) return;
        if (!_sessions.TryGetValue(id, out var session) || id != session.Profile.Service)
            throw new InvalidOperationException("Only the original service slot may follow its system CLI.");
        session.PrepareSourceChange();
        UpdateProfile(session, true, true); session.RestartClient();
        session.Account = new(id, "system", "checking"); session.Usage = NotConnected(id);
        SaveConnections();
        await RefreshAsync(true);
        if (session is ClaudeSession claude && session.Account.Status == "connected")
        { claude.Bridge.Install(); session.Usage = claude.ReadSnapshot() with { Id = id }; }
        _notice = "기존 CLI 계정 사용을 선택했습니다. CLI 계정이 바뀌면 해당 카드도 따라갑니다.";
        Publish();
    }

    private async Task LoginAsync(string id)
    {
        if (_busy || AuthBusy || _closed || !_sessions.TryGetValue(id, out var session)) return;
        _loginProvider = id;
        _loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _loginCancellation.Token;
        _notice = "공식 로그인 화면에서 직접 로그인해주세요. 5분 안에 완료하거나 취소할 수 있습니다.";
        try
        {
            session.PrepareSourceChange(); UpdateProfile(session, true, false); session.RestartClient();
            session.Account = new(id, "widget", "signingIn");
            session.Usage = new(id, "signingIn", "공식 로그인 화면에서 완료해주세요.", []);
            SaveConnections(); Publish();
            await session.LoginAsync(url => { _codexLoginUrl = url; _openCodexLogin(url); }, token);
            session.Account = (await session.ReadAccountAsync(token)) with { Id = id };
            if (session.Account.Status != "connected") throw new IOException("AI login is incomplete.");
            if (session is ClaudeSession claude) claude.Bridge.Install();
            session.Usage = new(id, "loading", "사용량을 조회하고 있습니다.", []); Publish();
            session.Usage = (await session.ReadUsageAsync(true, false, token)) with { Id = id };
            _notice = $"{session.Profile.DisplayName} 계정을 연결했습니다.";
        }
        catch (Exception ex)
        {
            if (_closed) return;
            try { await session.CancelLoginAsync(_lifetime.Token); }
            catch (Exception error) { WidgetStorage.Log("Login cancellation failed", error); }
            session.Account = new(id, "widget", "disconnected"); session.Usage = NotConnected(id);
            WidgetStorage.Log("Account login ended", ex);
            _notice = ex is OperationCanceledException ? "로그인을 취소했습니다." : "로그인이 완료되지 않았습니다. CLI 설치와 공식 로그인 창을 확인해주세요.";
        }
        finally
        {
            session.ReleaseIdleResources();
            _codexLoginUrl = null; _loginProvider = null;
            _loginCancellation.Dispose(); _loginCancellation = null;
            if (!_closed) Publish();
        }
    }

    internal async Task DisconnectAsync(string id)
    {
        if (_busy || AuthBusy || _closed || !_sessions.TryGetValue(id, out var session)) return;
        _busy = true; Publish();
        try
        {
            await session.LogoutAsync(_lifetime.Token);
            UpdateProfile(session, false, false); session.RestartClient();
            session.Account = new(id, "widget", "disconnected"); session.Usage = NotConnected(id);
            SaveConnections(); _notice = $"{session.Profile.DisplayName} 연결을 해제했습니다. 다른 계정의 로그인은 유지됩니다.";
        }
        finally { _busy = false; if (!_closed) Publish(); }
    }

    private void Publish(bool force = false)
    {
        if (!_ready || _closed || WebView is null) return;
        string? Identity(AccountConnection account, ProviderUsage usage) => _testMode ? "verification"
            : account.Status == "connected" && account.Email is not null
                ? $"{account.Source}:{account.Email.ToLowerInvariant()}:{usage.Plan ?? account.Plan}" : null;
        var now = DateTimeOffset.UtcNow;
        var interval = _testMode ? TimeSpan.Zero : TimeSpan.FromMinutes(_measurement.IntervalMinutes);
        _history.RetainProviders(_dashboard.ProviderIds);
        var providers = _dashboard.ProviderIds.Select(id =>
        {
            var session = _sessions[id];
            var usage = session.Usage with { Id = id, Name = session.Profile.DisplayName, Service = session.Profile.Service, DefaultName = session.Profile.Name };
            _history.Observe(usage, Identity(session.Account, usage), now);
            return _daily.Observe(usage, Identity(session.Account, usage), now, interval, _measurement.WindowMinutes);
        }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            busy = _busy, pinned = _cardPinned, logoPinned = _logoPinned, hideLogoInFullscreen = _hideLogoInFullscreen,
            collapsed = _viewMode != WidgetViewMode.Expanded, viewMode = (int)_viewMode, viewRevision = _viewRevision,
            bridgeInstalled = Claude.Installed, notice = _notice, authBusy = AuthBusy, loginProvider = _loginProvider,
            animateTransitions = _animateTransitions, transitionPhase = _transitionPhase, transitionFrom = (int)_transitionFrom,
            logoRotation = _logoRotation, logoRotationStep = _logoRotationStep, anchorCorner = (int)_anchorCorner, lastCardView = (int)_lastCardView,
            measurementIntervalMinutes = _measurement.IntervalMinutes, measurementWindowMinutes = _measurement.WindowMinutes,
            update = _updates.State,
            history = _historyVisible ? _history.Snapshot(_sessions.Keys) : null,
            maxCardHeight = MaximumCardHeight(), serviceCatalog = AiServices.Catalog,
            accounts = _sessions.Values.Select(session => session.Account with
                { Name = session.Profile.DisplayName, Service = session.Profile.Service, Enabled = session.Profile.Enabled,
                    Nickname = session.Profile.Nickname, DefaultName = session.Profile.Name }).ToArray(), providers
        }, WidgetStorage.Json);
        // Keep source polling and daily aggregation running, but avoid parsing and
        // rendering an identical snapshot in React. A new ready handshake always
        // receives the full state, including after a page reload.
        if (!force && payload == _lastPublishedState) return;
        WebView.PostWebMessageAsJson(payload);
        _lastPublishedState = payload;
    }

    internal void SetFixture(ProviderUsage codex, ProviderUsage claude, bool busy = false)
    {
        if (!_testMode) throw new InvalidOperationException("Fixtures are only available in verification mode.");
        _timer.Stop(); _busy = busy;
        SetAccountFixture("codex", codex); SetAccountFixture("claude", claude);
    }
    internal void SetCodexFixture(string id, ProviderUsage usage, string? email = null) => SetAccountFixture(id, usage, email);
    internal void SetAccountFixture(string id, ProviderUsage usage, string? email = null)
    {
        if (!_testMode || !_sessions.TryGetValue(id, out var session)) throw new InvalidOperationException("Unknown test account.");
        session.Usage = usage with { Id = id };
        session.Account = new(id, "widget", usage.Status is "disconnected" or "signingIn" ? "disconnected" : "connected", Email: email);
        Publish();
    }

    internal Task<string?> AddCodexAccountAsync() => AddAccountAsync("codex");
    internal async Task<string?> AddAccountAsync(string service)
    {
        if (_busy || AuthBusy || _closed || !AiServices.Catalog.Any(item => item.Id == service)) return null;
        var updated = _accountProfiles.Add(service, out var profile);
        WidgetStorage.Write(Path.Combine(_dataDirectory, "account-profiles.json"), updated);
        _accountProfiles = updated; _sessions.Add(profile.Id, CreateSession(profile)); SaveConnections();
        await ShowProviderAsync(profile.Id);
        _notice = $"{profile.Name} 카드를 추가했습니다. 계정 연결에서 로그인해주세요."; Publish();
        return profile.Id;
    }
    private void RemoveAccount(string id)
    {
        if (_closed || _busy || AuthBusy || !_sessions.TryGetValue(id, out var session)) return;
        var profile = session.Profile;
        var profiles = _accountProfiles.Remove(id);
        var dashboard = new DashboardPreferences(_dashboard.ProviderIds.Where(provider => provider != id).ToArray());
        if (session is ClaudeSession claude && claude.Bridge.Installed) claude.Bridge.Uninstall();
        // Delete only the app entry; retain CLI login files and local usage history.
        WidgetStorage.Write(Path.Combine(_dataDirectory, "dashboard.json"), dashboard);
        var connections = profiles.Save(_dataDirectory);
        _accountProfiles = profiles; _dashboard = dashboard; _connections = connections;
        session.Dispose();
        _sessions.Remove(id);
        if (id == profile.Service) _sessions.Add(id, CreateSession(profiles.Profiles.Single(p => p.Id == id)));
        _notice = $"{profile.DisplayName} 계정 항목을 삭제했습니다.";
        Publish();
    }
    private async Task ShowProviderAsync(string id)
    {
        if (_closed || !_sessions.TryGetValue(id, out var session) || AuthBusy || _busy || _dashboard.ProviderIds.Contains(id)) return;
        var dashboard = new DashboardPreferences([.. _dashboard.ProviderIds, id]);
        WidgetStorage.Write(Path.Combine(_dataDirectory, "dashboard.json"), dashboard);
        _dashboard = dashboard;
        BeginAutoConnection(session);
        Publish();
        if (!_testMode && session.Profile.Enabled) await RefreshAsync();
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
            _cardPinned = state.Pinned;
            _logoPinned = state.LogoPinned ?? true;
            _hideLogoInFullscreen = state.HideLogoInFullscreen ?? true;
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
                WidgetStorage.Write(Path.Combine(_dataDirectory, "window-state.json"), new SavedWindow(rect.Left, rect.Top, _cardPinned, _viewMode != WidgetViewMode.Expanded, (int)_viewMode, _animateTransitions, (int)_anchorCorner, (int)_lastCardView, _logoPinned, _hideLogoInFullscreen));
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
        if (mode != WidgetViewMode.Logo) _hiddenUntil = null;
        ApplyPresencePolicy();
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
        UpdatePresence();
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
        if (_viewMode == WidgetViewMode.Logo && message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE: allow the click/drag without taking keyboard focus.
        }
        if (_viewMode == WidgetViewMode.Logo && message is 0x00A5 or 0x007B) // WM_NCRBUTTONUP / WM_CONTEXTMENU
        {
            handled = true;
            Dispatcher.BeginInvoke(ShowLogoMenu);
        }
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

    private sealed record SavedWindow(int Left, int Top, bool Pinned, bool Collapsed, int? ViewMode = null, bool? AnimateTransitions = null, int? AnchorCorner = null, int? LastCardView = null, bool? LogoPinned = null, bool? HideLogoInFullscreen = null);
    internal sealed record ResizeFrameSample(int Revision, double ElapsedMs, double WorkMs, double Width, double Height,
        bool ViewportFrozen, double ViewportWidth, double ViewportHeight);
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
