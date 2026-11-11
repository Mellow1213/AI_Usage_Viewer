export const settingsPages = Object.freeze({ settings: '설정', accounts: '계정 관리', providers: '계정 추가', trends: '사용량 추이', history: '사용 기록', measurement: '사용량 측정', appearance: '화면 설정', updates: '앱 업데이트' });

const isPage = page => Object.hasOwn(settingsPages, page);

// The path records visited screens, including entry from a main-card shortcut.
// Revisiting an ancestor returns to it instead of creating a navigation loop.
export function visitSettingsPage(path, page) {
  if (!isPage(page)) return path;
  const index = path.indexOf(page);
  return index < 0 ? [...path, page] : path.slice(0, index + 1);
}

export function finishAddingAccount(path) {
  // The picker is a step in account creation, not a destination to revisit
  // with Back after creation. Keep the route used to reach account management.
  return visitSettingsPage(path.filter(page => page !== 'providers'), 'accounts');
}

export function restoreSettingsPath(ui, previewTrends = false) {
  if (Array.isArray(ui?.settingsPath)) {
    return ui.settingsPath.reduce((path, page) => visitSettingsPage(path, page), []);
  }
  if (ui?.menu) {
    const page = isPage(ui.menuPage) ? ui.menuPage : 'settings';
    if (page === 'settings') return ['settings'];
    const parent = page === 'providers' ? ['accounts'] : page === 'trends' ? ['measurement'] : [];
    return ['settings', ...parent, page];
  }
  return previewTrends ? ['settings', 'measurement', 'trends'] : [];
}
