import React, { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { createRoot } from 'react-dom/client';
import { animate, AnimatePresence, motion, useMotionValue, useSpring, useTransform } from 'motion/react';
import { ArrowDownLeft, ArrowDownRight, ArrowLeft, ArrowUpLeft, ArrowUpRight, ChevronDown, ChevronRight, CirclePlus, Clock3, Command, Grip, Settings, Pin, Plus, Power, RefreshCw, Rows3, LayoutPanelTop, Trash2, UserRound, X } from 'lucide-react';
import './style.css';
import { UpdateSettings } from './Updates.jsx';
import { nextDisplayDelay } from './displayClock.js';

const native = window.aiUsageNative ?? window.chrome?.webview;
const send = (action, extra = {}) => native?.postMessage({ action, ...extra });
const motionPreference = window.matchMedia('(prefers-reduced-motion: reduce)');
const subscribeMotion = callback => { motionPreference.addEventListener('change', callback); window.addEventListener('usage-motion', callback); return () => { motionPreference.removeEventListener('change', callback); window.removeEventListener('usage-motion', callback); }; };
function useReducedMotion() { return useSyncExternalStore(subscribeMotion, () => typeof native?.motionOverride === 'boolean' ? native.motionOverride : motionPreference.matches); }
const initial = { busy: true, pinned: true, collapsed: false, viewMode: 0, bridgeInstalled: false, authBusy: false, accounts: [
  { id: 'codex', source: 'widget', status: 'disconnected' }, { id: 'claude', source: 'widget', status: 'disconnected' }
], providers: [] };
const catalog = [
  { id: 'codex', name: 'Codex', symbol: '⌘', description: 'ChatGPT 구독 한도 · 자동 조회' },
  { id: 'claude', name: 'Claude', symbol: '✳', description: 'Claude 구독 한도 · 자동 조회' }
];
const isCodex = id => id === 'codex' || id?.startsWith('codex-');
const serviceOf = item => item.service ?? catalog.find(service => item.id === service.id || item.id?.startsWith(`${service.id}-`))?.id;
const providerClass = provider => serviceOf(provider) ?? 'unknown';
const defaultName = provider => provider.defaultName ?? provider.name ?? catalog.find(service => service.id === serviceOf(provider))?.name ?? 'AI';
const accountAlias = provider => provider.nickname ?? (provider.defaultName && provider.name !== provider.defaultName ? provider.name : null);
const providerName = provider => accountAlias(provider) ? `${defaultName(provider)} · ${accountAlias(provider)}` : defaultName(provider);
const savedAccount = account => account.enabled || account.id !== serviceOf(account) || account.status === 'connected';
const managedAccounts = state => (state.accounts ?? initial.accounts).filter(account => state.providers.some(provider => provider.id === account.id) || savedAccount(account));
function AccountLabel({ account }) {
  const alias = accountAlias(account);
  return <span className="account-label"><span className="account-service">{defaultName(account)}</span>{alias && <><span className="account-alias-dot" aria-hidden="true">·</span><span className="account-alias" title={alias}>{alias}</span></>}</span>;
}
const measurementOptions = [{ minutes: 30, label: '30m' }, { minutes: 60, label: '1h' }, { minutes: 180, label: '3h' }, { minutes: 360, label: '6h' }, { minutes: 720, label: '12h' }, { minutes: 1440, label: '1d' }];
const intervalOptions = [1, 5, 15, 30, 60, 180];
const cornerOptions = [
  { id: 0, label: '좌측 상단', Icon: ArrowUpLeft }, { id: 1, label: '우측 상단', Icon: ArrowUpRight },
  { id: 2, label: '좌측 하단', Icon: ArrowDownLeft }, { id: 3, label: '우측 하단', Icon: ArrowDownRight }
];
const measurementLabel = minutes => measurementOptions.find(option => option.minutes === minutes)?.label
  ?? (minutes < 60 ? `${minutes}m` : `${Math.floor(minutes / 60)}h${minutes % 60 ? ` ${minutes % 60}m` : ''}`);
// Browser preview is explicitly labelled; the desktop app never uses example values.
const preview = new URLSearchParams(location.search).get('preview') === '1';
const previewState = { ...initial, busy: false, accounts: initial.accounts.map(account => ({ ...account, status: 'connected' })), providers: [
  { id: 'codex', status: 'ready', message: '계정 사용량', updatedAt: new Date().toISOString(), windows: [{ key: 'primary', label: '주간', usedPercent: 8, remainingPercent: 92, resetsAt: Date.now() / 1000 + 273000 }] },
  { id: 'claude', status: 'ready', message: 'Claude Code에서 수신', updatedAt: new Date().toISOString(), windows: [{ key: 'five_hour', label: '5시간', usedPercent: 36, remainingPercent: 64, resetsAt: Date.now() / 1000 + 8280 }, { key: 'seven_day', label: '주간', usedPercent: 62, remainingPercent: 38, resetsAt: Date.now() / 1000 + 180000 }] }
] };
const restoredUI = native?.uiState;
const restoredForm = { ...restoredUI?.form };
function takeFormDraft(id, saved) {
  const entry = restoredForm[id];
  delete restoredForm[id];
  return entry?.saved === String(saved) ? entry.value : saved;
}
let latest = native?.initialState ?? (preview ? previewState : initial);
native?.addEventListener('message', event => {
  latest = event.data;
  window.dispatchEvent(new CustomEvent('usage-state', { detail: latest }));
});

function Triangle({ active = false, connected = false, large = false, transitioning = false, rotation = 0, rotationStep = 120, onSpinComplete }) {
  const reduce = useReducedMotion();
  const boundedRefresh = Boolean(window.aiUsageNative);
  const refreshRotation = useMotionValue(rotation);
  const wasActive = useRef(false);
  useEffect(() => {
    // Offscreen WebViews can suspend frames while continuing to receive usage updates.
    // Discard the old turn on either visibility edge instead of replaying it on return.
    const reset = () => refreshRotation.jump(rotation);
    reset();
    document.addEventListener('visibilitychange', reset);
    window.addEventListener('usage-visibility', reset);
    return () => {
      document.removeEventListener('visibilitychange', reset);
      window.removeEventListener('usage-visibility', reset);
      refreshRotation.stop();
    };
  }, [refreshRotation, rotation, reduce, transitioning]);
  useEffect(() => {
    if (boundedRefresh && active && !wasActive.current && !transitioning && !reduce && !document.hidden && native?.visible !== false && !refreshRotation.isAnimating()) {
      // One bounded turn: overlapping refreshes never add rotations to a backlog.
      animate(refreshRotation, rotation + 360, { duration: 1.6, ease: 'easeInOut', onComplete: () => refreshRotation.jump(rotation) });
    }
    wasActive.current = active;
  }, [active, transitioning, reduce, refreshRotation, rotation, boundedRefresh]);
  const waving = connected && !transitioning && !reduce;
  const from = transitioning ? rotation - rotationStep : rotation;
  const spinning = transitioning || (active && !reduce);
  return <motion.svg className={`triangle ${connected ? 'connected' : 'disconnected'} ${large ? 'large' : ''} ${waving ? 'waving' : ''} ${active && !reduce ? 'syncing' : ''}`} viewBox="0 0 40 40" fill="none" aria-label={transitioning ? '화면 전환 중' : active ? '갱신 중' : connected ? '모든 AI 연결됨' : 'AI 연결 확인 필요'}
    style={{ transformOrigin: '50% 58.333333%', ...(boundedRefresh && !transitioning ? { rotate: refreshRotation } : {}) }} initial={transitioning || !boundedRefresh ? { rotate: from } : false}
    animate={transitioning ? { rotate: [from, rotation] } : boundedRefresh ? undefined : { rotate: spinning ? [rotation, rotation + 120, rotation + 240, rotation + 360] : rotation }}
    onAnimationComplete={transitioning ? onSpinComplete : undefined}
    transition={boundedRefresh ? { duration: transitioning ? .48 : reduce ? 0 : 1.6, ease: 'easeInOut' } : spinning ? { duration: transitioning ? .48 : 1.6, repeat: transitioning ? 0 : Infinity, ease: 'easeInOut' } : { duration: .25 }}>
    {['M20 4 L27 16 L13 16 Z', 'M30 21 L37 33 L23 33 Z', 'M10 21 L17 33 L3 33 Z'].map((d, i) =>
      // CSS keeps a continuous ripple across React's periodic state updates.
      // Negative phases spread the three pieces evenly around the wave.
      <path key={d} d={d} fill="currentColor" opacity={[1, .65, .35][i]} style={{ '--wave-phase': i, filter: 'brightness(1)' }} />)}
  </motion.svg>;
}

function NumberValue({ value }) {
  const reduce = useReducedMotion();
  const raw = useMotionValue(value);
  const spring = useSpring(raw, { stiffness: 90, damping: 22 });
  const formatted = useTransform(spring, n => String(Math.round(n)));
  useEffect(() => { if (reduce) spring.jump(value); else raw.set(value); }, [value, reduce, raw, spring]);
  return <motion.span>{formatted}</motion.span>;
}

function remainingTime(epoch, now) {
  if (!epoch) return '초기화 시간 미제공';
  const seconds = Math.max(0, Math.ceil(epoch - now / 1000));
  if (!seconds) return '초기화됨 · 다시 확인 필요';
  const mins = Math.ceil(seconds / 60), hours = Math.floor(mins / 60), days = Math.floor(hours / 24);
  return days ? `${days}일 ${hours % 24}시간 후 초기화` : hours ? `${hours}시간 ${mins % 60}분 후 초기화` : `${mins}분 후 초기화`;
}

function CompactReset({ quota, label, now }) {
  const epoch = quota?.resetsAt;
  const provided = Number.isFinite(epoch) && epoch > 0;
  const mins = provided ? Math.max(0, Math.ceil((epoch * 1000 - now) / 60000)) : 0;
  const days = Math.floor(mins / 1440), hours = Math.floor(mins % 1440 / 60), minutes = mins % 60;
  // Two units keep the short countdown readable beside a quota percentage.
  const text = !provided ? '—' : mins === 0 ? '초기화됨' : days ? `${days}일${hours ? ` ${hours}시간` : ''}`
    : hours ? `${hours}시간${minutes ? ` ${minutes}분` : ''}` : `${minutes}분`;
  const exact = provided ? new Date(epoch * 1000).toLocaleString('ko-KR', {
    year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false, timeZoneName: 'short'
  }) : null;
  const description = !provided ? '리셋 시간 미제공' : mins === 0 ? '초기화됨 · 다음 수신 대기' : `리셋까지 ${text}`;
  return <span className="compact-reset" title={exact ? `${label} 다음 리셋: ${exact}` : `${label} 리셋 시간 미제공`}
    aria-label={`${label} ${description}`}><Clock3 size={8} aria-hidden="true" /><span>{text}</span></span>;
}

function ageText(date, now) {
  if (!date) return '아직 수신된 데이터 없음';
  const secs = Math.max(0, Math.floor((now - new Date(date).getTime()) / 1000));
  return secs < 60 ? '방금 수신' : secs < 3600 ? `${Math.floor(secs / 60)}분 전 수신` : `${Math.floor(secs / 3600)}시간 전 수신`;
}

function PlanLabel({ id, plan }) {
  if (!plan?.trim()) return null;
  const key = plan.trim().toLowerCase();
  const common = { free: 'Free', go: 'Go', plus: 'Plus', pro: 'Pro', max: 'Max', team: 'Team', business: 'Business', enterprise: 'Enterprise', education: 'Edu', edu: 'Edu' };
  // Codex exposes subscription tiers as raw planType values, separately from usage.
  const codex = { prolite: 'Pro x5', pro: 'Pro x20', promax: 'Pro Max' };
  const label = (isCodex(id) ? codex[key] : null) ?? common[key] ?? plan.trim();
  return <span className="provider-plan" title={label}><span className="plan-dot" aria-hidden="true">·</span>{label}</span>;
}

function QuotaMeter({ quota, value, available, label, small = false }) {
  const reduce = useReducedMotion();
  const trail = useMotionValue(value);
  const trailWidth = useTransform(trail, n => `${n}%`);
  const previous = useRef(value);
  useEffect(() => {
    const decreased = value < previous.current;
    previous.current = value;
    if (reduce || !available || !decreased) { trail.set(value); return; }
    const animation = animate(trail, value, { delay: .65, duration: 1.25, ease: [.22, 1, .36, 1] });
    return () => animation.stop();
  }, [value, available, reduce, trail]);
  const today = available ? Math.min(quota?.today?.cyclePercentagePoints ?? 0, 100 - value) : 0;
  return <div className={`meter ${small ? 'small-quota' : ''} ${!available ? 'empty' : ''}`} role="progressbar" aria-label={label}
    aria-valuemin={0} aria-valuemax={100} aria-valuenow={available ? Math.round(value) : undefined} aria-valuetext={available ? `${Math.round(value)}% 남음` : '사용량 정보 없음'}>
    <div className="meter-today" style={{ width: `${value + today}%` }} />
    <motion.div className="meter-trail" style={{ width: trailWidth }} />
    <motion.div className="meter-fill" initial={false} animate={{ width: `${value}%` }} transition={reduce ? { duration: 0 } : { type: 'spring', stiffness: 85, damping: 24 }} />
    {!small && <div className="meter-ticks" />}
  </div>;
}

function TodayUsage({ quota, available, observedAt, compact = false, intervalMinutes = 30, onConfigure }) {
  if (!available || !quota?.today) return null;
  const points = quota.today.usedPercentagePoints;
  // The visible clock follows the latest received quota snapshot, independently of aggregation.
  const observationAt = observedAt ?? quota.today.sourceAt;
  const time = new Date(observationAt).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  const since = new Date(quota.today.observedSince).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' });
  const measured = new Date(quota.today.measuredAt).toLocaleString('ko-KR', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false });
  const received = new Date(quota.today.sourceAt).toLocaleString('ko-KR', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false });
  const recent = quota.today.recentPercentagePoints;
  const format = value => `${value > 0 ? '−' : ''}${value.toLocaleString('ko-KR', { maximumFractionDigits: 1 })}`;
  return <div className="observed-usage"><button className="today-usage" aria-label="관측 측정 설정" onClick={onConfigure}
    title={`마지막 사용량 수신 ${time}. ${since}부터 관측한 ${quota.label} 한도 변화량입니다. 한도 초기화 또는 날짜 변경 시 누적 소모량을 새로 집계합니다. 집계 ${measured}, 집계에 반영된 수신 ${received}, 갱신 주기 ${measurementLabel(intervalMinutes)}. 하루 전체 사용량이나 토큰 수가 아니며, 수신되지 않은 소비와 초기화 사이의 사용은 빠질 수 있습니다. 클릭해서 측정 설정을 변경하세요.`}>
    <span><time dateTime={observationAt}>{time}</time>관측 {!compact && <span className="today-basis">· {quota.label}</span>}<span className="measurement-chip">{measurementLabel(intervalMinutes)}</span></span><span className="today-amount">{quota.today.isCurrentDay ? <>{format(points)}<small>%p</small></> : '집계 대기'}</span>
  </button><button className="today-usage recent-usage" onClick={onConfigure} title={`마지막 집계(${measured}) 시점의 최근 ${measurementLabel(quota.today.windowMinutes)} 소비량입니다. 한도 초기화 시 이전 소비 기록도 지웁니다. 변화량을 수신한 시각을 기준으로 계산하며, 아직 쌓이지 않은 구간은 복원할 수 없습니다.`}>
    <span>최근 {measurementLabel(quota.today.windowMinutes)} 관측</span><span className="today-amount">{format(recent)}<small>%p</small></span>
  </button></div>;
}

