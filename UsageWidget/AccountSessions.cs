using System.IO;
using System.Text.Json;

namespace UsageWidget;

// The host manages account IDs, persistence and polling uniformly; adapters own service-specific CLI behavior.
internal interface IAccountSession : IDisposable
{
    AiAccountProfile Profile { get; set; }
    AccountConnection Account { get; set; }
    ProviderUsage Usage { get; set; }
    void RestartClient();
    void ReleaseIdleResources();
    void PrepareSourceChange();
    Task<AccountConnection> ReadAccountAsync(CancellationToken token);
    Task<ProviderUsage> ReadUsageAsync(bool force, bool skipQueries, CancellationToken token);
    Task LoginAsync(Action<string> openLogin, CancellationToken token);
    Task CancelLoginAsync(CancellationToken token);
    Task LogoutAsync(CancellationToken token);
}

internal sealed class CodexSession : IAccountSession
{
    public AiAccountProfile Profile { get; set; }
    public AccountConnection Account { get; set; }
    public ProviderUsage Usage { get; set; }
    private CodexClient _client;
    private readonly string _directory;
    private readonly Func<CodexProfile, CodexClient>? _factory;
    internal CodexSession(string directory, AiAccountProfile profile, Func<CodexProfile, CodexClient>? factory = null)
    {
        _directory = directory; Profile = profile; _factory = factory;
        _client = CreateClient();
        Account = new(profile.Id, profile.UseSystem ? "system" : "widget", "disconnected");
        Usage = new(profile.Id, "disconnected", "ChatGPT 계정으로 로그인해주세요.", []);
    }
    private CodexClient CreateClient() => _factory?.Invoke(new(Profile.Id, Profile.Name, Profile.Enabled, Profile.UseSystem))
        ?? new(Profile.UseSystem ? null : AccountProfiles.CodexHome(_directory, Profile.Id));
    public void RestartClient() { _client.Dispose(); _client = CreateClient(); }
    public void ReleaseIdleResources() => _client.ReleaseIdleResources();
    public void PrepareSourceChange() { }
    public Task<AccountConnection> ReadAccountAsync(CancellationToken token) => _client.ReadAccountAsync(token);
    public Task<ProviderUsage> ReadUsageAsync(bool force, bool skipQueries, CancellationToken token) => _client.ReadAsync(token);
    public async Task LoginAsync(Action<string> openLogin, CancellationToken token)
    {
        await _client.LogoutAsync(token);
        openLogin(await _client.BeginLoginAsync(token));
        await _client.WaitForLoginAsync(token);
    }
    public Task CancelLoginAsync(CancellationToken token) => _client.CancelLoginAsync(token);
    public Task LogoutAsync(CancellationToken token) => Profile.Enabled && !Profile.UseSystem ? _client.LogoutAsync(token) : Task.CompletedTask;
    public void Dispose() => _client.Dispose();
}

internal sealed class ClaudeSession : IAccountSession
{
    public AiAccountProfile Profile { get; set; }
    public AccountConnection Account { get; set; }
    public ProviderUsage Usage { get; set; }
    internal ClaudeBridge Bridge { get; private set; }
    internal ClaudeAuthClient Client { get; private set; }
    private readonly string _directory;
    private readonly string? _settingsOverride;
    private readonly Func<AiAccountProfile, ClaudeAuthClient>? _factory;
    private DateTimeOffset _lastAttempt;
    internal ClaudeSession(string directory, AiAccountProfile profile, string? settingsOverride = null, Func<AiAccountProfile, ClaudeAuthClient>? factory = null)
    {
        _directory = directory; Profile = profile; _settingsOverride = settingsOverride; _factory = factory;
        Client = CreateClient(); Bridge = CreateBridge();
        Account = new(profile.Id, profile.UseSystem ? "system" : "widget", "disconnected");
        Usage = new(profile.Id, "disconnected", "Claude Code 계정으로 로그인해주세요.", []);
    }
    private ClaudeAuthClient CreateClient() => _factory?.Invoke(Profile)
        ?? new(Profile.UseSystem ? null : AccountProfiles.ClaudeHome(_directory, Profile.Id));
    private ClaudeBridge CreateBridge()
    {
        if (_settingsOverride is not null) return new(_directory, _settingsOverride);
        var home = AccountProfiles.ClaudeHome(_directory, Profile.Id);
        return Profile.UseSystem ? new(Path.Combine(_directory, "SystemClaudeBridge"))
            : new(Path.Combine(home, "UsageBridge"), Path.Combine(home, "settings.json"));
    }
    public void RestartClient() { Client.Dispose(); Client = CreateClient(); Bridge = CreateBridge(); _lastAttempt = default; }
    public void ReleaseIdleResources() { }
    public void PrepareSourceChange()
    {
        if (Bridge.Installed) Bridge.Uninstall();
        ClearSnapshot();
    }
    internal void ClearSnapshot()
    {
        if (File.Exists(Bridge.SnapshotFile)) File.Delete(Bridge.SnapshotFile);
        _lastAttempt = default;
    }
    private void BindSnapshot(string? email)
    {
        var identityFile = Path.Combine(Path.GetDirectoryName(Bridge.SnapshotFile)!, "usage-account.json");
        string? previous = null;
        if (File.Exists(identityFile))
        {
            try { using var doc = JsonDocument.Parse(File.ReadAllText(identityFile)); previous = doc.RootElement.GetProperty("email").GetString(); }
            catch (JsonException) { }
        }
        if (!File.Exists(identityFile) || !string.Equals(previous, email, StringComparison.OrdinalIgnoreCase)) ClearSnapshot();
        WidgetStorage.Write(identityFile, new { email });
    }
    public async Task<AccountConnection> ReadAccountAsync(CancellationToken token)
    {
        var account = await Client.ReadAccountAsync(token);
        if (account.Status == "connected") BindSnapshot(account.Email);
        return account;
    }
    internal ProviderUsage ReadSnapshot()
    {
        var snapshot = Bridge.Read();
        return snapshot.Status != "ready" && Usage.Status == "error" ? Usage : snapshot;
    }
    public async Task<ProviderUsage> ReadUsageAsync(bool force, bool skipQueries, CancellationToken token)
    {
        if (skipQueries || !force && DateTimeOffset.UtcNow - _lastAttempt < TimeSpan.FromMinutes(5)) return ReadSnapshot();
        _lastAttempt = DateTimeOffset.UtcNow;
        try
        {
            var usage = await Client.ReadUsageAsync(token);
            Bridge.SaveUsage(usage);
            return usage;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WidgetStorage.Log("Claude account usage query failed", ex);
            var cached = Bridge.Read();
            return cached.Status == "ready" ? cached : new(Profile.Id, "error", "자동 조회 실패 · Claude Code 업데이트와 로그인을 확인해주세요. Claude Code 응답 수신도 사용할 수 있습니다.", []);
        }
    }
    public async Task LoginAsync(Action<string> openLogin, CancellationToken token)
    {
        await Client.LogoutAsync(token);
        await Client.LoginAsync(token);
    }
    public Task CancelLoginAsync(CancellationToken token) { Client.CancelLogin(); return Task.CompletedTask; }
    public async Task LogoutAsync(CancellationToken token)
    {
        if (Profile.Enabled && !Profile.UseSystem) await Client.LogoutAsync(token);
        if (Bridge.Installed) Bridge.Uninstall();
        ClearSnapshot();
    }
    public void Dispose() => Client.Dispose();
}
