// Calendar buckets use the viewer's local time. UTC source timestamps survive DST/time-zone changes.
export const historyModes = [{ id: 'day', label: '일별' }, { id: 'hour', label: '시간별' }, { id: 'week', label: '주별' }];
const midnight = value => { const date = new Date(value); date.setHours(0, 0, 0, 0); return date; };
const addDays = (value, count) => { const date = new Date(value); date.setDate(date.getDate() + count); return date; };
const dateText = value => new Date(value).toLocaleDateString('ko-KR', { month: 'numeric', day: 'numeric' });

export function historyRange(mode, offset = 0, now = Date.now()) {
  const today = midnight(now);
  let start, end, boundaries;
  if (mode === 'hour') {
    start = addDays(today, offset); end = addDays(start, 1); boundaries = [+start];
    // Advance absolute hours so both repeated hours on a fall-back day remain visible.
    for (let time = +start + 3600000; time < +end; time += 3600000) boundaries.push(time);
    boundaries.push(+end);
  } else if (mode === 'week') {
    const monday = addDays(today, -((today.getDay() + 6) % 7));
    start = addDays(monday, -49 + offset * 56); end = addDays(start, 56);
    boundaries = Array.from({ length: 9 }, (_, i) => +addDays(start, i * 7));
  } else {
    start = addDays(today, -6 + offset * 7); end = addDays(start, 7);
    boundaries = Array.from({ length: 8 }, (_, i) => +addDays(start, i));
  }
  return { start: +start, end: +end, boundaries,
    title: mode === 'hour' ? new Date(start).toLocaleDateString('ko-KR', { year: 'numeric', month: 'long', day: 'numeric' })
      : `${dateText(start)} – ${dateText(+end - 1)}` };
}

export function aggregateHistory(series, range, now = Date.now()) {
  const cutoff = now - 90 * 86400000;
  return range.boundaries.slice(0, -1).map((start, i) => {
    const end = range.boundaries[i + 1], until = Math.min(end, now);
    const from = Math.max(start, cutoff);
    let points = 0, covered = 0;
    for (const [at, amount] of series?.points ?? []) {
      const time = at * 1000;
      if (time >= from && time < end && time <= now) points += amount;
    }
    // Merge clipped spans defensively: overlapping coverage must never inflate completeness.
    const spans = (series?.coverage ?? []).map(([a, b]) => [Math.max(from, a * 1000), Math.min(until, b * 1000)])
      .filter(([a, b]) => b > a).sort((a, b) => a[0] - b[0]);
    let tail = -Infinity;
    for (const [a, b] of spans) { covered += Math.max(0, b - Math.max(a, tail)); tail = Math.max(tail, b); }
    const elapsed = Math.max(0, until - start);
    const status = start >= now ? 'future' : covered === 0 && points === 0 ? 'missing'
      : covered >= elapsed - 1 && start >= cutoff ? 'complete' : 'partial';
    const hour = new Date(start).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
    return { start, end, points: status === 'missing' || status === 'future' ? null : points, covered, elapsed, status,
      label: end - start <= 3600000 ? hour : dateText(start),
      description: `${new Date(start).toLocaleString('ko-KR', { timeZoneName: 'short' })} – ${new Date(end).toLocaleString('ko-KR', { timeZoneName: 'short' })}` };
  });
}

export const formatPoints = points => points.toLocaleString('ko-KR', { maximumFractionDigits: 1 });
export const coverageText = bucket => bucket.status === 'future' ? '아직 오지 않은 구간' : bucket.status === 'missing' ? '미관측'
  : `${bucket.status === 'partial' ? '일부 관측' : '관측됨'} · ${Math.floor(bucket.covered / 3600000)}시간 ${Math.floor(bucket.covered / 60000) % 60}분`;

// Used only by the explicitly marked browser design preview.
export function previewHistory(now = Date.now()) {
  const start = +midnight(now), coverage = [], points = [];
  for (let day = -12; day <= 0; day++) {
    if (day === -4 || day === -9) continue;
    const from = +addDays(start, day) + 9 * 3600000, to = Math.min(now, from + (day === -2 ? 3 : 9) * 3600000);
    if (to <= from) continue;
    coverage.push([from / 1000, to / 1000]);
    if (day !== -3) points.push([(to - 60000) / 1000, Math.abs(day * 7 + 13) % 22 + 2]);
  }
  return { retentionDays: 90, series: [{ id: 'preview', providerId: 'codex', label: '주간', startedAt: (start - 12 * 86400000) / 1000, lastObservedAt: now / 1000, coverage, points }] };
}
