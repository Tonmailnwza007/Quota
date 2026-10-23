import type { DashboardState as WireState, ProviderReading as WireReading, SettingsSnapshot, SettingsPatch, RuntimeInfo, ConnectionState, ProviderKey } from '../../contracts';
import type { ProviderReading } from './types';
import { useCallback, useEffect, useRef, useState } from 'react';
import { APP_NAME } from './brand';

export const desktopApi = window.costats;
export const DEFAULT_SWITCHES: Record<ProviderKey, boolean> = {
  codex: true,
  'codex-cli': true,
  antigravity: true,
  claude: false,
  copilot: false,
};
const count = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
export const money = (value: number) => new Intl.NumberFormat(undefined, { style: 'currency', currency: 'USD' }).format(value);
const percentage = (value: number | null) => typeof value === 'number' && Number.isFinite(value) ? Math.min(100, Math.max(0, value)) : null;

export function relativeTime(value: string | null, now = Date.now()): string {
  if (!value || !Number.isFinite(Date.parse(value))) return 'Waiting for first update';
  const seconds = Math.max(0, Math.floor((now - Date.parse(value)) / 1000));
  if (seconds < 60) return 'Updated just now';
  if (seconds < 3600) return `Updated ${Math.floor(seconds / 60)}m ago`;
  return `Updated ${Math.floor(seconds / 3600)}h ago`;
}

function resetLabel(value: string | null, now: number) {
  if (!value || !Number.isFinite(Date.parse(value))) return null;
  const minutes = Math.ceil((Date.parse(value) - now) / 60000);
  if (minutes <= 0) return 'Reset time reached';
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const remaining = minutes % 60;
  return `Resets in ${days ? `${days}d ` : ''}${hours ? `${hours}h ` : ''}${!days ? `${remaining}m` : ''}`.trim();
}

function portalUrl(id: string) {
  if (id === 'codex' || id === 'codex-cli') return 'https://chatgpt.com/codex/settings/usage';
  if (id === 'claude' || id.startsWith('claude:')) return 'https://claude.ai/settings/usage';
  if (id === 'copilot') return 'https://github.com/settings/copilot/features';
  return null;
}

export function mapReading(reading: WireReading, now: number): ProviderReading {
  const consumption = reading.consumption;
  const details = consumption ? [
    { label: 'Today tokens', value: count.format(consumption.todayTokens) },
    { label: 'Today est. API cost', value: money(consumption.todayCostUsd) },
    { label: '30-day tokens', value: count.format(consumption.monthTokens) },
    { label: '30-day est. API cost', value: money(consumption.monthCostUsd) },
    { label: 'This-week tokens', value: count.format(consumption.weekTokens) },
    { label: 'This-week est. API cost', value: money(consumption.weekCostUsd) },
  ] : [];
  if (reading.spending?.kind === 'overage') details.push({ label: 'Overage spend', value: `${reading.spending.currencySymbol}${reading.spending.consumed.toFixed(2)}${reading.spending.limit == null ? '' : ` / ${reading.spending.currencySymbol}${reading.spending.limit.toFixed(2)}`}` });
  return {
    ...reading,
    status: reading.status === 'error' ? 'offline' : reading.status,
    sessionProgress: percentage(reading.sessionProgress),
    weeklyProgress: percentage(reading.weeklyProgress),
    sessionResetLabel: resetLabel(reading.sessionResetAt, now),
    weeklyResetLabel: resetLabel(reading.weeklyResetAt, now),
    namedBuckets: reading.namedBuckets.map(bucket => ({ ...bucket, percentage: percentage(bucket.percentage), resetLabel: resetLabel(bucket.resetAt, now) })),
    tokenUsage: consumption ? `${count.format(consumption.todayTokens)} today` : null,
    estimatedCost: consumption ? `${money(consumption.todayCostUsd)} est. API` : null,
    creditsRemaining: reading.spending?.kind === 'credits' && reading.spending.remaining !== null
      ? `${reading.spending.currencySymbol}${reading.spending.remaining.toFixed(2)}` : null,
    portalUrl: portalUrl(reading.id),
    usageDetails: details,
    consumption,
  };
}

export function useDesktopData() {
  const [snapshot, setSnapshot] = useState<WireState | null>(null);
  const [settings, setSettings] = useState<SettingsSnapshot | null>(null);
  const [runtime, setRuntime] = useState<RuntimeInfo | null>(null);
  const [connection, setConnection] = useState<ConnectionState>({ connected: false, message: null });
  const [operationError, setOperationError] = useState<string | null>(null);
  const [persistence, setPersistence] = useState('');
  const saves = useRef(0);

  useEffect(() => {
    if (!desktopApi) return;
    let active = true;
    let generation = 0;
    let connectionGeneration = 0;
    const stateOff = desktopApi.onState(value => { generation++; if (active) setSnapshot(value); });
    const connectionOff = desktopApi.onConnection(value => { connectionGeneration++; if (active) setConnection(value); });
    const initialGeneration = generation;
    const initialConnection = connectionGeneration;
    void desktopApi.getRuntimeInfo().then(value => {
      if (active) { setRuntime(value); if (initialConnection === connectionGeneration) setConnection({ connected: value.connected, message: null }); }
    }).catch(() => { if (active) setConnection({ connected: false, message: 'Could not connect to the desktop host.' }); });
    void desktopApi.getState().then(value => { if (active && generation === initialGeneration) setSnapshot(value); }).catch(() => {
      if (active) setConnection({ connected: false, message: `The account engine is unavailable. Restart ${APP_NAME} to reconnect.` });
    });
    void desktopApi.getSettings().then(value => { if (active) setSettings(value); }).catch(() => { if (active) setOperationError('Could not load provider preferences.'); });
    return () => { active = false; stateOff(); connectionOff(); };
  }, []);

  const updateSettings = useCallback(async (patch: SettingsPatch) => {
    if (!desktopApi) return false;
    const serial = ++saves.current;
    setOperationError(null);
    setPersistence('Saving…');
    setSettings(current => current ? {
      ...current,
      ...patch,
      enabled: { ...current.enabled, ...patch.enabled },
      paths: { ...current.paths, ...patch.paths },
      notifications: { ...current.notifications, ...patch.notifications },
      antigravitySource: patch.antigravitySource ?? current.antigravitySource,
    } : current);
    try {
      const result = await desktopApi.updateSettings(patch);
      if (serial === saves.current) { setSettings(result); setPersistence('Saved'); }
      return true;
    } catch {
      if (serial === saves.current) { setPersistence('Could not save'); setOperationError('Could not save provider preferences. Check the settings location and try again.'); }
      return false;
    }
  }, []);

  const setStartWithWindows = useCallback(async (enabled: boolean) => {
    if (!desktopApi?.setStartWithWindows) {
      throw new Error('Start with Windows is not supported by the current host.');
    }
    const result = await desktopApi.setStartWithWindows(enabled);
    setRuntime(current => current ? { ...current, startWithWindows: result.enabled } : current);
    return result;
  }, []);

  const refresh = useCallback(async () => {
    if (!desktopApi) return;
    setOperationError(null);
    try { await desktopApi.refresh(); } catch { setOperationError('Could not refresh. Check the account engine connection.'); }
  }, []);

  return { snapshot, settings, runtime, connection, operationError, persistence, updateSettings, setStartWithWindows, refresh };
}
