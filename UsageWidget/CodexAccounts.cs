using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal sealed record CodexProfile(string Id, string Name, bool Enabled = false, bool UseSystem = false);

internal sealed record CodexAccountPreferences(CodexProfile[] Profiles)
{
    internal static bool IsCodexId(string? id) => id == "codex" ||
        id is not null && id.StartsWith("codex-", StringComparison.Ordinal) && Guid.TryParseExact(id[6..], "N", out _);

    internal static CodexAccountPreferences Read(string directory, ConnectionPreferences legacy)
    {
        var primary = new CodexProfile("codex", "Codex", legacy.CodexEnabled, legacy.UseSystemCodex);
        try
        {
            var path = Path.Combine(directory, "codex-accounts.json");
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<CodexAccountPreferences>(File.ReadAllText(path), WidgetStorage.Json);
                var profiles = (saved?.Profiles ?? []).Where(p => p is not null && IsCodexId(p.Id))
                    .DistinctBy(p => p.Id).Select(p => p with
                    {
                        Name = string.IsNullOrWhiteSpace(p.Name) ? "Codex" : p.Name[..Math.Min(p.Name.Length, 40)],
                        // A system CLI follows one current login; only the legacy primary slot may share it.
                        UseSystem = p.Id == "codex" && p.UseSystem
                    }).ToList();
                if (!profiles.Any(p => p.Id == "codex")) profiles.Insert(0, primary);
                return new(profiles.ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { WidgetStorage.Log("Codex profiles restore failed", ex); }
        return new([primary]);
    }

    internal CodexAccountPreferences Add(out CodexProfile profile)
    {
        var number = 2;
        while (Profiles.Any(p => p.Name == $"Codex {number}")) number++;
        profile = new("codex-" + Guid.NewGuid().ToString("N"), $"Codex {number}");
        return new([.. Profiles, profile]);
    }

    internal CodexAccountPreferences Update(CodexProfile profile) =>
        new(Profiles.Select(p => p.Id == profile.Id ? profile : p).ToArray());
}

internal sealed class CodexSession : IDisposable
{
    internal CodexProfile Profile { get; set; }
    internal CodexClient Client { get; private set; }
    internal AccountConnection Account { get; set; }
    internal ProviderUsage Usage { get; set; }
    private readonly string _directory;
    private readonly Func<CodexProfile, CodexClient>? _clientFactory;

    internal CodexSession(string directory, CodexProfile profile, Func<CodexProfile, CodexClient>? clientFactory = null)
    {
        _directory = directory;
        _clientFactory = clientFactory;
        Profile = profile;
        Client = CreateClient();
        Account = new(profile.Id, profile.UseSystem ? "system" : "widget", "disconnected");
        Usage = new(profile.Id, "disconnected", "ChatGPT 계정으로 로그인해주세요.", []);
    }

    private CodexClient CreateClient() => _clientFactory?.Invoke(Profile)
        ?? new(Profile.UseSystem ? null : AccountProfiles.CodexHome(_directory, Profile.Id));
    internal void RestartClient() { Client.Dispose(); Client = CreateClient(); }
    public void Dispose() => Client.Dispose();
}
