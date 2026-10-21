// Wake only when a displayed minute, receipt age, freshness flag or quota expiry
// can change. Animation clocks remain entirely independent of this scheduler.
export function nextDisplayDelay(provider, now, windowMinutes = 30) {
  let next = Infinity;
  const received = Date.parse(provider.updatedAt);
  if (Number.isFinite(received)) {
    const elapsed = Math.max(0, now - received);
    const step = elapsed < 3600000 ? 60000 : 3600000;
    next = received + (Math.floor(elapsed / step) + 1) * step;
    const staleAt = received + 300000;
    if (staleAt >= now) next = Math.min(next, staleAt);
  }
  for (const quota of provider.windows ?? []) {
    if (quota.trend?.points?.length || quota.trend?.coverage?.length) {
      // Sliding graphs move on local minute boundaries and expire points exactly,
      // even when the CLI has supplied no new snapshot. Logo mode owns no card clocks.
      next = Math.min(next, (Math.floor(now / 60000) + 1) * 60000);
      for (const [at] of quota.trend.points ?? []) {
        const expires = at * 1000 + windowMinutes * 60000;
        if (expires > now) next = Math.min(next, expires);
      }
    }
    const reset = quota.resetsAt * 1000;
    if (!Number.isFinite(reset) || reset <= now || reset <= 0) continue;
    const remaining = reset - now;
    next = Math.min(next, now + (remaining % 60000 || 60000));
  }
  // A minute cap also handles system clock changes while the card is visible.
  // One extra millisecond crosses strict expiry/freshness boundaries reliably.
  return Number.isFinite(next) ? Math.min(60000, Math.max(1, next - now + 1)) : null;
}
