import React from 'react';
import { recentUsage } from './recentUsage.js';
import { formatPoints } from './usageHistory.js';
import './recentUsage.css';

const shortTime = at => new Date(at).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
const fullTime = at => new Date(at).toLocaleString('ko-KR', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false });
export function RecentUsage({ quota, minutes, now, compact, onConfigure }) {
  const view = recentUsage(quota.trend, minutes, now);
  const label = minutes < 60 ? `${minutes}m` : minutes < 1440 ? `${minutes / 60}h` : '1d';
  const maximum = Math.max(1, ...view.buckets.map(bucket => bucket.points ?? 0));
  const x = at => (at - view.start) / (view.end - view.start) * 240;
  const status = view.status === 'error' ? '기록 오류' : view.status === 'missing' ? '기록 없음' : view.status === 'partial' ? '일부 관측' : '전체 관측';
  const description = `${fullTime(view.start)} – ${fullTime(view.end)}. ${quota.label} 관측 총소비량 ${view.total === null ? '기록 없음' : `${formatPoints(view.total)}%p`}. ${status}. 빈 구간은 미관측이며, 관측된 0은 낮은 선으로 표시합니다. 변화량은 수신 시각 기준입니다.${quota.trend?.error ? ` ${quota.trend.error}` : ''}`;
  return <button className="recent-usage recent-trend" onClick={onConfigure} title={description}
    aria-label={`${quota.label} 최근 ${label} 총소비량과 추이`} data-status={view.status}
    data-start={view.start} data-end={view.end} data-total={view.total ?? ''}>
    <span className="recent-summary"><span className="recent-title">{compact ? '' : '최근 '}{label} 총소비</span>
      <span className="today-amount recent-total">{view.total === null ? '—' : <>{formatPoints(view.total)}<small>%p</small></>}</span></span>
    <svg className="recent-chart" viewBox="0 0 240 28" preserveAspectRatio="none" role="img" aria-label={description}>
      {view.buckets.map((bucket, i) => {
        if (bucket.points === null) return null;
        const height = Math.max(1, bucket.points / maximum * 24);
        return <g key={i} className={`recent-bucket ${bucket.partial ? 'partial' : 'complete'} ${bucket.points === 0 ? 'zero' : ''}`}>
          <title>{shortTime(bucket.start)} – {shortTime(bucket.end)} · {formatPoints(bucket.points)}%p{bucket.partial ? ' · 일부 관측' : ''}</title>
          {bucket.coverage.map(([a, b], j) => <rect key={j} x={x(a)} y={27 - height} width={(x(b) - x(a)) * .94} height={height} />)}
          {bucket.coverage.length === 0 && bucket.events.map(([at], j) => <circle key={j} cx={Math.min(239, x(at))} cy={27 - height} r=".8" />)}
        </g>;
      })}
      {view.total === null && <text x="120" y="17" textAnchor="middle">{status}</text>}
    </svg>
    <span className="recent-axis"><time dateTime={new Date(view.start).toISOString()}>{shortTime(view.start)}</time>
      <span className="recent-coverage">{status}</span><span>현재</span></span>
  </button>;
}