function useProviderClock(provider) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    let timer;
    const tick = () => {
      clearTimeout(timer);
      const current = Date.now();
      setNow(current);
      const delay = nextDisplayDelay(provider, current);
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
  }, [provider]);
  return now;
}

function ProviderCard({ provider, account, busy, onConnect, onMeasurements, measurementInterval, services = catalog }) {
  const now = useProviderClock(provider);
  const reduce = useReducedMotion();
  const isClaude = serviceOf(provider) === 'claude';
  const service = services.find(item => item.id === serviceOf(provider));
  const windows = provider.windows ?? [];
  const main = windows.find(w => w.key === 'seven_day' || w.label === '주간')
    ?? windows.find(w => w.key === 'five_hour' || w.label === '5시간') ?? windows[0];
  const extra = windows.filter(w => w !== main);
  const expired = main?.resetsAt && main.resetsAt * 1000 <= now;
  const available = !!main && !expired;
  const stale = provider.updatedAt && now - new Date(provider.updatedAt).getTime() > 300000;
  const value = available ? Math.max(0, Math.min(100, main.remainingPercent)) : 0;
  const low = available && value <= 20;
  return <motion.section className={`provider ${providerClass(provider)} ${low ? 'low' : ''}`} data-account-id={provider.id} aria-label={`${providerName(provider)} 잔여량`}
    initial={reduce ? false : { opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: .45, delay: isClaude ? .08 : 0 }}>
    <div className="provider-head">
      <div className="provider-brand"><span className="provider-symbol">{service?.id === 'codex' ? <Command size={14} aria-hidden="true" /> : service?.symbol ?? '✦'}</span><span className="provider-name" title={[providerName(provider), account?.email].filter(Boolean).join(' · ')}><AccountLabel account={provider} /><PlanLabel id={provider.id} plan={provider.plan ?? account?.plan} /></span></div>
      <span className={`status-tag ${stale || provider.status === 'error' ? 'muted' : ''}`}><span className="status-dot" />{provider.status === 'ready' ? stale ? '이전 수신값' : '연결됨' : provider.status === 'loading' ? '연결 중' : provider.status === 'error' ? '연결 오류' : provider.status === 'disconnected' ? '로그인 필요' : provider.status === 'signingIn' ? '로그인 중' : '응답 대기'}</span>
    </div>
    <div className="quota-title"><span>{main ? `${main.label} 잔여량` : '사용량 잔여'} </span><span className="tiny-caption">REMAINING</span></div>
    <div className="quota-value"><span className="number">{available ? <NumberValue value={value} /> : <span className="unavailable">—</span>}</span>{available && <span className="percent">%</span>}<span className="quota-aside">{available ? low ? '조금 남았어요' : '남아 있어요' : busy ? '확인하고 있어요' : '수신을 기다려요'}<ArrowDownRight size={14} /></span></div>
    <QuotaMeter key={`${main?.key}:${main?.resetsAt}:${main?.label}:${account?.email}:${provider.plan ?? account?.plan}`} quota={main} value={value} available={available} label={`${provider.id} ${main?.label ?? ''} 잔여량`} />
    <TodayUsage quota={main} available={available} observedAt={provider.updatedAt} intervalMinutes={measurementInterval} onConfigure={onMeasurements} />
    <div className="reset"><Clock3 size={11} /><span>{main ? remainingTime(main.resetsAt, now) : provider.message}</span></div>
    {extra.map(w => {
      const old = w.resetsAt && w.resetsAt * 1000 <= now;
      return <div className="secondary" key={w.key}><div><span>{w.label} 잔여</span><span>{old ? '확인 필요' : <><NumberValue value={w.remainingPercent} />%</>}</span></div><QuotaMeter key={`${w.resetsAt}:${w.label}:${account?.email}:${provider.plan ?? account?.plan}`} small quota={w} value={old ? 0 : w.remainingPercent} available={!old} label={`${provider.id} ${w.label} 잔여량`} /><TodayUsage quota={w} available={!old} observedAt={provider.updatedAt} intervalMinutes={measurementInterval} onConfigure={onMeasurements} /><div className="secondary-reset"><Clock3 size={11} /><span>{remainingTime(w.resetsAt, now)}</span></div></div>;
    })}
    {!isClaude && provider.status === 'error' && <p className="provider-note">{provider.message}</p>}
    {['disconnected', 'signingIn'].includes(provider.status) && <button className="connect" onClick={onConnect}><UserRound size={12} />{provider.status === 'signingIn' ? '로그인 진행 상태' : `${service?.loginName ?? service?.name ?? 'AI'} 계정 연결`}<ArrowUpRight size={12} /></button>}
    <div className="card-foot"><span title={account?.email}>{account?.email ?? (isClaude ? 'CLAUDE CODE' : 'CODEX ACCOUNT')}</span><span>{provider.status === 'error' && main ? '조회 실패 · 이전 값' : ageText(provider.updatedAt, now)}</span></div>
  </motion.section>;
}

