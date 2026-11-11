import React, { useEffect, useState } from 'react';
import { RecentUsage } from './RecentUsage.jsx';
import { useProviderClock } from './useProviderClock.js';

const empty = { windows: [] };
export function UsageTrends({ providers, options, windowMinutes, onWindow, onVisible, sourceLabel, initialView }) {
  const [sourceId, setSourceId] = useState(initialView?.sourceId ?? '');
  const sources = providers.flatMap(provider => (provider.windows ?? []).map(quota => ({
    id: `${provider.id}:${quota.key}`, provider, quota, label: `${sourceLabel(provider)} · ${quota.label}`
  })));
  const current = sources.find(source => source.id === sourceId) ?? sources[0];
  const now = useProviderClock(current?.provider ?? empty, windowMinutes);
  useEffect(() => { onVisible(true); return () => onVisible(false); }, [onVisible]);
  useEffect(() => {
    window.aiUsageTrendView = { sourceId };
    return () => { delete window.aiUsageTrendView; };
  }, [sourceId]);
  return <div className="usage-trends">
    {sources.length === 0 ? <div className="trend-empty"><strong>아직 조회된 한도가 없어요</strong><p>계정을 연결하고 사용량을 수신하면 이곳에서 추이를 볼 수 있습니다.</p></div> : <>
      <label className="trend-source" htmlFor="trend-source"><span>계정 · 한도 <span className="trend-live">현재 시각 기준</span></span>
        <select id="trend-source" aria-label="사용량 추이 계정과 한도" value={current.id} onChange={event => setSourceId(event.target.value)}>
          {sources.map(source => <option key={source.id} value={source.id}>{source.label}</option>)}
        </select>
      </label>
      <div className="trend-ranges" role="group" aria-label="사용량 추이 비교 구간">
        {options.map(option => <button key={option.minutes} aria-label={`비교 구간 ${option.label}`} aria-pressed={windowMinutes === option.minutes}
          className={windowMinutes === option.minutes ? 'selected' : ''} onClick={() => onWindow(option.minutes)}>{option.label}</button>)}
      </div>
      <div className={`trend-result ${current.provider.service ?? current.provider.id.split('-')[0]}`}>
        {!current.quota.trend ? <p role="status">사용 기록을 불러오고 있습니다.</p>
          : <RecentUsage key={`${current.id}:${windowMinutes}`} quota={current.quota} minutes={windowMinutes} now={now} />}
      </div>
      <details className="trend-help"><summary>이 그래프를 읽는 방법</summary><p>합계는 선택한 기간에 관측한 소비량입니다. %p는 한도 사용률의 변화량으로, 20%에서 23%가 되면 3%p를 소비한 것입니다.</p><p>막대는 수신 시각을 기준으로 묶은 소비량이며, 구간 일부만 관측했으면 옅게 표시합니다. 빈 시간은 미관측으로 남기고 0으로 채우거나 추정하지 않습니다.</p><p>기간을 바꾸면 현재 시각을 기준으로 즉시 다시 계산합니다. 그래프에서 마우스를 움직이거나 클릭하고, 키보드의 ← →로 구간을 살펴볼 수 있습니다.</p></details>
    </>}
  </div>;
}
