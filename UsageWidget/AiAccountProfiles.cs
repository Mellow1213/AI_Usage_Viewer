using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal sealed record AiService(string Id, string Name, string Symbol, string Description, string LoginName);
internal static class AiServices
{
    internal static readonly AiService[] Catalog =
    [new("codex", "Codex", "⌘", "ChatGPT 구독 한도 · 자동 조회", "ChatGPT"), new("claude", "Claude", "✳", "Claude 구독 한도 · 자동 조회", "Claude Code")];

    internal static string? ServiceOf(string? id)
    {
        if (id is null) return null;
        foreach (var service in Catalog)
            if (id == service.Id || id.StartsWith(service.Id + "-", StringComparison.Ordinal)
                && Guid.TryParseExact(id[(service.Id.Length + 1)..], "N", out _)) return service.Id;
        return null;
    }
    internal static string Name(string service) => Catalog.Single(item => item.Id == service).Name;
}

internal sealed record AiAccountProfile(string Id, string Service, string Name, bool Enabled = false, bool UseSystem = false, string? Nickname = null)
{
    internal string DisplayName => Nickname ?? Name;
    internal static string? NormalizeNickname(string? value)
    {
        if (value is null) return null;
        var text = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (text.Length > 24)
        {
            text = text[..24];
            if (char.IsHighSurrogate(text[^1])) text = text[..^1];
        }
        return text.Length == 0 ? null : text;
    }
}
internal sealed record AiAccountPreferences(AiAccountProfile[] Profiles)
{
    internal static AiAccountPreferences Read(string directory, ConnectionPreferences legacy)
    {
        try
        {
            var file = Path.Combine(directory, "account-profiles.json");
            if (File.Exists(file))
            {
                var saved = JsonSerializer.Deserialize<AiAccountPreferences>(File.ReadAllText(file), WidgetStorage.Json);
                var profiles = (saved?.Profiles ?? []).Where(p => p is not null && AiServices.ServiceOf(p.Id) == p.Service && p.Service is not null)
                    .DistinctBy(p => p.Id).Select(p => p with
                    {
                        Name = string.IsNullOrWhiteSpace(p.Name) ? AiServices.Name(p.Service) : p.Name[..Math.Min(p.Name.Length, 40)],
                        Nickname = AiAccountProfile.NormalizeNickname(p.Nickname),
                        // Only the original slot of each service may follow its one system CLI login.
                        UseSystem = p.Id == p.Service && p.UseSystem
                    }).ToList();
                EnsurePrimary(profiles, legacy);
                return new(profiles.ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { WidgetStorage.Log("AI profiles restore failed", ex); }
        // Migrate existing Codex slots and the original Claude slot without moving any credentials.
        var migrated = CodexAccountPreferences.Read(directory, legacy).Profiles
            .Select(p => new AiAccountProfile(p.Id, "codex", p.Name, p.Enabled, p.UseSystem)).ToList();
        EnsurePrimary(migrated, legacy);
        return new(migrated.ToArray());
    }

    private static void EnsurePrimary(List<AiAccountProfile> profiles, ConnectionPreferences legacy)
    {
        foreach (var service in AiServices.Catalog)
            if (!profiles.Any(p => p.Id == service.Id)) profiles.Add(new(service.Id, service.Id, service.Name,
                service.Id == "codex" ? legacy.CodexEnabled : legacy.ClaudeEnabled,
                service.Id == "codex" ? legacy.UseSystemCodex : legacy.UseSystemClaude));
    }

    internal AiAccountPreferences Add(string service, out AiAccountProfile profile)
    {
        var name = AiServices.Name(service);
        var number = 2;
        while (Profiles.Any(p => p.Service == service && p.Name == $"{name} {number}")) number++;
        profile = new(service + "-" + Guid.NewGuid().ToString("N"), service, $"{name} {number}");
        return new([.. Profiles, profile]);
    }
    internal AiAccountPreferences Update(AiAccountProfile profile) => new(Profiles.Select(p => p.Id == profile.Id ? profile : p).ToArray());

    internal AiAccountPreferences Remove(string id)
    {
        // Primary slots are required by the host, but must lose every saved-account marker.
        // Extra slots are removed entirely, including entries hidden by older releases.
        return new(Profiles.Where(p => p.Id != id || p.Id == p.Service).Select(p => p.Id == id
            ? new AiAccountProfile(p.Service, p.Service, AiServices.Name(p.Service)) : p).ToArray());
    }

    internal ConnectionPreferences Save(string directory)
    {
        var codex = Profiles.Single(p => p.Id == "codex");
        var claude = Profiles.Single(p => p.Id == "claude");
        var connections = new ConnectionPreferences(codex.UseSystem, claude.UseSystem, codex.Enabled, claude.Enabled);
        WidgetStorage.Write(Path.Combine(directory, "account-profiles.json"), this);
        // Keep legacy registries in sync so an upgrade fallback cannot resurrect deleted tags.
        WidgetStorage.Write(Path.Combine(directory, "codex-accounts.json"), new CodexAccountPreferences(
            Profiles.Where(p => p.Service == "codex").Select(p => new CodexProfile(p.Id, p.Name, p.Enabled, p.UseSystem)).ToArray()));
        WidgetStorage.Write(Path.Combine(directory, "connections.json"), connections);
        return connections;
    }
}
