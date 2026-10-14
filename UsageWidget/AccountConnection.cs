using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal sealed record AccountConnection(string Id, string Source, string Status, string? Email = null, string? Plan = null, string? Message = null, string? Name = null);
internal sealed record ConnectionPreferences(bool UseSystemCodex = false, bool UseSystemClaude = false,
    bool CodexEnabled = false, bool ClaudeEnabled = false);

internal static class AccountProfiles
{
    internal static string CodexHome(string dataDirectory, string id = "codex")
    {
        if (!CodexAccountPreferences.IsCodexId(id)) throw new ArgumentException("Invalid Codex profile ID.", nameof(id));
        // Preserve the original account directory during upgrades. New slots never reuse its credentials.
        return id == "codex" ? Path.Combine(dataDirectory, "Accounts", "Codex")
            : Path.Combine(dataDirectory, "Accounts", "CodexProfiles", id[6..]);
    }
    internal static string ClaudeHome(string dataDirectory) => Path.Combine(dataDirectory, "Accounts", "Claude");
    internal static ConnectionPreferences ReadPreferences(string dataDirectory)
    {
        try
        {
            var file = Path.Combine(dataDirectory, "connections.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<ConnectionPreferences>(File.ReadAllText(file), WidgetStorage.Json) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal static bool IsOfficialCodexLoginUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo)
        && uri.Host is "auth.openai.com" or "chatgpt.com";
    internal static void OpenCodexLogin(string value)
    {
        if (!IsOfficialCodexLoginUrl(value)) throw new InvalidOperationException("Unexpected login host.");
        Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
    }
}