function CompactProvider({ provider, account, intervalMinutes, onMeasurements }) {
  const now = useProviderClock(provider);
  const claude = serviceOf(provider) === 'claude';
  const windows = provider.windows ?? [];
  // Compact Claude prioritizes the weekly pool. Keep the 5h quota separate
  // so a missing or expired weekly value never borrows its percentage.
  const main = claude ? windows.find(w => w.key === 'seven_day' || w.label === '주간')
    : windows.find(w => w.label === '5시간') ?? windows[0];
  const sub = claude ? windows.find(w => w.key === 'five_hour' || w.label === '5시간') : null;
  const available = quota => !!quota && (!quota.resetsAt || quota.resetsAt * 1000 > now);
  const valid = available(main);
  return <div className={providerClass(provider)} data-account-id={provider.id}>
    <span className="compact-name" title={[providerName(provider), account?.email].filter(Boolean).join(' · ')}><AccountLabel account={provider} /><PlanLabel id={provider.id} plan={provider.plan ?? account?.plan} /></span>
    <strong>{valid ? <><NumberValue value={main.remainingPercent} /><small>%</small></> : '—'}</strong>
    <div className="compact-period"><span className="compact-label">{main?.label ?? (claude ? '주간' : '수신 대기')}</span><CompactReset quota={main} label={main?.label ?? (claude ? '주간' : '한도')} now={now} /></div>
    {claude ? <div className="compact-subquota" aria-label="Claude 5시간 잔여량"><div className="compact-period"><span>5시간</span><CompactReset quota={sub} label="5시간" now={now} /></div><span className="compact-subvalue">{available(sub) ? <><NumberValue value={sub.remainingPercent} />%</> : '—'}</span></div> : <div className="compact-subquota-slot" aria-hidden="true" />}
    <TodayUsage quota={main} available={valid} observedAt={provider.updatedAt} compact intervalMinutes={intervalMinutes} onConfigure={onMeasurements} />
  </div>;
}

