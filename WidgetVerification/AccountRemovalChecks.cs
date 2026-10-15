using System.IO;
using UsageWidget;

internal static class AccountRemovalChecks
{
    internal static void Run(string output, Action<bool, string> check)
    {
        foreach (var service in AiServices.Catalog)
        {
            var directory = Path.Combine(output, service.Id + "-" + Guid.NewGuid().ToString("N"));
            var legacy = new ConnectionPreferences(true, true, true, true);
            var profiles = AiAccountPreferences.Read(directory, legacy);
            var primary = profiles.Profiles.Single(p => p.Id == service.Id);
            profiles = profiles.Update(primary with { Nickname = "삭제할 별명" });
            profiles = profiles.Add(service.Id, out var removed);
            profiles = profiles.Update(removed with { Enabled = true, Nickname = "삭제할 추가 계정" });
            profiles = profiles.Add(service.Id, out var remaining);
            profiles = profiles.Update(remaining with { Enabled = true, Nickname = "유지할 계정" });
            profiles.Save(directory);
            var survivors = profiles.Profiles.Where(p => p.Id != removed.Id).ToArray();
            var home = service.Id == "codex" ? AccountProfiles.CodexHome(directory, removed.Id) : AccountProfiles.ClaudeHome(directory, removed.Id);
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "fixture-login"), "preserved-login");
            File.WriteAllText(Path.Combine(directory, "fixture-history.json"), "preserved-history");
            // A stale display list must also discard an account missing from the registry.
            WidgetStorage.Write(Path.Combine(directory, "dashboard.json"), new DashboardPreferences([service.Id, removed.Id, remaining.Id]));

            profiles = profiles.Remove(removed.Id);
            profiles.Save(directory);
            var restored = AiAccountPreferences.Read(directory, AccountProfiles.ReadPreferences(directory));
            check(restored.Profiles.SequenceEqual(survivors), $"{service.Name}: deleting an extra account persists its absence and preserves every other profile");
            check(!DashboardPreferences.Read(directory, legacy).ProviderIds.Contains(removed.Id), $"{service.Name}: a stale dashboard cannot restore the removed account");
            check(!CodexAccountPreferences.Read(directory, legacy).Profiles.Any(p => p.Id == removed.Id), $"{service.Name}: legacy account metadata cannot restore the removed tag");
            check(File.ReadAllText(Path.Combine(home, "fixture-login")) == "preserved-login"
                && File.ReadAllText(Path.Combine(directory, "fixture-history.json")) == "preserved-history", $"{service.Name}: deleting an app entry preserves login files and local history");
            var replacementProfiles = restored.Add(service.Id, out var replacement);
            check(replacement.Name == removed.Name && replacement.Id != removed.Id && !replacement.Enabled && replacement.Nickname is null,
                $"{service.Name}: re-adding a deleted account creates a fresh ID without old connection state or nickname");

            // Old releases can leave registered accounts without visible cards.
            replacementProfiles.Save(directory);
            replacementProfiles.Remove(replacement.Id).Save(directory);
            check(!AiAccountPreferences.Read(directory, legacy).Profiles.Any(p => p.Id == replacement.Id), $"{service.Name}: an already hidden account can be permanently removed");
            profiles = restored.Remove(service.Id);
            profiles.Save(directory);
            restored = AiAccountPreferences.Read(directory, legacy);
            check(restored.Profiles.Single(p => p.Id == service.Id) == new AiAccountProfile(service.Id, service.Id, service.Name),
                $"{service.Name}: deleting the primary account clears its nickname, enabled flag and system-login source");
            check(restored.Profiles.Where(p => p.Id != service.Id).SequenceEqual(profiles.Profiles.Where(p => p.Id != service.Id)),
                $"{service.Name}: deleting the primary account preserves sibling and other-service profiles");
            var connections = AccountProfiles.ReadPreferences(directory);
            check(service.Id == "codex" ? !connections.CodexEnabled && !connections.UseSystemCodex && connections.ClaudeEnabled
                : !connections.ClaudeEnabled && !connections.UseSystemClaude && connections.CodexEnabled,
                $"{service.Name}: primary deletion clears only that service's legacy connection flags");
            // Exercise the migration path with the common registry absent.
            File.Delete(Path.Combine(directory, "account-profiles.json"));
            var migrated = AiAccountPreferences.Read(directory, connections);
            check(!migrated.Profiles.Any(p => p.Id == removed.Id)
                && migrated.Profiles.Single(p => p.Id == service.Id) is { Enabled: false, UseSystem: false, Nickname: null },
                $"{service.Name}: legacy migration does not resurrect deleted primary or extra tags");
            check(profiles.Remove("missing-account").Profiles.SequenceEqual(profiles.Profiles), $"{service.Name}: repeated or stale removal leaves remaining accounts intact");
        }
    }
}
