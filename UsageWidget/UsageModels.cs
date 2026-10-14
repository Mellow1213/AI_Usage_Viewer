using System.Text.Json;

namespace UsageWidget;

internal sealed record QuotaWindow(string Key, string Label, double UsedPercent, long? ResetsAt, DailyUsage? Today = null)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

internal sealed record ProviderUsage(string Id, string Status, string Message, IReadOnlyList<QuotaWindow> Windows,
    DateTimeOffset? UpdatedAt = null, string? Plan = null, string? Name = null, string? Service = null, string? DefaultName = null);

internal static class UsageParser
{
    internal static ProviderUsage Codex(JsonElement result)
    {
        var limits = result.TryGetProperty("rateLimitsByLimitId", out var buckets)
                     && buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out var codex)
            ? codex : result.TryGetProperty("rateLimits", out var legacy) ? legacy : default;
        if (limits.ValueKind != JsonValueKind.Object)
            return new("codex", "unavailable", "이 계정의 구독 한도 정보가 없습니다.", []);
        var windows = new List<QuotaWindow>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (!TryPercentage(window, "usedPercent", out var used)) continue;
            var minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.TryGetInt32(out var value) ? value : 0;
            windows.Add(new(key, minutes switch { 300 => "5시간", 10080 => "주간", > 0 => $"{minutes / 60d:0.#}시간", _ => "한도" }, used, Timestamp(window, "resetsAt")));
        }
        var plan = limits.TryGetProperty("planType", out var planType) && planType.ValueKind == JsonValueKind.String ? planType.GetString() : null;
        return new("codex", windows.Count > 0 ? "ready" : "unavailable",
            windows.Count > 0 ? "계정 사용량" : "이 계정은 잔여 비율을 제공하지 않습니다.", windows, DateTimeOffset.UtcNow, plan);
    }

    internal static ProviderUsage Claude(JsonElement snapshot)
    {
        var windows = new List<QuotaWindow>();
        var updated = snapshot.TryGetProperty("updatedAt", out var at) && at.TryGetDateTimeOffset(out var parsed) ? parsed : (DateTimeOffset?)null;
        if (snapshot.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, label) in new[] { ("five_hour", "5시간"), ("seven_day", "주간") })
            {
                if (!limits.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) continue;
                if (!TryPercentage(window, "used_percentage", out var used)) continue;
                windows.Add(new(key, label, used, Timestamp(window, "resets_at")));
            }
        }
        return new("claude", windows.Count > 0 ? "ready" : "waiting",
            windows.Count > 0 ? snapshot.TryGetProperty("source", out var source) && source.GetString() == "cli-query" ? "Claude 사용량 조회" : "Claude Code에서 수신" : "Claude Code 응답 후 사용량이 표시됩니다.", windows, updated);
    }

    internal static ProviderUsage ClaudeControl(JsonElement result)
    {
        var windows = new List<QuotaWindow>();
        if (result.TryGetProperty("rate_limits_available", out var available) && available.ValueKind == JsonValueKind.True
            && result.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, label) in new[] { ("five_hour", "5시간"), ("seven_day", "주간") })
            {
                if (!limits.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object
                    || !TryPercentage(window, "utilization", out var used)) continue;
                var reset = window.TryGetProperty("resets_at", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var time)
                    ? time.ToUnixTimeSeconds() : (long?)null;
                windows.Add(new(key, label, used, reset));
            }
        }
        // The SDK returns utilization as 0-100, unlike some model response events.
        return new("claude", windows.Count > 0 ? "ready" : "unavailable", windows.Count > 0 ? "Claude 사용량 조회" : "이 계정의 구독 한도 정보를 조회할 수 없습니다.",
            windows, windows.Count > 0 ? DateTimeOffset.UtcNow : null);
    }

    private static bool TryPercentage(JsonElement element, string key, out double value)
    {
        value = 0;
        return element.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value) && double.IsFinite(value) && value >= 0;
    }
    private static long? Timestamp(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            && result >= 0 && result <= 253402300799 ? result : null;
}