function AccountNickname({ account, disabled, onSave }) {
  const saved = account.nickname ?? '';
  const [draft, setDraft] = useState(() => takeFormDraft(`nickname-${account.id}`, saved));
  const previousSaved = useRef(saved);
  useEffect(() => {
    if (previousSaved.current !== saved) { previousSaved.current = saved; setDraft(saved); }
  }, [saved]);
  const changed = draft !== (account.nickname ?? '');
  return <form className="account-nickname" onSubmit={event => { event.preventDefault(); if (changed && !disabled) onSave(account.id, draft); }}>
    <label htmlFor={`nickname-${account.id}`}>별명 <span>비우면 기본 이름</span></label>
    <div><input id={`nickname-${account.id}`} type="text" value={draft} data-saved-value={saved} maxLength={24} autoComplete="off" spellCheck={false}
      placeholder="예: 개인용, 업무용" disabled={disabled} onChange={event => setDraft(event.target.value)}
      onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); setDraft(account.nickname ?? ''); } }} />
      <button type="submit" disabled={disabled || !changed}>저장</button></div>
  </form>;
}

function Accounts({ state, onAdd, onRemove, onRestore, onRename, expandedId, onExpand }) {
  const locked = state.busy || state.authBusy;
  const accounts = managedAccounts(state);
  useEffect(() => {
    if (expandedId) document.getElementById(`account-details-${expandedId}`)?.closest('.managed-account')?.scrollIntoView({ block: 'nearest' });
  }, [expandedId]);
  if (!accounts.length) return <div className="account-empty"><p>연결할 AI를 먼저 추가하세요.</p><button className="account-primary" onClick={onAdd}><Plus size={13} />AI 추가</button></div>;
  return <div className="accounts-content">
    <p className="account-intro">계정을 선택해 로그인, 별명 변경과 삭제를 할 수 있습니다.</p>
    <button className="account-add" aria-label="AI 계정 추가" disabled={locked} onClick={onAdd}><Plus size={13} />AI 계정 추가</button>
    {accounts.map(account => {
      const claude = serviceOf(account) === 'claude';
      const service = (state.serviceCatalog ?? catalog).find(item => item.id === serviceOf(account));
      const name = providerName(account);
      const visible = state.providers.some(provider => provider.id === account.id);
      const connected = account.status === 'connected';
      const signing = account.status === 'signingIn';
      const expanded = expandedId === account.id || signing;
      return <section className={`account-box managed-account ${providerClass(account)}`} data-account-id={account.id} key={account.id} aria-label={`${name} 계정 관리`}>
        <button className="account-summary" aria-label={`${name} 계정 관리`} aria-expanded={expanded} aria-controls={`account-details-${account.id}`} onClick={() => onExpand(expanded ? null : account.id)}>
          <span className="account-summary-copy"><strong><AccountLabel account={account} /></strong><small title={account.email}>{account.email ?? (signing ? '로그인 진행 중' : connected ? '로그인된 계정' : '로그인이 필요합니다')}</small></span>
          <span className={`account-status ${connected ? 'connected' : ''}`}>{!visible ? '숨김' : signing ? '로그인 중' : connected ? '연결됨' : '미연결'}</span><ChevronDown size={13} />
        </button>
        {expanded && <div className="account-details" id={`account-details-${account.id}`}>
        <h3 className="settings-section-title">로그인</h3>
        <p className="account-source">{account.source === 'system' ? '기존 CLI 계정과 연결됨' : '이 앱의 전용 계정'}</p>
        {account.message && <p className="account-message">{account.message}</p>}
        {signing ? <>
          <p>{claude ? '공식 Claude Code 창과 브라우저에서 직접 완료해주세요.' : '브라우저에서 ChatGPT 로그인을 직접 완료해주세요.'}</p>
          <div className="account-buttons"><button className="account-secondary" onClick={() => send('cancelLogin')}>로그인 취소</button>{!claude && <button className="account-secondary" onClick={() => send('reopenLogin')}>브라우저 다시 열기</button>}</div>
        </> : <>
          <button className="account-primary" disabled={locked || !native} onClick={() => send('loginAccount', { accountId: account.id })}><ArrowUpRight size={13} />{connected ? '다른 계정으로 로그인' : claude ? '공식 Claude Code로 로그인' : `${service?.loginName ?? service?.name ?? 'AI'}로 로그인`}</button>
          <div className="account-buttons">{account.id === serviceOf(account) && <button className="account-secondary" title="선택한 AI의 기존 CLI 계정을 사용합니다. Claude 상태줄 설정은 백업합니다." disabled={locked || !native || (connected && account.source === 'system')} onClick={() => send('useExistingAccount', { accountId: account.id })}>기존 CLI 계정 사용</button>}{connected && <button className="account-secondary" disabled={locked || !native} onClick={() => send('logoutAccount', { accountId: account.id })}>{account.source === 'system' ? '연결 해제' : '로그아웃'}</button>}</div>
          {claude && connected && <button className="account-launch" disabled={locked || !native} onClick={() => send('launchClaudeCode', { accountId: account.id })}>이 계정으로 Claude Code 열기<ArrowUpRight size={12} /></button>}
        </>}
        <AccountNickname account={account} disabled={locked} onSave={onRename} />
        <div className="account-display-actions">
        {!visible && <button className="account-secondary account-restore" disabled={locked} onClick={() => onRestore(account.id)}>카드 다시 표시</button>}
        <button className="account-remove" aria-label={`${name} 계정 항목 삭제`} disabled={locked} onClick={() => onRemove(account.id)}><Trash2 size={12} />계정 항목 삭제</button>
        </div>
        {claude && <p className="account-caption">연결 직후와 이후 5분마다 사용량을 조회합니다. Claude에게 메시지를 보내지 않습니다. 자동 조회는 최신 Claude Code가 필요하며, 응답 후 상태줄 수신도 유지합니다.</p>}
        </div>}
      </section>;
    })}
  </div>;
}

