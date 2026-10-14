using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal sealed record AccountConnection(string Id, string Source, string Status, string? Email = null, string? Plan = null, string? Message = null, string? Name = null, string? Service = null, bool Enabled = false, string? Nickname = null, string? DefaultName = null);
internal sealed record ConnectionPreferences(bool UseSystemCodex = false, bool UseSystemClaude = false,
    bool CodexEnabled = false, bool ClaudeEnabled = false);

internal static class AccountProfiles
{
    internal static string CodexHome(string dataDirectory, string id = "codex")
    {
        return Home(dataDirectory, "codex", id);
    }
    internal static string ClaudeHome(string dataDirectory, string id = "claude") => Home(dataDirectory, "claude", id);
    private static string Home(string directory, string service, string id)
    {
        if (AiServices.ServiceOf(id) != service) throw new ArgumentException("Invalid AI profile ID.", nameof(id));
        var name = AiServices.Name(service);
        // Original directories remain intact; each extra account gets its own credential/config folder.
        return id == service ? Path.Combine(directory, "Accounts", name)
            : Path.Combine(directory, "Accounts", name + "Profiles", id[(service.Length + 1)..]);
    }
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
