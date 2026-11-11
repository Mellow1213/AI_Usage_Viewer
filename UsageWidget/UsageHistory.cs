using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UsageWidget;

// Independent of the widget's resettable measurement totals. All times are Unix seconds.
internal sealed class UsageHistory
{
    internal const int RetentionDays = 90;
    internal sealed class Series
    {
        public string Id { get; set; } = "";
        public string ProviderId { get; set; } = "";
        public string Label { get; set; } = "";
        public string? Plan { get; set; }
        public double StartedAt { get; set; }
        public double LastObservedAt { get; set; }
        public List<double[]> Points { get; set; } = [];
        public List<double[]> Coverage { get; set; } = [];
    }
    private sealed record Baseline(string SeriesId, double Used, long? Reset, DateTimeOffset At);
    private readonly string _path;
    private DateTimeOffset _startedAt;
    private readonly Dictionary<string, DateTimeOffset> _barriers = [];
    private readonly Dictionary<string, Baseline> _baselines = [];
    private List<Series> _series = [];
    private string? _error;
    private DateTimeOffset _lastPruned;

    internal UsageHistory(string directory, DateTimeOffset? startedAt = null)
    {
        _path = Path.Combine(directory, "usage-history.json");
        _startedAt = startedAt ?? DateTimeOffset.UtcNow;
        try
        {
            if (File.Exists(_path)) _series = JsonSerializer.Deserialize<List<Series>>(File.ReadAllText(_path), WidgetStorage.Json) ?? [];
            if (_series.Any(s => s is null || string.IsNullOrEmpty(s.Id) || string.IsNullOrEmpty(s.ProviderId)
                || s.Points is null || s.Coverage is null || s.Points.Any(p => p is null || p.Length != 2 || !double.IsFinite(p[0]) || !double.IsFinite(p[1]) || p[1] < 0)
                || s.Coverage.Any(c => c is null || c.Length != 2 || !double.IsFinite(c[0]) || !double.IsFinite(c[1]) || c[1] < c[0])))
                throw new JsonException("Invalid history record");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Preserve an unreadable file instead of overwriting the user's history.
            _error = "사용 기록을 읽지 못했습니다. 기존 파일을 보존하고 있습니다.";
            _series = [];
            WidgetStorage.Log("Usage history restore failed", ex);
        }
    }

    internal void Observe(ProviderUsage usage, string? identity, DateTimeOffset now)
    {
        if (_error is not null) return;
        var prefix = usage.Id + ":";
        void BreakObservation()
        {
            foreach (var key in _baselines.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray()) _baselines.Remove(key);
            _barriers[usage.Id] = now;
        }
        if (identity is null || usage.Status != "ready" || usage.UpdatedAt is not { } sample || sample < _startedAt
            || _barriers.TryGetValue(usage.Id, out var barrier) && sample < barrier || sample > now || now - sample > TimeSpan.FromMinutes(10))
        { BreakObservation(); return; }
        var changed = false;
        var present = usage.Windows.Select(q => prefix + q.Key).ToHashSet();
        foreach (var key in _baselines.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !present.Contains(k)).ToArray()) _baselines.Remove(key);
        foreach (var quota in usage.Windows)
        {
            var key = prefix + quota.Key;
            if (!double.IsFinite(quota.UsedPercent) || quota.UsedPercent < 0 || quota.ResetsAt <= sample.ToUnixTimeSeconds())
            { _baselines.Remove(key); continue; }
            var id = SeriesId(usage.Id, identity, quota);
            var used = Math.Clamp(quota.UsedPercent, 0, 100);
            _baselines.TryGetValue(key, out var previous);
            if (previous is not null && sample <= previous.At) continue; // Never turn cached data into coverage.
            var series = _series.FirstOrDefault(s => s.Id == id);
            var at = sample.ToUnixTimeMilliseconds() / 1000d;
            if (series is null)
            {
                series = new() { Id = id, ProviderId = usage.Id, Label = quota.Label, Plan = usage.Plan, StartedAt = at };
                _series.Add(series);
            }
            series.LastObservedAt = at;
            var continuous = previous is not null && previous.SeriesId == id && sample - previous.At <= TimeSpan.FromMinutes(10);
            var reset = previous is not null && ((used == 0 && previous.Used > 0)
                || previous.Reset is { } old && (old <= sample.ToUnixTimeSeconds()
                    || quota.ResetsAt is { } next && used < previous.Used && Math.Abs((double)next - old) >= 60));
            if (continuous && !reset)
            {
                var from = previous!.At.ToUnixTimeMilliseconds() / 1000d;
                if (series.Coverage.LastOrDefault() is { } tail && tail[1] == from) tail[1] = at;
                else series.Coverage.Add([from, at]);
                var delta = Math.Max(0, used - previous.Used);
                if (delta > 0) series.Points.Add([at, delta]);
                used = Math.Max(used, previous.Used); // Refunds/rounding never count consumption twice.
            }
            _baselines[key] = new(id, used, quota.ResetsAt, sample);
            changed = true;
        }
        if (now - _lastPruned >= TimeSpan.FromHours(1)) { Prune(now); _lastPruned = now; changed = true; }
        if (changed)
        {
            try { WidgetStorage.Write(_path, _series); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { WidgetStorage.Log("Usage history save failed", ex); _error = "사용 기록을 저장하지 못했습니다. 저장 폴더를 확인해주세요."; }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddDays(-RetentionDays).ToUnixTimeMilliseconds() / 1000d;
        foreach (var series in _series)
        {
            series.Points.RemoveAll(p => p[0] < cutoff);
            series.Coverage.RemoveAll(c => c[1] <= cutoff);
            foreach (var span in series.Coverage) span[0] = Math.Max(span[0], cutoff);
        }
        _series.RemoveAll(s => s.LastObservedAt < cutoff);
    }

    internal object Snapshot(IEnumerable<string> providers) => new
    {
        retentionDays = RetentionDays, error = _error,
        series = _series.Where(s => providers.Contains(s.ProviderId)).ToArray()
    };
    private static string SeriesId(string providerId, string identity, QuotaWindow quota) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{providerId}\n{identity}\n{quota.Key}\n{quota.Label}")));

    internal ProviderUsage WithTrends(ProviderUsage usage, string? identity, DateTimeOffset now)
    {
        // Bound the card payload to the longest rolling window plus one hour of clock headroom.
        // Keep original span endpoints so identical snapshots do not trigger new UI messages every tick.
        var cutoff = now.AddHours(-25).ToUnixTimeMilliseconds() / 1000d;
        return usage with { Windows = usage.Windows.Select(quota =>
        {
            var series = identity is null || _error is not null ? null : _series.FirstOrDefault(s => s.Id == SeriesId(usage.Id, identity, quota));
            return quota with { Trend = new UsageTrend(
                series?.Points.Where(p => p[0] > cutoff).Select(p => p.ToArray()).ToArray() ?? [],
                series?.Coverage.Where(c => c[1] > cutoff).Select(c => c.ToArray()).ToArray() ?? [], _error) };
        }).ToArray() };
    }
    internal IReadOnlyList<Series> Records => _series;
    internal void RetainProviders(IEnumerable<string> providers, DateTimeOffset? now = null)
    {
        foreach (var key in _baselines.Keys.Where(k => !providers.Contains(k.Split(':')[0])).ToArray())
        { _barriers[key.Split(':')[0]] = now ?? DateTimeOffset.UtcNow; _baselines.Remove(key); }
    }
    internal void Interrupt(DateTimeOffset? now = null) { _baselines.Clear(); _startedAt = now ?? DateTimeOffset.UtcNow; }
}
