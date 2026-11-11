import test from 'node:test';
import assert from 'node:assert/strict';
import { finishAddingAccount, restoreSettingsPath, visitSettingsPage } from './settingsNavigation.js';

test('main shortcuts do not invent unvisited settings parents', () => {
  for (const page of ['providers', 'accounts', 'measurement']) {
    const path = visitSettingsPage([], page);
    assert.deepEqual(path, [page]);
    assert.deepEqual(path.slice(0, -1), []);
  }
});

test('a graph returns along the route used to reach measurement settings', () => {
  for (const entry of [[], ['settings']]) {
    const measurement = visitSettingsPage(entry, 'measurement');
    const graph = visitSettingsPage(measurement, 'trends');
    assert.deepEqual(graph.slice(0, -1), measurement);
    assert.deepEqual(graph.slice(0, -2), entry);
    assert.deepEqual(visitSettingsPage(graph, 'measurement'), measurement);
  }
});

test('completing account creation consumes the picker and retains its entry route', () => {
  assert.deepEqual(finishAddingAccount(['providers']), ['accounts']);
  assert.deepEqual(finishAddingAccount(['accounts', 'providers']), ['accounts']);
  assert.deepEqual(finishAddingAccount(['settings', 'accounts', 'providers']), ['settings', 'accounts']);
});

test('restoring a renderer keeps its visited route, including closed and shortcut states', () => {
  for (const settingsPath of [[], ['measurement'], ['measurement', 'trends'], ['settings', 'accounts', 'providers']]) {
    assert.deepEqual(restoreSettingsPath({ settingsPath }), settingsPath);
  }
  assert.deepEqual(restoreSettingsPath({ settingsPath: ['settings', 'bad', 'measurement', 'trends', 'measurement'] }), ['settings', 'measurement']);
  assert.deepEqual(restoreSettingsPath({ menu: true, menuPage: 'providers' }), ['settings', 'accounts', 'providers']);
  assert.deepEqual(restoreSettingsPath({ menu: true, menuPage: 'trends' }), ['settings', 'measurement', 'trends']);
  assert.deepEqual(restoreSettingsPath({ menu: true, menuPage: 'bad' }), ['settings']);
  assert.deepEqual(restoreSettingsPath({ menu: false, menuPage: 'trends' }), []);
  assert.deepEqual(restoreSettingsPath(null, true), ['settings', 'measurement', 'trends']);
});
