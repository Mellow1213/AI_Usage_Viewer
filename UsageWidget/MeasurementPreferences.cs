using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal sealed record MeasurementPreferences(int IntervalMinutes = 30, int WindowMinutes = 30)
{
    internal const int MinimumIntervalMinutes = 1;
    internal const int MaximumIntervalMinutes = 180;
    internal static readonly int[] AllowedWindowMinutes = [30, 60, 180, 360, 720, 1440];
    internal static bool ValidInterval(int minutes) => minutes is >= MinimumIntervalMinutes and <= MaximumIntervalMinutes;
    internal static MeasurementPreferences Read(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "measurement.json");
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<MeasurementPreferences>(File.ReadAllText(path), WidgetStorage.Json);
                if (saved is not null)
                {
                    // Keep the user's comparison window when migrating older
                    // 6h/12h/1d aggregation settings into the new 1m-3h range.
                    return new(Math.Clamp(saved.IntervalMinutes, MinimumIntervalMinutes, MaximumIntervalMinutes),
                        AllowedWindowMinutes.Contains(saved.WindowMinutes) ? saved.WindowMinutes : 30);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { WidgetStorage.Log("Measurement preferences restore failed", ex); }
        return new();
    }
}
