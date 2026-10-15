using System.IO;
using UsageWidget;
using Velopack;

internal static partial class Program
{
    internal const string UpdateVerificationPackage = "Mellow.AIUsageViewer.UpdateVerification";
    private static int RunPackagedUpdateScenario(string[] args)
    {
        var mode = args[1];
        var profile = args[2];
        var source = args[3];
        Directory.CreateDirectory(profile);
        try
        {
            var backend = new VelopackUpdateBackend(profile, new UpdateManager(source), UpdateVerificationPackage);
            using var updater = new WidgetUpdater(profile, backend);
            if (!backend.IsInstalled) throw new Exception("Fixture was not installed by Velopack.");
            if (backend.CurrentVersion == "1.1.1")
            {
                WidgetStorage.Write(Path.Combine(profile, "applied.json"), new { currentVersion = backend.CurrentVersion, installed = backend.IsInstalled });
                return 0;
            }
            if (mode == "apply")
            {
                if (updater.State.Phase != "ready") throw new Exception("Downloaded update did not survive process restart.");
                WidgetStorage.Write(Path.Combine(profile, "pending-restored.json"), updater.State);
                if (!updater.PrepareRestart()) throw new Exception("Could not queue the real updater.");
                return 0; // Update.exe waits for this process to exit before replacing the files.
            }
            updater.CheckAsync().GetAwaiter().GetResult();
            if (updater.State.Phase != "available") throw new Exception("Fixture did not find version 1.1.1.");
            updater.DownloadAsync().GetAwaiter().GetResult();
            if (mode == "corrupt")
            {
                if (updater.State.Phase != "error" || updater.State.CanApply) throw new Exception("Corrupted package was accepted.");
                WidgetStorage.Write(Path.Combine(profile, "corrupt-rejected.json"), updater.State);
            }
            else
            {
                if (updater.State.Phase != "ready") throw new Exception("Real download or package verification failed.");
                WidgetStorage.Write(Path.Combine(profile, "downloaded.json"), updater.State);
            }
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(profile, "failure.txt"), ex.ToString());
            return 1;
        }
    }
}
