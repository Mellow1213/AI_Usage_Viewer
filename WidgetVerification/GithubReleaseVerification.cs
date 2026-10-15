using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UsageWidget;
using Velopack;
using Velopack.Locators;

internal static partial class Program
{
    private static async Task<int> VerifyGithubReleaseAsync(string[] args)
    {
        var version = args[1];
        var directory = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(directory);
        try
        {
            // Simulate an older installed version without accessing the real app or account directory.
            var packages = Path.Combine(directory, "packages");
            Directory.CreateDirectory(packages);
            var locator = new TestVelopackLocator(VelopackUpdateBackend.PackageId, "1.1.0", packages);
            var prerelease = VelopackUpdateBackend.IncludesPrereleases(version);
            var manager = new UpdateManager(VelopackUpdateBackend.CreateSource(null, prerelease), locator: locator);
            var backend = new VelopackUpdateBackend(directory, manager);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var release = await backend.CheckAsync(timeout.Token);
            if (release?.Version != version) throw new Exception($"Expected public release {version}; found {release?.Version ?? "none"}.");
            await backend.DownloadAsync(release, _ => { }, timeout.Token);
            var asset = ((UpdateInfo)release.Package).TargetFullRelease;
            var file = Path.Combine(packages, asset.FileName);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, timeout.Token)));
            if (hash != asset.SHA256 || new FileInfo(file).Length != asset.Size) throw new Exception("Downloaded public package size/SHA256 differs from its feed.");
            // Also exercise the static migration endpoint consumed by the original 1.1.0 app.
            var legacy = new UpdateManager(VelopackUpdateBackend.Repository + "/releases/latest/download", locator: locator);
            var legacyUpdate = await legacy.CheckForUpdatesAsync().WaitAsync(timeout.Token);
            var stableManager = new UpdateManager(VelopackUpdateBackend.CreateSource(null), locator: locator);
            var stableUpdate = await stableManager.CheckForUpdatesAsync().WaitAsync(timeout.Token);
            var stableVersion = stableUpdate?.TargetFullRelease.Version.ToString();
            if (stableVersion is null || legacyUpdate?.TargetFullRelease.Version.ToString() != stableVersion)
                throw new Exception("The stable provider and 1.1.0 migration endpoint disagree.");
            if (prerelease ? VelopackUpdateBackend.IncludesPrereleases(stableVersion) || stableVersion == version : stableVersion != version)
                throw new Exception("Stable installations must not receive beta releases.");
            File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new {
                success = true, version, prerelease, stableVersion, package = asset.FileName, sha256 = hash,
                anonymousGithubCheck = true, anonymousDownloadVerified = true, legacyEndpointVerified = true,
                installedAppAndAccountsUntouched = true
            }, WidgetStorage.Json));
            Console.WriteLine($"PASS: anonymous GitHub check, download, SHA256 and 1.1.0 migration endpoint ({version})");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "failure.txt"), ex.ToString());
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
