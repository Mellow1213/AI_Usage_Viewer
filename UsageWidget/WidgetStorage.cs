using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal static class WidgetStorage
{
    internal static readonly string DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageWidget");
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, path, true);
    }
    internal static void Log(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, "widget.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message} {exception?.GetType().Name}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
