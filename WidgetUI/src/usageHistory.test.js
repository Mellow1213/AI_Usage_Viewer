import test from 'node:test';
import assert from 'node:assert/strict';
import { aggregateHistory, historyRange } from './usageHistory.js';

test('unobserved, observed zero, and partially observed zero stay distinct', () => {
  const now = new Date(2026, 9, 19, 12).getTime(), range = historyRange('hour', 0, now);
  const start = range.start / 1000;
  const buckets = aggregateHistory({ coverage: [[start, start + 3600], [start + 7200, start + 7500]], points: [] }, range, now);
  assert.equal(buckets[0].status, 'complete'); assert.equal(buckets[0].points, 0);
  assert.equal(buckets[1].status, 'missing'); assert.equal(buckets[1].points, null);
  assert.equal(buckets[2].status, 'partial'); assert.equal(buckets[2].points, 0);
  assert.equal(buckets.at(-1).status, 'future'); assert.equal(buckets.at(-1).points, null);
});

test('receipt timestamps assign consumption once and coverage gaps remain partial', () => {
  const now = new Date(2026, 9, 19, 12).getTime(), range = historyRange('hour', 0, now), start = range.start / 1000;
  const buckets = aggregateHistory({ coverage: [[start, start + 1200], [start + 1800, start + 3600]],
    points: [[start + 1200, 4], [start + 3600, 2]] }, range, now);
  assert.equal(buckets[0].points, 4); assert.equal(buckets[0].covered, 3000000); assert.equal(buckets[0].status, 'partial');
  assert.equal(buckets[1].points, 2); assert.equal(buckets[1].status, 'partial');
  assert.equal(buckets.reduce((sum, bucket) => sum + (bucket.points ?? 0), 0), 6);
});

test('calendar day and Monday-based week aggregation use the same source amounts', () => {
  const now = new Date(2026, 9, 19, 12).getTime(), point = new Date(2026, 9, 18, 11).getTime() / 1000;
  const series = { coverage: [[point - 60, point]], points: [[point, 3]] };
  const day = aggregateHistory(series, historyRange('day', 0, now), now);
  const weekRange = historyRange('week', 0, now), week = aggregateHistory(series, weekRange, now);
  assert.equal(new Date(weekRange.start).getDay(), 1);
  assert.equal(day.find(bucket => bucket.points === 3).label, '10. 18.');
  assert.equal(week.reduce((sum, bucket) => sum + (bucket.points ?? 0), 0), 3);
  assert.equal(new Date(historyRange('day', -1, now).end).getTime(), historyRange('day', 0, now).start);
});

test('overlapping spans do not inflate coverage and retention does not invent zeros', () => {
  const now = new Date(2026, 9, 19, 12).getTime(), range = historyRange('hour', 0, now), start = range.start / 1000;
  const buckets = aggregateHistory({ coverage: [[start, start + 3600], [start, start + 1800]], points: [] }, range, now);
  assert.equal(buckets[0].covered, 3600000);
  const oldRange = historyRange('day', -14, now);
  assert(aggregateHistory({ coverage: [[0, now / 1000]], points: [] }, oldRange, now).every(bucket => bucket.points === null));
});

test('hourly calendar handles 23-hour and 25-hour DST days', () => {
  const previous = process.env.TZ;
  process.env.TZ = 'America/New_York';
  try {
    assert.equal(historyRange('hour', 0, new Date(2026, 2, 8, 12).getTime()).boundaries.length, 24);
    assert.equal(historyRange('hour', 0, new Date(2026, 10, 1, 12).getTime()).boundaries.length, 26);
    const daily = historyRange('day', 0, new Date(2026, 2, 8, 12).getTime());
    assert.equal(daily.end - daily.boundaries.at(-2), 23 * 3600000);
  } finally { if (previous === undefined) delete process.env.TZ; else process.env.TZ = previous; }
});