function EmptyDashboard({ onAdd }) {
  return <div className="empty-dashboard"><div className="empty-plus"><Plus size={21} strokeWidth={1.5} /></div><h2>AI를 추가하세요</h2><p>보고 싶은 AI의 사용량만 모아보세요.</p><button className="add-ai-button" aria-label="첫 AI 추가" onClick={onAdd}><Plus size={12} />AI 선택하기</button></div>;
}

function ProviderPicker({ state, onAdd }) {
  return <div className="provider-picker"><p>추가할 계정의 AI를 선택하세요. 같은 AI도 여러 번 추가할 수 있습니다.</p><div className="provider-catalog">{(state.serviceCatalog ?? catalog).map(service => {
    const added = state.providers.some(provider => provider.id === service.id);
    return <button key={service.id} className={`catalog-option ${service.id}`} aria-label={`${service.name} 추가`} disabled={state.busy || state.authBusy} onClick={() => onAdd(service.id)}>
      <span className="catalog-symbol">{service.id === 'codex' ? <Command size={18} aria-hidden="true" /> : service.symbol}</span><span className="catalog-copy"><strong>{added ? `${service.name} 계정 추가` : service.name}</strong><small>{service.description} · 여러 계정</small></span><Plus size={14} />
    </button>;
  })}</div><p className="settings-help">추가한 계정은 계정 관리에서 로그인할 수 있습니다.</p>
  </div>;
}

function MeasurementSettings({ state, onChange }) {
  const interval = state.measurementIntervalMinutes ?? 30;
  const [draft, setDraft] = useState(() => Number(takeFormDraft('measurement-interval', interval)));
  const committed = useRef(interval);
  useEffect(() => {
    if (committed.current !== interval) { setDraft(interval); committed.current = interval; }
  }, [interval]);
  const commit = minutes => {
    setDraft(minutes);
    if (committed.current === minutes) return;
    committed.current = minutes;
    onChange('interval', minutes);
  };
  return <div className="measurement-settings">
    <section>
      <div className="measurement-heading"><h3><label htmlFor="measurement-interval">집계 주기</label></h3><output htmlFor="measurement-interval">{measurementLabel(draft)}</output></div>
      <p>오늘 누적치와 최근 소비량을 갱신하는 간격</p>
      {/* Preview while dragging; persist only on release or keyboard completion. */}
      <input id="measurement-interval" className="measurement-slider" type="range" min="1" max="180" step="1" value={draft} data-saved-value={interval}
        aria-label="집계 주기 슬라이더" aria-valuetext={measurementLabel(draft)} style={{ '--range-progress': `${(draft - 1) / 179 * 100}%` }}
        onChange={event => setDraft(Number(event.target.value))} onPointerUp={event => commit(Number(event.currentTarget.value))}
        onKeyUp={event => commit(Number(event.currentTarget.value))} onBlur={event => commit(Number(event.currentTarget.value))} />
      <div className="measurement-range-labels"><span>1m</span><span>3h</span></div>
      <div className="measurement-options interval-options">{intervalOptions.map(minutes => <button key={minutes} aria-label={`집계 주기 ${measurementLabel(minutes)}`} aria-pressed={draft === minutes} className={draft === minutes ? 'selected' : ''} onClick={() => commit(minutes)}>{measurementLabel(minutes)}</button>)}</div>
    </section>
    <section><h3>비교 구간</h3><p>마지막 집계 시각부터 거슬러 볼 소비량 범위</p><div className="measurement-options">{measurementOptions.map(option => <button key={option.minutes} aria-label={`비교 구간 ${option.label}`} aria-pressed={(state.measurementWindowMinutes ?? 30) === option.minutes} className={(state.measurementWindowMinutes ?? 30) === option.minutes ? 'selected' : ''} onClick={() => onChange('window', option.minutes)}>{option.label}</button>)}</div></section>
    <p className="measurement-hint">집계 주기는 1분~3시간, 1분 단위로 설정합니다. 잔여량 조회는 기존대로 유지되며 새 수신값이 있어야 집계됩니다.</p>
  </div>;
}

function SettingsHome({ state, onNavigate, onQuit }) {
  const sections = [
    ...(state.platform === 'macos' ? [] : [{ page: 'updates', title: '앱 업데이트', label: '앱 업데이트 설정', description: 'GitHub Releases · 확인 · 다운로드 · 재시작', value: state.update?.canApply ? '적용 대기' : state.update?.canDownload ? '새 버전' : `v${state.update?.currentVersion ?? ''}`, Icon: RefreshCw }]),
    { page: 'accounts', title: '계정 관리', description: '추가 · 로그인 · 별명 · 삭제', value: `${managedAccounts(state).length}개 계정`, Icon: UserRound },
    { page: 'measurement', title: '사용량 측정', description: '관측 사용량 계산 기준', value: `집계 ${measurementLabel(state.measurementIntervalMinutes ?? 30)} · 비교 ${measurementLabel(state.measurementWindowMinutes ?? 30)}`, Icon: Clock3 },
    { page: 'appearance', title: '화면 설정', description: state.platform === 'macos' ? '화면 전환 애니메이션' : '애니메이션 · 창 고정 · 위치', value: state.animateTransitions !== false ? '애니메이션 켜짐' : '애니메이션 꺼짐', Icon: LayoutPanelTop }
  ];
  return <>
    <nav className="settings-nav" aria-label="설정 분류">{sections.map(({ page, title, label, description, value, Icon }) =>
      <button key={page} className="settings-link" aria-label={label ?? title} onClick={() => onNavigate(page)}>
        <span className="settings-link-icon"><Icon size={17} /></span><span className="settings-link-copy"><strong>{title}</strong><small>{description}</small><span>{value}</span></span><ChevronRight size={14} />
      </button>
    )}</nav>
    <div className="settings-app-actions"><button className="menu-row close-row" onClick={onQuit}><Power size={14} />AI Usage 종료</button></div>
  </>;
}

