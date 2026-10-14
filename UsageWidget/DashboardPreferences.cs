using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal sealed record DashboardPreferences(string[] ProviderIds)
{
    internal static bool Supported(string id) => id is "codex" or "claude";
    internal static DashboardPreferences Read(string directory, ConnectionPreferences connections)
    {
        var path = Path.Combine(directory, "dashboard.json");
        try
        {
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<DashboardPreferences>(File.ReadAllText(path), WidgetStorage.Json);
                return new((saved?.ProviderIds ?? []).Where(Supported).Distinct().ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { WidgetStorage.Log("Dashboard preferences restore failed", ex); }
        // Existing explicitly connected accounts remain visible when upgrading.
        return new(new[] { connections.CodexEnabled ? "codex" : null, connections.ClaudeEnabled ? "claude" : null }
            .OfType<string>().ToArray());
    }
}
