import React from 'react';
import { Download, RefreshCw, RotateCw } from 'lucide-react';

export function UpdateSettings({ update, onAction, authBusy }) {
  const info = update ?? { phase: 'idle', currentVersion: '1.1.0', message: '새 버전을 확인할 수 있습니다.', canCheck: true };
  const working = ['checking', 'downloading', 'applying'].includes(info.phase);
  const failed = info.phase === 'error';
  return <div className="update-settings">
    <dl className="update-versions"><div><dt>현재 버전</dt><dd>{info.currentVersion}</dd></div>
      {info.latestVersion && <div><dt>새 버전</dt><dd>{info.latestVersion}</dd></div>}</dl>
    <p className={`update-message ${failed ? 'failed' : ''}`} role="status" aria-live="polite">{info.message}</p>
    {info.phase === 'downloading' && <div className="update-progress"><progress aria-label="업데이트 다운로드 진행률" max="100" value={info.progress} /><span>{info.progress}%</span></div>}
    {info.releaseNotes && <section className="update-notes"><h3>변경 내용</h3><pre>{info.releaseNotes.slice(0, 8000)}</pre></section>}
    <div className="update-actions">
      <button className="account-secondary" aria-label="업데이트 확인" disabled={working || !info.canCheck} onClick={() => onAction('checkUpdates')}><RefreshCw size={13} />{failed && info.retryAction === 'check' ? '다시 시도' : '업데이트 확인'}</button>
      {info.canDownload && <button className="account-primary" aria-label="다운로드 및 업데이트" onClick={() => onAction('downloadUpdate')}><Download size={13} />{failed ? '다운로드 다시 시도' : '다운로드 및 업데이트'}</button>}
      {info.canApply && <button className="account-primary" aria-label="재시작하여 적용" disabled={authBusy} onClick={() => onAction('applyUpdate')}><RotateCw size={13} />{failed ? '업데이트 적용 다시 시도' : '재시작하여 적용'}</button>}
    </div>
    {info.lastCheckedAt && <p className="update-checked">마지막 확인 {new Date(info.lastCheckedAt).toLocaleString('ko-KR', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false })}</p>}
    <p className="menu-hint">앱 시작 시 새 버전을 확인합니다. 계속 켜두는 동안에는 이 화면에서 직접 확인해주세요. 다운로드 후 원하는 때 재시작할 수 있습니다.</p>
    {authBusy && info.canApply && <p className="menu-hint">로그인을 완료하거나 취소하면 재시작할 수 있습니다.</p>}
    {failed && <p className="menu-hint">현재 버전으로 계속 사용할 수 있습니다.</p>}
  </div>;
}
