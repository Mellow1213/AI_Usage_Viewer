using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal sealed class CodexClient(string? profileHome = null, Func<ProcessStartInfo>? processFactory = null) : IDisposable
{
    private Process? _process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _start = new(1, 1);
    private int _nextId;
    private bool _disposed;
    private TaskCompletionSource<bool>? _login;
    private string? _loginId;
    private readonly ConcurrentDictionary<string, bool> _loginCompletions = new();

    internal async Task<AccountConnection> ReadAccountAsync(CancellationToken token)
    {
        await EnsureStartedAsync(token);
        var result = await CallAsync("account/read", new { refreshToken = false }, token);
        var source = profileHome is null ? "system" : "widget";
        if (!result.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
            return new("codex", source, "disconnected", Message: "로그인할 계정을 선택해주세요.");
        if (account.TryGetProperty("type", out var type) && type.GetString() != "chatgpt")
            return new("codex", source, "unsupported", Message: "구독 잔여량은 ChatGPT 계정 로그인이 필요합니다.");
        return new("codex", source, "connected", StringField(account, "email"), StringField(account, "planType"));
    }

    internal async Task<string> BeginLoginAsync(CancellationToken token)
    {
        if (profileHome is null) throw new InvalidOperationException("Login must use the widget profile.");
        await EnsureStartedAsync(token);
        _loginId = null;
        _loginCompletions.Clear();
        _login = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = await CallAsync("account/login/start", new { type = "chatgpt" }, token);
        _loginId = response.GetProperty("loginId").GetString();
        if (_loginId is not null && _loginCompletions.TryRemove(_loginId, out var completed)) _login.TrySetResult(completed);
        var url = response.GetProperty("authUrl").GetString() ?? "";
        if (!AccountProfiles.IsOfficialCodexLoginUrl(url)) { await CancelLoginAsync(token); throw new InvalidOperationException("Unexpected login URL."); }
        return url;
    }
    internal async Task WaitForLoginAsync(CancellationToken token)
    {
        if (_login is null || !await _login.Task.WaitAsync(TimeSpan.FromMinutes(5), token))
            throw new IOException("Codex login did not complete.");
        _loginId = null;
    }
    internal async Task CancelLoginAsync(CancellationToken token)
    {
        if (_loginId is not null && _process is { HasExited: false })
            await CallAsync("account/login/cancel", new { loginId = _loginId }, token);
        _loginId = null;
        _login?.TrySetCanceled();
    }
    internal async Task LogoutAsync(CancellationToken token)
    {
        if (profileHome is null) throw new InvalidOperationException("Cannot log out the system CLI.");
        await EnsureStartedAsync(token);
        await CallAsync("account/logout", new { }, token);
    }
    private static string? StringField(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    internal async Task<ProviderUsage> ReadAsync(CancellationToken token)
    {
        await EnsureStartedAsync(token);
        var result = await CallAsync("account/rateLimits/read", new { }, token);
        return UsageParser.Codex(result);
    }

    private async Task EnsureStartedAsync(CancellationToken token)
    {
        await _start.WaitAsync(token);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CodexClient));
            if (_process is { HasExited: false }) return;
            _process?.Dispose();
            var info = processFactory is null ? FindCommand() : processFactory();
            if (profileHome is not null)
            {
                Directory.CreateDirectory(profileHome);
                info.Environment["CODEX_HOME"] = profileHome;
                info.Environment.Remove("OPENAI_API_KEY");
                info.Environment.Remove("CODEX_ACCESS_TOKEN");
                info.Environment.Remove("OPENAI_BASE_URL");
                // File storage is scoped to this CODEX_HOME rather than a shared keyring.
                info.ArgumentList.Add("-c");
                info.ArgumentList.Add("cli_auth_credentials_store=\"file\"");
            }
            info.ArgumentList.Add("app-server");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = System.Text.Encoding.UTF8;
            info.StandardErrorEncoding = System.Text.Encoding.UTF8;
            info.WorkingDirectory = AppContext.BaseDirectory;
            var process = Process.Start(info) ?? throw new IOException("Codex did not start.");
            _process = process;
            _ = PumpAsync(process);
            _ = DrainErrorsAsync(process);
            try
            {
                await CallAsync("initialize", new { clientInfo = new { name = "ai_usage_widget", title = "AI Usage Widget", version = "1.0.0" } }, token);
                await SendAsync(new { method = "initialized", @params = new { } }, token);
            }
            catch { Stop(); throw; }
        }
        finally { _start.Release(); }
    }

    private static ProcessStartInfo FindCommand()
    {
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        var candidates = new[]
        {
            Path.Combine(npm, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe"),
            Path.Combine(npm, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe"),
            Path.Combine(npm, "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe")
        };
        foreach (var path in candidates) if (File.Exists(path)) return new(path);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var executable = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(executable)) return new(executable);
        }
        var script = Path.Combine(npm, "bin", "codex.js");
        if (File.Exists(script))
        {
            var info = new ProcessStartInfo("node.exe");
            info.ArgumentList.Add(script);
            return info;
        }
        throw new FileNotFoundException("Codex CLI is not installed.");
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(18), token);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task SendAsync(object value, CancellationToken token)
    {
        await _write.WaitAsync(token);
        try
        {
            if (_process is null || _process.HasExited) throw new IOException("Codex connection closed.");
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), token);
            await _process.StandardInput.FlushAsync(token);
        }
        finally { _write.Release(); }
    }

    private async Task PumpAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("method", out var method) && method.GetString() == "account/login/completed"
                        && root.TryGetProperty("params", out var parameters))
                    {
                        if (parameters.TryGetProperty("loginId", out var loginId) && loginId.ValueKind == JsonValueKind.String && loginId.GetString() is { } completedId)
                        {
                            var succeeded = parameters.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
                            _loginCompletions[completedId] = succeeded;
                            if (completedId == _loginId) _login?.TrySetResult(succeeded);
                        }
                        continue;
                    }
                    if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || !_pending.TryGetValue(number, out var request)) continue;
                    if (root.TryGetProperty("result", out var result)) request.TrySetResult(result.Clone());
                    else if (root.TryGetProperty("error", out _)) request.TrySetException(new IOException("Codex returned an error. Check CLI login."));
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally
        {
            foreach (var request in _pending.Values) request.TrySetException(new IOException("Codex connection closed."));
        }
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        try { while (await process.StandardError.ReadLineAsync() is not null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
    private void Stop()
    {
        // This client never starts a model turn or a child command; stop only its owned reader.
        try { if (_process is { HasExited: false }) _process.Kill(); }
        catch (InvalidOperationException) { }
    }
    public void Dispose() { _disposed = true; _login?.TrySetCanceled(); Stop(); _process?.Dispose(); }
}