function AppearanceSettings({ state, onAction }) {
  const menuBar = state.platform === 'macos';
  return <div className="appearance-settings">
    <section className="settings-section"><h3 className="settings-section-title">애니메이션</h3>
      <button className="settings-switch-row" role="switch" aria-label="화면 전환 애니메이션" aria-checked={state.animateTransitions !== false} onClick={() => onAction('toggleMotion')}>
        <span><strong>부드러운 화면 전환</strong><small>{menuBar ? '메뉴 창을 열고 닫을 때 적용' : '카드를 펼치고 접을 때 적용'}</small></span><span className="settings-switch" aria-hidden="true" />
      </button>
    </section>
    {!menuBar && <section className="settings-section"><h3 className="settings-section-title">창 위치</h3>
      <button className="settings-switch-row" role="switch" aria-label="항상 위에 표시" aria-checked={!!state.pinned} onClick={() => onAction('pin')}>
        <span><strong>카드 항상 위에 표시</strong><small>카드와 로고의 고정은 각각 설정합니다.</small></span><span className="settings-switch" aria-hidden="true" />
      </button>
      <button className="settings-switch-row" role="switch" aria-label="로고 항상 위에 표시" aria-checked={state.logoPinned !== false} onClick={() => onAction('toggleLogoPin')}>
        <span><strong>로고 항상 위에 표시</strong><small>입력 포커스를 옮기지 않고 로고를 유지합니다.</small></span><span className="settings-switch" aria-hidden="true" />
      </button>
      <button className="settings-switch-row" role="switch" aria-label="전체 화면에서 로고 숨기기" aria-checked={state.hideLogoInFullscreen !== false} onClick={() => onAction('toggleFullscreenHide')}>
        <span><strong>전체 화면에서 로고 숨기기</strong><small>같은 모니터에서 전체 화면 앱을 사용할 때 숨깁니다.</small></span><span className="settings-switch" aria-hidden="true" />
      </button>
      <div className="dock-heading">화면 모서리<span>드래그 위치에 따라 자동 전환</span></div>
      <div className="dock-options" role="group" aria-label="화면 모서리">{cornerOptions.map(({ id, label, Icon }) => <button key={id} className="dock-option" aria-label={`${label}에 붙이기`} aria-pressed={(state.anchorCorner ?? 1) === id} onClick={() => onAction('dock', { corner: id })}><Icon size={12} />{label}</button>)}</div>
    </section>}
  </div>;
}

const settingsPages = { settings: '설정', accounts: '계정 관리', providers: '계정 추가', measurement: '사용량 측정', appearance: '화면 설정', updates: '앱 업데이트' };
function SettingsPanel({ page, onBack, onClose, notice, children }) {
  const heading = useRef(null);
  const content = useRef(null);
  useEffect(() => { content.current.scrollTop = 0; heading.current.focus({ preventScroll: true }); }, [page]);
  return <section className="menu settings-panel" aria-labelledby="settings-title">
    <header className="settings-heading">
      {page === 'settings' ? <Settings className="settings-heading-icon" size={16} /> : <button className="icon-button" aria-label={page === 'providers' ? '계정 관리로 돌아가기' : '설정으로 돌아가기'} onClick={onBack}><ArrowLeft size={16} /></button>}
      <h2 id="settings-title" ref={heading} tabIndex={-1}>{settingsPages[page]}</h2>
      <button className="icon-button" aria-label="설정 닫기" title="사용량으로 돌아가기" onClick={onClose}><X size={16} /></button>
    </header>
    <div className="settings-content" ref={content}>
      {notice && <div role="status" className="notice settings-notice">{notice}</div>}
      {children}
    </div>
  </section>;
}

