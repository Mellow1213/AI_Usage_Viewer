using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageWidget;

internal sealed class ClaudeBridge(string dataDirectory, string? settingsFile = null)
{
    internal string SettingsFile { get; } = settingsFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
    private string MetadataFile => Path.Combine(dataDirectory, "claude-bridge.json");
    private string ScriptFile => Path.Combine(dataDirectory, "claude-statusline.ps1");
    internal string SnapshotFile => Path.Combine(dataDirectory, "claude-usage.json");
    private string Command => $"powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptFile}\"";

    internal bool Installed
    {
        get
        {
            try { return ReadSettings()["statusLine"]?["command"]?.GetValue<string>() == Command; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
        }
    }

    internal void Install()
    {
        if (Installed) return;
        var settings = ReadSettings();
        var previous = settings["statusLine"]?.DeepClone();
        if (previous is not null && previous is not JsonObject)
            throw new InvalidOperationException("Unsupported Claude status line configuration.");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        var script = Path.Combine(AppContext.BaseDirectory, "Bridge", "claude-statusline.ps1");
        File.Copy(script, ScriptFile, true);
        // Keep a full backup, and also the previous statusLine for a focused uninstall.
        if (File.Exists(SettingsFile)) File.Copy(SettingsFile, Path.Combine(dataDirectory, $"claude-settings-backup-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json"));
        WidgetStorage.Write(MetadataFile, new { previousStatusLine = previous, installedCommand = Command });
        var wrapper = previous as JsonObject ?? new JsonObject();
        wrapper["type"] = "command";
        wrapper["command"] = Command;
        settings["statusLine"] = wrapper;
        WriteSettings(settings);
    }

    internal void Uninstall()
    {
        if (!Installed || !File.Exists(MetadataFile)) return;
        var settings = ReadSettings();
        var metadata = JsonNode.Parse(File.ReadAllText(MetadataFile)) as JsonObject
            ?? throw new JsonException("Invalid bridge metadata.");
        var previous = metadata["previousStatusLine"]?.DeepClone();
        if (previous is null) settings.Remove("statusLine"); else settings["statusLine"] = previous;
        WriteSettings(settings);
    }

    internal ProviderUsage Read()
    {
        if (!Installed) return new("claude", "disconnected", "Claude Code 사용량을 연결해주세요.", []);
        if (!File.Exists(SnapshotFile)) return new("claude", "waiting", "Claude Code 응답 후 사용량이 표시됩니다.", []);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SnapshotFile));
            return UsageParser.Claude(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new("claude", "error", "사용량 파일을 읽지 못했습니다. 다음 응답을 기다려주세요.", []);
        }
    }

    internal void SaveUsage(ProviderUsage usage)
    {
        if (usage.Status != "ready" || usage.UpdatedAt is null) return;
        // Store only quota fields, as with statusLine; never SDK session data.
        var limits = usage.Windows.Where(w => w.Key is "five_hour" or "seven_day")
            .ToDictionary(w => w.Key, w => new { used_percentage = w.UsedPercent, resets_at = w.ResetsAt });
        WidgetStorage.Write(SnapshotFile, new { updatedAt = usage.UpdatedAt, source = "cli-query", rate_limits = limits });
    }

    private JsonObject ReadSettings() => File.Exists(SettingsFile)
        ? JsonNode.Parse(File.ReadAllText(SettingsFile), documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("Claude settings must be an object.")
        : new JsonObject();
    private void WriteSettings(JsonObject settings)
    {
        var temp = SettingsFile + ".widget.tmp";
        File.WriteAllText(temp, settings.ToJsonString(WidgetStorage.Json));
        File.Move(temp, SettingsFile, true);
    }
}
