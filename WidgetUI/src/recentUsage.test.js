import test from 'node:test';
import assert from 'node:assert/strict';
import { recentUsage } from './recentUsage.js';

const now = Date.UTC(2026, 9, 21, 10, 10);
const at = minutes => (now + minutes * 60000) / 1000;
test('window changes immediately use now, including observations after the last scheduled measurement', () => {
  const trend = { points: [[at(-70), 3], [at(-40), 5], [at(-5), 2]], coverage: [[at(-90), at(-1)]] };
  assert.equal(recentUsage(trend, 30, now).total, 2);
  assert.equal(recentUsage(trend, 60, now).total, 7);
  assert.equal(recentUsage(trend, 180, now).total, 10);
  assert.equal(recentUsage(trend, 60, now).end, now);
  assert.equal(recentUsage(trend, 60, now).start, now - 3600000);
});
test('unobserved windows are blank while observed zero has a baseline', () => {
  const missing = recentUsage({ points: [], coverage: [] }, 30, now);
  assert.equal(missing.total, null);
  assert.equal(missing.status, 'missing');
  assert.ok(missing.buckets.every(bucket => bucket.points === null && !bucket.coverage.length));
  const zero = recentUsage({ points: [], coverage: [[at(-30), at(0)]] }, 30, now);
  assert.equal(zero.total, 0);
  assert.equal(zero.status, 'complete');
  assert.ok(zero.buckets.every(bucket => bucket.points === 0 && bucket.coverage.length));
});
test('gaps stay blank, including gaps inside one graph bucket', () => {
  const trend = { points: [[at(-26), 2], [at(-3), 1]], coverage: [[at(-29), at(-25)], [at(-4), at(-1)]] };
  const view = recentUsage(trend, 30, now, 3);
  assert.equal(view.total, 3);
  assert.equal(view.status, 'partial');
  assert.equal(view.buckets[1].points, null);
  assert.deepEqual(view.buckets[0].coverage, [[at(-29) * 1000, at(-25) * 1000]]);
  assert.equal(view.covered, 7 * 60000);
  const split = recentUsage({ coverage: [[at(-29), at(-28)], [at(-26), at(-25)]] }, 30, now, 1);
  assert.equal(split.buckets[0].coverage.length, 2);
});
test('point receipt boundaries are counted once and old points expire without a new source message', () => {
  const trend = { points: [[at(-30), 3], [at(-1), 4], [at(0), 2], [at(1), 99]], coverage: [[at(-60), at(0)]] };
  const view = recentUsage(trend, 30, now);
  assert.equal(view.total, 6);
  assert.equal(view.buckets.reduce((sum, bucket) => sum + (bucket.points ?? 0), 0), view.total);
  assert.equal(recentUsage(trend, 30, now + 30 * 60000).total, 99);
  assert.equal(recentUsage(trend, 30, now + 32 * 60000).total, null);
});
test('overlapping coverage cannot invent completeness and records before resets or midnight remain in range', () => {
  const trend = { points: [[at(-80), 5], [at(-20), 2]], coverage: [[at(-90), at(-70)], [at(-85), at(-75)], [at(-25), at(-10)]] };
  const view = recentUsage(trend, 1440, now);
  assert.equal(view.total, 7);
  assert.equal(view.covered, 35 * 60000);
  assert.equal(view.status, 'partial');
});
test('unreadable history is not shown as zero consumption', () => {
  const view = recentUsage({ error: 'unreadable', points: [[at(-1), 4]], coverage: [[at(-30), at(0)]] }, 30, now);
  assert.equal(view.total, null);
  assert.equal(view.status, 'error');
  assert.ok(view.buckets.every(bucket => bucket.points === null));
});