function App() {
  const [state, setState] = useState(latest);
  const menuBar = state.platform === 'macos';
  const [menu, setMenu] = useState(restoredUI?.menu ?? false);
  const [menuPage, setMenuPage] = useState(restoredUI?.menuPage ?? 'settings');
  const [expandedAccountId, setExpandedAccountId] = useState(restoredUI?.expandedAccountId ?? null);
  const pendingAccount = useRef(restoredUI?.pendingAccount ?? null);
  const reduce = useReducedMotion();
  const panel = useRef(null);
  const mode = state.viewMode ?? (state.collapsed ? 1 : 0);
  const revision = state.viewRevision ?? 0;
  const transitionReduce = native ? state.animateTransitions === false : reduce;
  const phase = state.transitionPhase ?? 'settled';
  const changing = phase !== 'settled' && !transitionReduce;
  const [finishedRevision, setFinishedRevision] = useState(revision);
  const [contentReadyRevision, setContentReadyRevision] = useState(revision);
  const logoMoving = !transitionReduce && (changing || finishedRevision !== revision || contentReadyRevision !== revision);
  const markFromLogo = state.transitionFrom === 2;
  const markAtLogo = phase === 'hiding' ? markFromLogo : mode === 2;
  const logoRotation = state.logoRotation ?? 0;
  const logoRotationStep = state.logoRotationStep ?? 120;
  // Only the AI cards selected for this dashboard contribute to the logo's connection state.
  const allConnected = state.providers.length > 0 && state.providers.every(provider => {
    const account = state.accounts?.find(item => item.id === provider.id);
    return account?.status === 'connected' && !['disconnected', 'error', 'signingIn'].includes(provider.status);
  });
  useEffect(() => {
    // The macOS host can release WebKit while closed. Retain only small UI state,
    // never the renderer, account payloads, DOM nodes, or animation queues.
    window.aiUsageCaptureUI = () => {
      const form = Object.fromEntries([...document.querySelectorAll('input[data-saved-value]')]
        .map(input => [input.id, { value: input.value, saved: input.dataset.savedValue }]));
      const scroll = ['.cards', '.compact', '.settings-content'].flatMap(selector => {
        const element = document.querySelector(selector);
        return element ? [{ selector, top: element.scrollTop, left: element.scrollLeft }] : [];
      });
      const focused = document.activeElement;
      const focus = focused?.id ? { id: focused.id,
        ...(Number.isInteger(focused.selectionStart) ? { start: focused.selectionStart, end: focused.selectionEnd } : {}) } : null;
      return { menu, menuPage, expandedAccountId, pendingAccount: pendingAccount.current, form, scroll, focus };
    };
    return () => { delete window.aiUsageCaptureUI; };
  }, [menu, menuPage, expandedAccountId]);
  useEffect(() => {
    if (!restoredUI) return;
    let frame = requestAnimationFrame(() => { frame = requestAnimationFrame(() => {
      for (const position of restoredUI.scroll ?? []) {
        const element = document.querySelector(position.selector);
        if (element) { element.scrollTop = position.top; element.scrollLeft = position.left; }
      }
      const focus = restoredUI.focus, element = focus?.id && document.getElementById(focus.id);
      if (element) {
        element.focus({ preventScroll: true });
        if (typeof element.setSelectionRange === 'function' && Number.isInteger(focus.start)) element.setSelectionRange(focus.start, focus.end);
      }
    }); });
    return () => cancelAnimationFrame(frame);
  }, []);
  useEffect(() => {
    const receive = event => { setState(event.detail); };
    window.addEventListener('usage-state', receive);
    send('ready');
    if (!native && !preview) setState(s => ({ ...s, busy: false, providers: s.providers.map(p => ({ ...p, status: 'disconnected', message: '데스크톱 앱에서 실행해주세요.' })) }));
    return () => { window.removeEventListener('usage-state', receive); };
  }, []);
  useEffect(() => {
    const view = panel.current.querySelector(`[data-view="${mode}"]`);
    let reportedHeight = -1;
    const observer = new ResizeObserver(() => {
      const height = view.offsetHeight;
      // Native width changes also fire ResizeObserver; avoid an IPC round trip for an unchanged height.
      if (height === reportedHeight) return;
      reportedHeight = height;
      send('height', { height, viewMode: mode, viewRevision: revision, reducedMotion: transitionReduce });
    });
    observer.observe(view);
    return () => observer.disconnect();
  }, [mode, revision, transitionReduce]);
  useEffect(() => { if (mode === 2) setMenu(false); }, [mode]);
  useEffect(() => {
    if (!pendingAccount.current) return;
    const { service, previous } = pendingAccount.current;
    const added = state.providers.find(provider => serviceOf(provider) === service && !previous.includes(provider.id));
    if (added) { setExpandedAccountId(added.id); pendingAccount.current = null; }
  }, [state.providers]);
  useEffect(() => { if (transitionReduce) setFinishedRevision(revision); }, [transitionReduce, revision]);
  useEffect(() => {
    if (transitionReduce) { setContentReadyRevision(revision); return; }
    if (phase !== 'settled') return;
    // Keep the opaque carrier until the incoming card's 130ms fade is fully finished.
    // Otherwise handing off at rotation completion would briefly dim the stationary logo.
    const timeout = setTimeout(() => setContentReadyRevision(previous => Math.max(previous, revision)), 155);
    return () => clearTimeout(timeout);
  }, [phase, revision, transitionReduce]);
  useEffect(() => {
    if (phase !== 'hiding') return;
    const timeout = setTimeout(() => send('viewHidden', { viewRevision: revision }), transitionReduce ? 0 : 90);
    return () => clearTimeout(timeout);
  }, [phase, revision, transitionReduce]);
  const action = (name, extra = {}) => {
    if (native) send(name, extra);
    else if (name === 'toggleLogo') setState(s => { const current = s.viewMode ?? 0; const next = current === 2 ? s.lastCardView ?? 0 : 2; return { ...s, viewMode: next, lastCardView: current === 2 ? next : current, collapsed: next !== 0 }; });
    else if (name === 'toggleDetails' || name === 'showFull') setState(s => { const next = name === 'showFull' ? 0 : s.viewMode === 0 ? 1 : 0; return { ...s, viewMode: next, lastCardView: next, collapsed: next !== 0 }; });
    else if (name === 'pin') setState(s => ({ ...s, pinned: !s.pinned }));
    else if (name === 'toggleLogoPin') setState(s => ({ ...s, logoPinned: s.logoPinned === false }));
    else if (name === 'toggleFullscreenHide') setState(s => ({ ...s, hideLogoInFullscreen: s.hideLogoInFullscreen === false }));
    else if (name === 'dock') setState(s => ({ ...s, anchorCorner: extra.corner ?? 1 }));
    else if (name === 'toggleMotion') setState(s => ({ ...s, animateTransitions: s.animateTransitions === false }));
    else if (name === 'checkUpdates') setState(s => ({ ...s, update: { phase: 'upToDate', currentVersion: '1.1.0', message: '디자인 미리보기입니다. 실제 업데이트는 설치된 앱에서 확인해주세요.', canCheck: true } }));
    else if (name === 'refresh' && preview) { setState(s => ({ ...s, busy: true })); setTimeout(() => setState(s => ({ ...s, busy: false })), 2200); }
  };
  const openSettingsPage = page => { setMenuPage(page); setMenu(true); };
  const openAccounts = id => { setExpandedAccountId(id ?? null); openSettingsPage('accounts'); };
  const openPicker = () => openSettingsPage('providers');
  const openMeasurements = () => openSettingsPage('measurement');
  const changeMeasurement = (kind, minutes) => {
    if (native) send(kind === 'window' ? 'setMeasurementWindow' : 'setMeasurementInterval', { minutes });
    else setState(s => ({ ...s, [kind === 'window' ? 'measurementWindowMinutes' : 'measurementIntervalMinutes']: minutes }));
  };
  const addProvider = id => {
    pendingAccount.current = { service: id, previous: state.providers.map(provider => provider.id) };
    setMenuPage('accounts');
    if (native) send('addProvider', { provider: id });
    else setState(s => {
      const added = s.providers.some(p => p.id === id);
      const accountId = added ? `${id}-${crypto.randomUUID().replaceAll('-', '')}` : id;
      const title = catalog.find(service => service.id === id)?.name ?? 'AI';
      const name = added ? `${title} ${s.accounts.filter(a => serviceOf(a) === id).length + 1}` : title;
      const provider = { ...(preview ? previewState.providers.find(p => p.id === id) : { status: 'disconnected', message: '계정을 연결해주세요.', windows: [] }), id: accountId, service: id, name };
      return { ...s, providers: [...s.providers, provider], accounts: s.accounts.some(a => a.id === accountId) ? s.accounts : [...s.accounts, { id: accountId, service: id, name, status: preview ? 'connected' : 'disconnected', source: 'widget' }] };
    });
  };
  const removeProvider = id => {
    if (expandedAccountId === id) setExpandedAccountId(null);
    if (native) send('removeProvider', { provider: id });
    else setState(s => ({ ...s, providers: s.providers.filter(provider => provider.id !== id), accounts: s.accounts.filter(account => account.id !== id) }));
  };
  const restoreProvider = id => {
    if (native) send('showProvider', { provider: id });
    else setState(s => {
      if (s.providers.some(p => p.id === id)) return s;
      const account = s.accounts.find(a => a.id === id);
      if (!account) return s;
      const source = preview ? previewState.providers.find(p => p.id === serviceOf(account)) : { status: 'disconnected', message: '계정을 연결해주세요.', windows: [] };
      return { ...s, providers: [...s.providers, { ...source, id, service: serviceOf(account), name: account.name, defaultName: account.defaultName, nickname: account.nickname }] };
    });
  };
  const renameAccount = (id, nickname) => {
    if (native) send('setAccountNickname', { accountId: id, nickname });
    else setState(s => {
      const alias = nickname.replace(/[\u0000-\u001f\u007f]/g, '').trim().slice(0, 24) || null;
      const account = s.accounts.find(item => item.id === id);
      const defaultName = account?.defaultName ?? account?.name ?? providerName({ id });
      return { ...s, accounts: s.accounts.map(item => item.id === id ? { ...item, nickname: alias, defaultName, name: alias ?? defaultName } : item),
        providers: s.providers.map(item => item.id === id ? { ...item, defaultName: item.defaultName ?? item.name ?? defaultName, name: alias ?? (item.defaultName ?? defaultName) } : item) };
    });
  };
  const refreshButton = <button className={`icon-button refresh ${state.busy ? 'rotating' : ''}`} aria-label="사용량 새로고침"
    title={state.busy ? '사용량 업데이트 중' : menuBar ? '새로고침 · ⌘R 또는 F5' : '새로고침 · Codex 자동 갱신 60초'}
    disabled={state.providers.length === 0 || state.busy || state.authBusy} onClick={() => action('refresh')}><RefreshCw size={menuBar ? 14 : 12} /></button>;
  return <div className={`view-shell ${menuBar ? 'menu-bar-host' : ''} ${changing ? 'is-transitioning' : ''} ${logoMoving ? 'is-logo-moving' : ''}`} ref={panel}><AnimatePresence initial={false} mode="popLayout">
  {mode === 2 ? <motion.div key="logo" data-view={mode} className="logo-shell" role="button" tabIndex={0} aria-label="이전 카드로 돌아가기"
    title={`${state.lastCardView === 1 ? '간략히' : '자세히'} 보기로 돌아가기 · 드래그해서 이동 · 우클릭으로 로고 설정`} onClick={() => action('toggleLogo')}
    onContextMenu={event => { event.preventDefault(); action('logoMenu'); }}
    onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); action('toggleLogo'); } else if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) { event.preventDefault(); action('logoMenu'); } }}
    initial={transitionReduce ? false : { opacity: 0 }} animate={{ opacity: changing ? 0 : 1 }} exit={{ opacity: 0, transition: { duration: transitionReduce ? 0 : .08 } }} transition={{ duration: transitionReduce ? 0 : .08 }}>
    <Triangle connected={allConnected} active={!logoMoving && (state.busy || state.authBusy)} rotation={logoRotation} />
  </motion.div> : <motion.div key={`card-${mode}`} data-view={mode} className={`widget ${menu ? 'settings-open' : ''}`} style={{ '--card-max-height': `${(state.maxCardHeight ?? 880) - 2}px`, ...(native ? { width: 302 } : {}) }}
    initial={transitionReduce ? false : { opacity: 0, y: 4 }} animate={{ opacity: changing ? 0 : 1, y: changing ? 4 : 0 }} exit={{ opacity: 0, transition: { duration: transitionReduce ? 0 : .08 } }}
    transition={{ duration: transitionReduce ? 0 : changing ? .08 : .13, ease: [.22, 1, .36, 1] }}>
    {menu ? <SettingsPanel page={menuPage} onBack={() => setMenuPage(menuPage === 'providers' ? 'accounts' : 'settings')} onClose={() => setMenu(false)} notice={state.notice}>
      {menuPage === 'accounts' ? <Accounts state={state} onAdd={openPicker} onRemove={removeProvider} onRestore={restoreProvider} onRename={renameAccount} expandedId={expandedAccountId} onExpand={setExpandedAccountId} />
        : menuPage === 'providers' ? <ProviderPicker state={state} onAdd={addProvider} />
        : menuPage === 'measurement' ? <MeasurementSettings state={state} onChange={changeMeasurement} />
        : menuPage === 'appearance' ? <AppearanceSettings state={state} onAction={action} />
        : menuPage === 'updates' && !menuBar ? <UpdateSettings update={state.update} onAction={action} authBusy={state.authBusy} />
        : <SettingsHome state={state} onNavigate={page => page === 'accounts' ? openAccounts() : openSettingsPage(page)} onQuit={() => action('close')} />}
    </SettingsPanel> : <>
    <header className="header">
      <div className="drag-zone" title={menuBar ? "AI Usage" : "드래그해서 이동"}><button className="logo-toggle" aria-label={menuBar ? "메뉴 막대로 닫기" : "로고만 표시"} title={menuBar ? "메뉴 막대로 닫기 · 조회는 계속됩니다" : "로고만 표시 · 다시 누르면 이 화면으로 돌아오기"} onClick={() => { setMenu(false); action('toggleLogo'); }}><Triangle connected={allConnected} active={!logoMoving && (state.busy || state.authBusy)} rotation={logoRotation} /></button><div className="wordmark">AI<span>USAGE</span></div>{!menuBar && <Grip className="grip" size={13} />}</div>
      <div className="header-actions">
        {menuBar && refreshButton}
        {!menuBar && <button className={`icon-button ${state.pinned ? 'selected' : ''}`} title={state.pinned ? '항상 위 고정 해제' : '항상 위에 고정'} aria-label="항상 위에 고정" aria-pressed={state.pinned} onClick={() => action('pin')}><Pin size={13} /></button>}
        <button className="icon-button" title="AI 계정 추가" aria-label="AI 추가" onClick={openPicker}><CirclePlus size={14} /></button>
        <button className="icon-button view-toggle" title={mode === 0 ? '간략히 보기' : '자세히 보기'} aria-label={mode === 0 ? '간략히 보기' : '자세히 보기'} aria-pressed={mode === 0} onClick={() => { setMenu(false); action('toggleDetails'); }}>{mode === 0 ? <Rows3 size={14} /> : <LayoutPanelTop size={14} />}</button>
        <button className={`icon-button settings-button ${state.update?.canDownload || state.update?.canApply ? 'has-update' : ''}`} title={state.update?.canApply ? '설정 · 업데이트 적용 대기' : state.update?.canDownload ? '설정 · 새 버전 있음' : '설정'} aria-label="설정" onClick={() => openSettingsPage('settings')}><Settings size={17} /></button>
      </div>
    </header>
      {state.providers.length === 0 ? <EmptyDashboard onAdd={openPicker} /> : mode === 1 ? <motion.div key="compact" className="compact" data-count={state.providers.length} initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={{ duration: reduce ? 0 : .14 }}>
        {state.providers.map(p => <CompactProvider key={p.id} provider={p} account={state.accounts?.find(a => a.id === p.id)} intervalMinutes={state.measurementIntervalMinutes ?? 30} onMeasurements={openMeasurements} />)}
      </motion.div> : <motion.main key="expanded" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} transition={{ duration: reduce ? 0 : .14 }}>
        <div className="cards">{state.providers.map(p => <ProviderCard key={p.id} provider={p} account={state.accounts?.find(a => a.id === p.id)} busy={state.busy} onConnect={() => openAccounts(p.id)} measurementInterval={state.measurementIntervalMinutes ?? 30} onMeasurements={openMeasurements} services={state.serviceCatalog ?? catalog} />)}</div>
      </motion.main>}
    {!menuBar && <footer><span className="footer-status"><span className={`pulse-dot ${state.busy || state.authBusy ? 'working' : ''}`} />{state.authBusy ? '로그인 완료 대기 중' : state.busy ? '사용량 업데이트 중' : state.pinned ? '항상 위에 고정됨' : '고정 해제됨'}</span>{refreshButton}</footer>}
    </>}
    {preview && <div className="preview-label">디자인 미리보기 · 예시 데이터</div>}
  </motion.div>}
  </AnimatePresence>
  {logoMoving && <motion.div key={`transition-mark-${revision}`} className="transition-mark" aria-hidden="true"
    initial={{ opacity: 1, left: markFromLogo ? 9 : 17, top: markFromLogo ? 9 : 11, width: markFromLogo ? 28 : 26, height: markFromLogo ? 28 : 26 }}
    animate={{ opacity: 1, left: markAtLogo ? 9 : 17, top: markAtLogo ? 9 : 11, width: markAtLogo ? 28 : 26, height: markAtLogo ? 28 : 26 }}
    transition={{ duration: .13 }}><Triangle connected={allConnected} transitioning rotation={logoRotation} rotationStep={logoRotationStep} onSpinComplete={() => setFinishedRevision(previous => Math.max(previous, revision))} /></motion.div>}
  </div>;
}

createRoot(document.getElementById('root')).render(<App />);
