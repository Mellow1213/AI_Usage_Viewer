import { useEffect, useState } from 'react';
import { nextDisplayDelay } from './displayClock.js';

export function useProviderClock(provider, windowMinutes = 30) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    let timer;
    const tick = () => {
      clearTimeout(timer);
      const current = Date.now();
      setNow(current);
      const delay = nextDisplayDelay(provider, current, windowMinutes);
      if (delay !== null) timer = setTimeout(tick, delay);
    };
    // A newly received snapshot and a restored card update immediately. Cards
    // unmount in logo mode, so that mode has no receipt/countdown clock at all.
    tick();
    window.addEventListener('focus', tick);
    const resume = () => { if (!document.hidden) { clearTimeout(timer); tick(); } };
    document.addEventListener('visibilitychange', resume);
    return () => {
      clearTimeout(timer);
      window.removeEventListener('focus', tick);
      document.removeEventListener('visibilitychange', resume);
    };
  }, [provider, windowMinutes]);
  return now;
}
