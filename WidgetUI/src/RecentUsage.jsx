import React, { useState } from 'react';
import { recentUsage } from './recentUsage.js';
import { formatPoints } from './usageHistory.js';
import './recentUsage.css';

const shortTime = at => new Date(at).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
const fullTime = at => new Date(at).toLocaleString('ko-KR', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
const duration = ms => {
  const seconds = Math.floor(ms / 1000), minutes = Math.floor(seconds / 60);
  if (minutes >= 60) return `${Math.floor(minutes / 60)}시간${minutes % 60 ? ` ${minutes % 60}분` : ''}`;
  return minutes ? `${minutes}분` : `${seconds}초`;
};
const bucketSize = minutes => ({ 30: 1, 60: 2, 180: 5, 360: 15, 720: 30, 1440: 60 }[minutes] ?? minutes / 24);
const scaleTop = value => {
  const power = 10 ** Math.floor(Math.log10(Math.max(1, value)));
  return [1, 2, 2.5, 5, 10].map(step => step * power).find(step => step >= value) ?? power * 10;
};
const plot = { width: 248, top: 13, bottom: 117, height: 104 };

export function RecentUsage({ quota, minutes, now }) {
  const [hover, setHover] = useState(null);
  const [pinned, setPinned] = useState(null);
  const step = bucketSize(minutes);
  const view = recentUsage(quota.trend, minutes, now, Math.round(minutes / step));
  const selected = hover ?? pinned;
  const bucket = selected === null ? null : view.buckets[selected];
  const label = minutes < 60 ? `${minutes}분` : minutes < 1440 ? `${minutes / 60}시간` : '24시간';
  const maximum = scaleTop(Math.max(1, ...view.buckets.map(item => item.points ?? 0)));
  const x = at => (at - view.start) / (view.end - view.start) * plot.width;
  const y = value => plot.bottom - value / maximum * plot.height;
  const status = view.status === 'error' ? '기록 오류' : view.status === 'missing' ? '기록 없음' : view.status === 'partial' ? '일부 관측' : '전체 관측';
  const bucketStatus = item => item.points === null ? (view.status === 'error' ? '기록 오류' : '미관측') : item.partial ? '일부 관측' : '전체 관측';
  const bucketDescription = item => `${shortTime(item.start)} – ${shortTime(item.end)} · ${item.points === null ? '소비량 알 수 없음' : `${formatPoints(item.points)}%p`} · ${bucketStatus(item)}`;
  const description = `${fullTime(view.start)} – ${fullTime(view.end)}. ${quota.label} 관측 총소비량 ${view.total === null ? '기록 없음' : `${formatPoints(view.total)}%p`}. ${status}. 빈 구간은 미관측이며, 관측된 0은 기준선으로 표시합니다. 변화량은 수신 시각 기준입니다.${quota.trend?.error ? ` ${quota.trend.error}` : ''}`;
  const indexAt = event => {
    const rect = event.currentTarget.getBoundingClientRect();
    const position = (event.clientX - rect.left) / rect.width * 280 / plot.width;
    return position < 0 || position > 1 ? null : Math.min(view.buckets.length - 1, Math.floor(position * view.buckets.length));
  };
  const onKeyDown = event => {
    if (event.key === 'Escape') { setHover(null); setPinned(null); event.preventDefault(); event.stopPropagation(); return; }
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault(); event.stopPropagation(); setHover(null);
    setPinned(previous => {
      const last = view.buckets.length - 1;
      if (event.key === 'Home') return 0;
      if (event.key === 'End') return last;
      return Math.max(0, Math.min(last, (hover ?? previous ?? last) + (event.key === 'ArrowLeft' ? -1 : 1)));
    });
  };
  return <section className="recent-usage recent-trend"
    aria-label={`${quota.label} 최근 ${label} 총소비량과 추이`} data-status={view.status}
    data-start={view.start} data-end={view.end} data-total={view.total ?? ''}>
    <div className="recent-heading"><span>최근 {label} · 관측 총소비량</span><span className={`trend-status ${view.status}`}>{status}</span></div>
    <div className="recent-summary"><span className="recent-total">{view.total === null ? '—' : <>{formatPoints(view.total)}<small>%p</small></>}</span>
      <span className="trend-observed">관측 시간<strong>{view.status === 'error' ? '—' : duration(view.covered)}</strong></span></div>
    <div className="trend-time-range"><time dateTime={new Date(view.start).toISOString()}>{fullTime(view.start)}</time><span>–</span><time dateTime={new Date(view.end).toISOString()}>{fullTime(view.end)}</time></div>
    <div className="trend-plot-heading"><span>구간별 소비량</span><span>{duration(step * 60000)} 단위 · %p</span></div>
    <div className="trend-scrubber" tabIndex="0" role="slider" aria-label="시간별 소비량 탐색"
      aria-valuemin={0} aria-valuemax={view.buckets.length - 1} aria-valuenow={selected ?? view.buckets.length - 1}
      aria-valuetext={bucketDescription(bucket ?? view.buckets.at(-1))}
      onKeyDown={onKeyDown} onPointerMove={event => setHover(indexAt(event))} onPointerLeave={() => setHover(null)}
      onClick={event => { const index = indexAt(event); setPinned(previous => index === previous ? null : index); setHover(null); }}>
      <svg className="recent-chart" viewBox="0 0 280 166" role="img" aria-label={description}>
        {[maximum, maximum / 2, 0].map(value => <g className="trend-grid" key={value}>
          <line x1="0" x2={plot.width} y1={y(value)} y2={y(value)} />
          <text x="256" y={y(value) + 3}>{formatPoints(value)}</text>
        </g>)}
        {bucket && <path className="trend-selection" d={`M${x(bucket.start)} ${plot.top}H${x(bucket.end)}V${plot.bottom}H${x(bucket.start)}Z`} />}
        {view.buckets.map((item, i) => {
          if (item.points === null) return null;
          const height = Math.max(2, item.points / maximum * plot.height);
          return <g key={i} data-index={i} className={`recent-bucket ${item.partial ? 'partial' : 'complete'} ${item.points === 0 ? 'zero' : ''} ${selected === i ? 'active' : ''}`}>
            <title>{bucketDescription(item)}</title>
            {item.coverage.map(([a, b], j) => <rect key={j} x={x(a)} y={plot.bottom - height} width={Math.max(.1, x(b) - x(a) - .9)} height={height} rx=".6" />)}
            {item.coverage.length === 0 && item.events.map(([at], j) => <circle key={j} cx={x(at)} cy={plot.bottom - height} r="2" />)}
          </g>;
        })}
        {view.total === null && <text className="trend-no-data" x={plot.width / 2} y="62" textAnchor="middle">{view.status === 'error' ? '기록을 읽을 수 없습니다' : '이 시간대의 기록이 없습니다'}</text>}
        {bucket && <line className="trend-crosshair" x1={x((bucket.start + bucket.end) / 2)} x2={x((bucket.start + bucket.end) / 2)} y1={plot.top} y2="140" />}
        <g className="trend-coverage-strip" aria-hidden="true">
          <line className="trend-coverage-track" x1="0" x2={plot.width} y1="136" y2="136" />
          {view.status !== 'error' && view.buckets.flatMap((item, i) => item.coverage.map(([a, b], j) => <line key={`${i}:${j}`} x1={x(a)} x2={x(b)} y1="136" y2="136" />))}
        </g>
        <text className="trend-coverage-label" x="256" y="139">관측</text>
        {[0, .5, 1].map(fraction => <text className="trend-x-label" key={fraction} x={fraction * plot.width} y="159" textAnchor={fraction === 0 ? 'start' : fraction === 1 ? 'end' : 'middle'}>{shortTime(view.start + (view.end - view.start) * fraction)}</text>)}
      </svg>
    </div>
    <div className="trend-inspection" data-selected={selected ?? ''} aria-live="polite" aria-atomic="true">
      {bucket ? <><div><time>{shortTime(bucket.start)} – {shortTime(bucket.end)}</time><strong>{bucket.points === null ? '—' : <>{formatPoints(bucket.points)}<small>%p</small></>}</strong></div>
        <div><span>{bucketStatus(bucket)}{bucket.points === null ? ' · 소비량 알 수 없음' : bucket.points === 0 ? ' · 소비 없음' : ''}</span>{pinned !== null && <button aria-label="그래프 선택 해제" onClick={() => { setPinned(null); setHover(null); }}>해제</button>}</div></>
        : <><div><span>시간대별 소비량 살펴보기</span><span className="trend-inspect-arrow" aria-hidden="true">↔</span></div><div><span>그래프에 마우스를 올리거나 클릭하세요</span></div></>}
    </div>
    <div className="trend-legend"><span><i className="legend-bar" />소비량</span><span><i className="legend-zero" />관측된 0</span><span><i className="legend-gap" />미관측</span></div>
    {view.status === 'error' ? <p className="trend-caveat" role="status">기록을 읽지 못했습니다. 계정 연결 상태와 기록 파일을 확인해주세요.</p>
      : view.status === 'partial' && <p className="trend-caveat">기록이 있는 시간의 합계입니다. 빈 시간의 소비량은 포함되지 않습니다.</p>}
  </section>;
}
