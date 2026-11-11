import test from 'node:test';
import assert from 'node:assert/strict';
import { nextDisplayDelay } from './displayClock.js';

const now = 1700000000000;
test('cards without usable time fields have no recurring clock', () => {
  assert.equal(nextDisplayDelay({ windows: [] }, now), null);
  assert.equal(nextDisplayDelay({ updatedAt: 'invalid', windows: [{ resetsAt: null }, { resetsAt: 0 }, { resetsAt: 'invalid' }] }, now), null);
});
test('countdown wakes at its own minute boundary, independent of the wall-clock minute', () => {
  assert.equal(nextDisplayDelay({ windows: [{ resetsAt: (now + 61000) / 1000 }] }, now), 1001);
  assert.equal(nextDisplayDelay({ windows: [{ resetsAt: (now + 60000) / 1000 }] }, now), 60000);
});
test('reset expiry is not postponed to the next minute', () => {
  assert.equal(nextDisplayDelay({ windows: [{ resetsAt: (now + 999) / 1000 }] }, now), 1000);
  assert.equal(nextDisplayDelay({ windows: [{ resetsAt: (now - 1) / 1000 }] }, now), null);
});
test('fresh receipt becomes one minute old at the source-relative boundary', () => {
  assert.equal(nextDisplayDelay({ updatedAt: new Date(now - 59000).toISOString() }, now), 1001);
});
test('the strict five-minute stale flag crosses its boundary', () => {
  assert.equal(nextDisplayDelay({ updatedAt: new Date(now - 300000).toISOString() }, now), 1);
});
test('age changes to hours at the correct boundary and visible clocks recheck within one minute', () => {
  assert.equal(nextDisplayDelay({ updatedAt: new Date(now - 3599000).toISOString() }, now), 1001);
  assert.equal(nextDisplayDelay({ updatedAt: new Date(now - 3600000).toISOString() }, now), 60000);
});
test('the earliest provider window wins, including secondary quotas', () => {
  assert.equal(nextDisplayDelay({ updatedAt: new Date(now).toISOString(), windows: [
    { resetsAt: (now + 79000) / 1000 }, { resetsAt: (now + 2500) / 1000 }
  ] }, now), 2501);
});
test('future timestamps and already expired quotas cannot spin the clock', () => {
  assert.equal(nextDisplayDelay({ updatedAt: new Date(now + 10000).toISOString(), windows: [{ resetsAt: (now - 10000) / 1000 }] }, now), 60000);
});

test('rolling totals expire at the selected-window boundary without a new source snapshot', () => {
  const provider = { windows: [{ trend: { points: [[(now - 30 * 60000 + 1500) / 1000, 3]], coverage: [] } }] };
  assert.equal(nextDisplayDelay(provider, now, 30), 1501);
  assert.equal(nextDisplayDelay(provider, now, 60), Math.min(60000, 60000 - now % 60000 + 1));
  assert.equal(nextDisplayDelay({ windows: [{ trend: { points: [], coverage: [] } }] }, now, 30), null);
});
