import React, { useEffect, useMemo, useRef, useState } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { aggregateHistory, coverageText, formatPoints, historyModes, historyRange } from './usageHistory.js';
import './usageHistory.css';

export function UsageHistory({ history, accounts, onVisible, initialView }) {
  const [mode, setMode] = useState(initialView?.mode ?? 'day');
  const [offset, setOffset] = useState(initialView?.offset ?? 0);
  const [seriesId, setSeriesId] = useState(initialView?.seriesId ?? '');
  const [selected, setSelected] = useState(initialView?.selected ?? null);
  const chart = useRef(null);
  useEffect(() => { onVisible(true); return () => onVisible(false); }, [onVisible]);
  useEffect(() => {
    window.aiUsageHistoryView = { mode, offset, seriesId, selected };
    return () => { delete window.aiUsageHistoryView; };
  }, [mode, offset, seriesId, selected]);
  const series = history?.series ?? [];
  const current = series.find(item => item.id === seriesId) ?? series[0];
  // Only repaint on source/UI updates; opening this screen adds no polling or display timer.
  const now = Date.now();
  const range = historyRange(mode, offset, now);
  const buckets = useMemo(() => aggregateHistory(current, range, now), [current, mode, offset, history]);
  const active = buckets.find(bucket => bucket.start === selected)
    ?? (mode === 'hour' ? buckets.findLast(bucket => bucket.points !== null) : null)
    ?? buckets.findLast(bucket => bucket.status !== 'future') ?? buckets[0];
  useEffect(() => {
    if (mode !== 'hour' || !chart.current) return;
    const column = chart.current.querySelector('.history-column.selected');
    if (!column) return;
    const target = column.getBoundingClientRect(), viewport = chart.current.getBoundingClientRect();
    chart.current.scrollLeft += target.left + target.width / 2 - viewport.left - viewport.width / 2;
  }, [mode, offset, current?.id, selected]);
  const maximum = Math.max(1, ...buckets.map(bucket => bucket.points ?? 0));
  const total = buckets.reduce((sum, bucket) => sum + (bucket.points ?? 0), 0);
  const hasObservation = buckets.some(bucket => bucket.points !== null);
  const partial = buckets.some(bucket => ['missing', 'partial'].includes(bucket.status));
  const accountName = item => {
    const account = accounts.find(account => account.id === item.providerId);
    return account ? `${account.defaultName ?? account.name ?? account.service ?? 'AI'}${account.nickname ? ` · ${account.nickname}` : ''}` : '계정';
  };
  return <div className="usage-history">
    <p className="history-intro">필요할 때 돌아보는 관측 기록</p>
    {history?.error && <p role="alert" className="history-error">{history.error}</p>}
    {!history ? <p role="status">사용 기록을 불러오고 있습니다.</p> : series.length === 0 ? <div className="history-empty">
      <strong>아직 쌓인 기록이 없어요</strong><p>연결한 계정의 새 사용량을 수신하면 기록을 시작합니다. 이전 사용량은 복원하지 않습니다.</p>
    </div> : <>
      <label className="history-source">계정 · 한도<select aria-label="사용 기록 계정과 한도" value={current?.id ?? ''} onChange={event => { setSeriesId(event.target.value); setSelected(null); }}>
        {series.map(item => <option key={item.id} value={item.id}>{accountName(item)} · {item.label}{item.plan ? ` · ${item.plan}` : ''}{series.some(other => other.id !== item.id && other.providerId === item.providerId && other.label === item.label)
          ? ` · ${new Date(item.startedAt * 1000).toLocaleDateString('ko-KR')} 시작 (${item.id.slice(0, 4)})` : ''}</option>)}
      </select></label>
      <div className="history-modes" role="group" aria-label="사용 기록 단위">{historyModes.map(item => <button key={item.id} aria-pressed={mode === item.id} onClick={() => { setMode(item.id); setOffset(0); setSelected(null); }}>{item.label}</button>)}</div>
      <div className="history-period"><button className="icon-button" aria-label="이전 기간" disabled={range.start <= now - 90 * 86400000} onClick={() => { setOffset(offset - 1); setSelected(null); }}><ChevronLeft size={16} /></button>
        <span>{range.title}</span><button className="icon-button" aria-label="다음 기간" disabled={offset >= 0} onClick={() => { setOffset(offset + 1); setSelected(null); }}><ChevronRight size={16} /></button></div>
      <div className="history-summary"><span>관측된 소비 합계</span><strong>{hasObservation ? formatPoints(total) : '—'}{hasObservation && <small>%p</small>}</strong><span>{hasObservation ? partial ? '미관측 구간이 포함되어 있어요' : `${current.label} 한도 기준` : '이 기간은 미관측이에요'}</span></div>
      <div className="history-legend"><span><i className="history-observed-key" />관측</span><span><i className="history-partial-key" />일부 관측</span><span><i className="history-missing-key" />미관측</span></div>
      <div className="history-chart-scroll" ref={chart} role="region" aria-label="사용 기록 그래프" tabIndex={0}>
        <div className={`history-chart ${mode === 'hour' ? 'history-hourly' : ''}`} style={{ '--history-columns': buckets.length }}>
          {buckets.map(bucket => <button key={bucket.start} className={`history-column ${bucket.status} ${active?.start === bucket.start ? 'selected' : ''}`}
            disabled={bucket.status === 'future'} aria-pressed={active?.start === bucket.start} onClick={() => setSelected(bucket.start)}
            aria-label={`${bucket.label}, ${bucket.points === null ? '—' : `${formatPoints(bucket.points)}%p`}, ${coverageText(bucket)}`} title={`${bucket.description}\n${coverageText(bucket)}`}>
            <span className="history-column-value">{bucket.points === null ? '—' : formatPoints(bucket.points)}</span>
            <span className="history-plot"><span className="history-bar" style={{ height: `${bucket.points === null ? 0 : Math.max(2, bucket.points / maximum * 100)}%` }} /></span>
            <span className="history-column-label">{bucket.label}</span>
          </button>)}
        </div>
      </div>
      {active && <div className="history-detail" aria-live="polite"><strong>{mode === 'week' ? `${active.label}부터 1주` : active.label}<span>{active.points === null ? '—' : `${formatPoints(active.points)}%p`}</span></strong><span>{coverageText(active)}</span>{active.points === 0 && <small>관측 구간에서 소비 변화가 없었어요.</small>}</div>}
      {mode === 'hour' && <p className="history-scroll-hint">좌우로 스크롤해 하루 전체를 확인하세요.</p>}
    </>}
    <p className="history-note">최근 90일 · 기기 현지 시간 · 주별은 월요일 시작<br />수신한 한도 변화량(%p)이며 토큰 수나 전체 사용량은 아닙니다. 0은 관측 구간에서 변화가 없었다는 뜻입니다. 종료·연결 오류·10분 초과 수신 공백·초기화 전후는 미관측으로 남습니다. 변화량은 수신 시각에 기록합니다.</p>
  </div>;
}
