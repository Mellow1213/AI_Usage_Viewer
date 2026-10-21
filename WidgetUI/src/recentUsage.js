// Points are received percentage-point changes; coverage marks observed intervals.
export function recentUsage(trend, minutes, now = Date.now(), count = 24) {
  const end = now, start = end - minutes * 60000;
  const points = (trend?.points ?? []).filter(([at, value]) => Number.isFinite(at) && Number.isFinite(value)
    && value >= 0 && at * 1000 > start && at * 1000 <= end).map(([at, value]) => [at * 1000, value]);
  const spans = [];
  for (const [from, to] of (trend?.coverage ?? []).map(([a, b]) => [Math.max(start, a * 1000), Math.min(end, b * 1000)])
    .filter(([a, b]) => Number.isFinite(a) && Number.isFinite(b) && b > a).sort((a, b) => a[0] - b[0])) {
    const tail = spans.at(-1);
    if (tail && from <= tail[1]) tail[1] = Math.max(tail[1], to);
    else spans.push([from, to]);
  }
  const covered = spans.reduce((sum, [a, b]) => sum + b - a, 0);
  const observed = covered > 0 || points.length > 0;
  const status = trend?.error ? 'error' : !observed ? 'missing' : covered >= end - start - 1 ? 'complete' : 'partial';
  const buckets = Array.from({ length: count }, (_, i) => {
    const from = start + (end - start) * i / count, to = start + (end - start) * (i + 1) / count;
    const coverage = spans.map(([a, b]) => [Math.max(a, from), Math.min(b, to)]).filter(([a, b]) => b > a);
    const events = points.filter(([at]) => at > from && at <= to);
    const value = events.reduce((sum, [, amount]) => sum + amount, 0);
    const measured = coverage.length > 0 || events.length > 0;
    return { start: from, end: to, coverage, points: trend?.error || !measured ? null : value,
      events, partial: coverage.reduce((sum, [a, b]) => sum + b - a, 0) < to - from - 1 };
  });
  return { start, end, covered, status, buckets, total: status === 'missing' || status === 'error' ? null
    : points.reduce((sum, [, amount]) => sum + amount, 0) };
}
