using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UsageWidget;

// Delegates sign-in to the unmodified official Claude Code executable.
// This app never reads, copies or sends Claude OAuth tokens.
internal sealed class ClaudeAuthClient(string? profileHome = null, Func<string[], ProcessStartInfo>? processFactory = null) : IDisposable
{
    private Process? _loginProcess;
    internal string Source => profileHome is null ? "system" : "widget";

    internal static string FindExecutable()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), "claude.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Claude Code is not installed.");
    }

    internal ProcessStartInfo StartInfo(params string[] arguments)
    {
        var info = processFactory?.Invoke(arguments) ?? new ProcessStartInfo(FindExecutable());
        info.UseShellExecute = false;
        info.WorkingDirectory = AppContext.BaseDirectory;
        if (processFactory is null) foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (profileHome is not null)
        {
            Directory.CreateDirectory(profileHome);
            info.Environment["CLAUDE_CONFIG_DIR"] = profileHome;
            // Inherited provider credentials must not override the chosen subscription account.
            foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                || k is "CLAUDE_CODE_OAUTH_TOKEN" or "CLAUDE_CODE_USE_BEDROCK" or "CLAUDE_CODE_USE_VERTEX" or "CLAUDE_CODE_USE_FOUNDRY" or "CLAUDE_CODE_SIMPLE").ToArray())
                info.Environment.Remove(key);
        }
        return info;
    }

    internal async Task<AccountConnection> ReadAccountAsync(CancellationToken token)
    {
        var (code, output) = await RunAsync(["auth", "status", "--json"], token);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        if (profileHome is not null && root.TryGetProperty("configDirectory", out var directory) && directory.ValueKind == JsonValueKind.String
            && !string.Equals(Path.GetFullPath(directory.GetString()!), Path.GetFullPath(profileHome), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Claude Code did not use its isolated profile.");
        if (code != 0 || !root.TryGetProperty("loggedIn", out var logged) || logged.ValueKind != JsonValueKind.True)
            return new("claude", Source, "disconnected", Message: "Claude Code에서 로그인할 계정을 선택해주세요.");
        var method = StringField(root, "authMethod");
        if (method != "claude.ai") return new("claude", Source, "unsupported", Message: "Claude 구독 계정으로 로그인해주세요.");
        return new("claude", Source, "connected", StringField(root, "email"), StringField(root, "subscriptionType"));
    }

    internal async Task LoginAsync(CancellationToken token)
    {
        if (profileHome is null) throw new InvalidOperationException("Login must use the widget profile.");
        var info = StartInfo("auth", "login", "--claudeai");
        // The official CLI owns the browser flow and any fallback code-entry prompt.
        // A visible console is intentional: the user must complete authentication themselves.
        info.CreateNoWindow = processFactory is not null;
        _loginProcess = Process.Start(info) ?? throw new IOException("Claude login did not start.");
        try
        {
            await _loginProcess.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMinutes(5), token);
            if (_loginProcess.ExitCode != 0) throw new IOException("Claude login did not complete.");
        }
        finally { CancelLogin(); }
    }

    internal void CancelLogin()
    {
        try { if (_loginProcess is { HasExited: false }) _loginProcess.Kill(); }
        catch (InvalidOperationException) { }
        _loginProcess?.Dispose(); _loginProcess = null;
    }
    internal async Task LogoutAsync(CancellationToken token)
    {
        if (profileHome is null) throw new InvalidOperationException("Cannot log out the system CLI.");
        var result = await RunAsync(["auth", "logout"], token);
        if (result.Code != 0) throw new IOException("Claude logout failed.");
    }
    internal void LaunchCode(string folder)
    {
        var info = StartInfo();
        info.WorkingDirectory = Path.GetFullPath(folder);
        info.CreateNoWindow = false;
        Process.Start(info)?.Dispose();
    }

    internal async Task<ProviderUsage> ReadUsageAsync(CancellationToken token)
    {
        // The official SDK's experimental get_usage control request delegates
        // credentials and refresh to Claude Code. No user/model message is sent.
        var info = StartInfo("-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--tools", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--setting-sources", "",
            "--disable-slash-commands", "--no-session-persistence");
        return await ClaudeUsageControl.ReadAsync(info, token);
    }

    private async Task<(int Code, string Output)> RunAsync(string[] arguments, CancellationToken token)
    {
        var info = StartInfo(arguments);
        info.CreateNoWindow = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = System.Text.Encoding.UTF8;
        using var process = Process.Start(info) ?? throw new IOException("Claude Code did not start.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(20), token);
            _ = await errors; // CLI errors can contain auth details; never log or publish them.
            return (process.ExitCode, await output);
        }
        finally { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
    }
    private static string? StringField(JsonElement value, string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    public void Dispose() => CancelLogin();
}
