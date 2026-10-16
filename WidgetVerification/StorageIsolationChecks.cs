using System;
using System.IO;
using UsageWidget;

internal static class StorageIsolationChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var previous = WidgetStorage.LogDirectory;
        var production = Path.Combine(root, "production-fixture");
        var development = Path.Combine(root, "development-fixture");
        try
        {
            Directory.CreateDirectory(production);
            var productionLog = Path.Combine(production, "widget.log");
            File.WriteAllText(productionLog, "production-sentinel");
            WidgetStorage.UseDataDirectory(development);
            WidgetStorage.Log("development-only", new IOException("must-not-be-logged"));
            var log = Path.Combine(development, "widget.log");
            check(File.ReadAllText(productionLog) == "production-sentinel", "Development errors preserve the other profile's log");
            check(File.ReadAllText(log).Contains("development-only IOException") && !File.ReadAllText(log).Contains("must-not-be-logged"),
                "Errors use the selected profile without exposing exception details");
            File.WriteAllText(log, new string('x', 512001));
            WidgetStorage.Log("rotated-development");
            check(File.Exists(log + ".old") && File.ReadAllText(log).Contains("rotated-development")
                && !File.Exists(productionLog + ".old"), "Log rotation stays in the selected profile");
            WidgetStorage.Write(Path.Combine(production, "connections.json"), new ConnectionPreferences(false, false, true, true));
            check(!AccountProfiles.ReadPreferences(development).CodexEnabled && !AccountProfiles.ReadPreferences(development).ClaudeEnabled
                && DashboardPreferences.Read(development, new()).ProviderIds.Length == 0,
                "A fresh development profile does not inherit connected production accounts or cards");
        }
        finally { WidgetStorage.UseDataDirectory(previous); }
    }
}
