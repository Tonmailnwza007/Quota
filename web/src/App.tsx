import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { Header } from './components/Header';
import { ProviderCard } from './components/ProviderCard';
import { SettingsModal } from './components/SettingsModal';
import { EmptyState } from './components/EmptyState';
import { LoadingSkeleton } from './components/LoadingSkeleton';
import { SCENARIO_MULTI_ACCOUNT, SCENARIO_EDGE_CASES } from './fixtures';
import { DEFAULT_SWITCHES, desktopApi, mapReading, money, relativeTime, useDesktopData } from './desktopAdapter';
import { APP_NAME } from './brand';
import type { ProviderReading } from './types';
import type { ProviderKey, NotificationSettings, AntigravitySource, AdvancedPaths } from '../../contracts';

const ORDER: Record<string, number> = {
  codex: 1,
  'codex-cli': 2,
  antigravity: 3,
  claude: 4,
  copilot: 5,
};

const DEFAULT_NOTIFICATIONS: NotificationSettings = {
  enabled: true,
  warningPercent: 10,
  criticalPercent: 5,
};

const rank = (provider: ProviderReading) => ORDER[provider.id] ?? ORDER[provider.family] ?? 100;

export default function App() {
  const desktop = useDesktopData();
  const native = Boolean(desktopApi);
  const [scenario, setScenario] = useState('multi');
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [previewRefreshing, setPreviewRefreshing] = useState(false);
  const [previewSwitches, setPreviewSwitches] = useState<Record<ProviderKey, boolean>>(DEFAULT_SWITCHES);
  const [previewShowUsageDetails, setPreviewShowUsageDetails] = useState(false);
  const [previewNotifications, setPreviewNotifications] = useState<NotificationSettings>(DEFAULT_NOTIFICATIONS);
  const [previewAntigravitySource, setPreviewAntigravitySource] = useState<AntigravitySource>('saved');
  const [now, setNow] = useState(Date.now);

  const closeSettings = useCallback(() => setSettingsOpen(false), []);

  useEffect(() => {
    document.title = `${APP_NAME} • AI usage`;
  }, []);

  useEffect(() => {
    const dismiss = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !settingsOpen && desktopApi) {
        event.preventDefault();
        void desktopApi.hide();
      }
    };
    window.addEventListener('keydown', dismiss);
    return () => window.removeEventListener('keydown', dismiss);
  }, [settingsOpen]);

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 30000);
    return () => clearInterval(timer);
  }, []);

  const switches = (native ? desktop.settings?.enabled ?? DEFAULT_SWITCHES : previewSwitches) as Record<string, boolean>;
  const showUsageDetails = native ? desktop.settings?.showUsageDetails ?? false : previewShowUsageDetails;
  const notifications = native ? desktop.settings?.notifications ?? DEFAULT_NOTIFICATIONS : previewNotifications;
  const antigravitySource = native ? desktop.settings?.antigravitySource ?? 'saved' : previewAntigravitySource;

  const providers = useMemo(() => {
    const raw = native
      ? (desktop.snapshot?.providers ?? []).map(item => mapReading(item, now))
      : scenario === 'multi'
      ? SCENARIO_MULTI_ACCOUNT
      : scenario === 'edge'
      ? SCENARIO_EDGE_CASES
      : [];

    return raw
      .filter(provider => switches[provider.id.startsWith('claude:') ? 'claude' : (provider.id as ProviderKey)] ?? true)
      .sort(
        (a, b) =>
          rank(a) - rank(b) ||
          (a.family === 'claude' && b.family === 'claude' ? (b.sessionProgress ?? -1) - (a.sessionProgress ?? -1) : 0) ||
          a.displayName.localeCompare(b.displayName)
      );
  }, [native, desktop.snapshot, now, scenario, switches]);

  const claude = providers.filter(item => item.id.startsWith('claude:'));
  const nearLimit = claude.filter(item => Math.max(item.sessionProgress ?? -1, item.weeklyProgress ?? -1) >= 80).length;
  const claudeKnown = claude.some(item => item.sessionProgress !== null || item.weeklyProgress !== null);
  const hasClaudeTotals = claude.length > 1 && claude.every(item => item.consumption != null);
  const firstClaude = claude[0]?.id;

  const refreshing = native ? desktop.snapshot?.isRefreshing ?? !desktop.connection.message : previewRefreshing;
  const loading = native ? !providers.length && refreshing : scenario === 'loading';
  const lastUpdated = native
    ? desktop.connection.message
      ? 'Account engine disconnected'
      : `${desktop.snapshot?.mode === 'demo' ? 'Demo accounts • ' : ''}${relativeTime(desktop.snapshot?.lastRefreshTime ?? null, now)}`
    : 'Updated just now';

  const errors = [desktop.connection.message, desktop.operationError, ...(desktop.snapshot?.globalErrors ?? [])].filter(Boolean);

  const refresh = () => {
    if (native) {
      void desktop.refresh();
      return;
    }
    setPreviewRefreshing(true);
    window.setTimeout(() => setPreviewRefreshing(false), 450);
  };

  const toggle = (key: string) => {
    if (native) {
      void desktop.updateSettings({ enabled: { [key as ProviderKey]: !switches[key] } });
      return;
    }
    setPreviewSwitches(previous => ({ ...previous, [key as ProviderKey]: !previous[key as ProviderKey] }));
  };

  const toggleShowUsageDetails = (value: boolean) => {
    if (native) {
      void desktop.updateSettings({ showUsageDetails: value });
      return;
    }
    setPreviewShowUsageDetails(value);
  };

  const saveNotifications = async (patch: Partial<NotificationSettings>) => {
    if (native) {
      if (!await desktop.updateSettings({ notifications: patch })) throw new Error('Alert settings could not be saved.');
      return;
    }
    setPreviewNotifications(previous => ({ ...previous, ...patch }));
  };

  const savePathsAndSource = async (paths: AdvancedPaths, minutes: number, source: 'saved' | 'custom') => {
    if (native) {
      if (!await desktop.updateSettings({ paths, refreshMinutes: minutes, antigravitySource: source })) throw new Error('Source settings could not be saved.');
      return;
    }
    setPreviewAntigravitySource(source);
  };

  const testNotification = async (): Promise<{ accepted: boolean }> => {
    if (native && desktopApi?.testNotification) {
      return await desktopApi.testNotification();
    }
    // Simulate test notification feedback in browser preview
    await new Promise(resolve => setTimeout(resolve, 200));
    return { accepted: true };
  };

  return (
    <div className="flex flex-col items-center">
      {/* Main product panel adhering strictly to fixed 360x520 normal tray height */}
      <div
        data-tray-content
        inert={settingsOpen}
        className="w-[360px] h-[520px] max-h-[100dvh] bg-[#202020] text-zinc-200 p-[14px] flex flex-col font-sans select-none antialiased relative rounded-xl box-border overflow-hidden"
      >
        <Header
          appName={APP_NAME}
          isRefreshing={refreshing}
          onRefresh={refresh}
          onOpenSettings={() => setSettingsOpen(true)}
          isDesktop={native}
          isDemo={desktop.snapshot?.mode === 'demo'}
        />

        {native && errors.length > 0 && (
          <div role="alert" className="mt-2 shrink-0 rounded-lg border border-amber-500/20 bg-amber-500/10 p-2 text-[10px] text-amber-200">
            {errors.join(' ')}
          </div>
        )}

        {/* Scrollable body filling remaining space; footer stays pinned to bottom */}
        <div className="flex-1 my-2 min-h-0 overflow-y-auto pr-0.5">
          {loading ? (
            <div aria-busy="true" aria-label="Loading account quotas" className="py-2">
              <LoadingSkeleton />
            </div>
          ) : providers.length === 0 ? (
            <div className="h-full flex items-center justify-center">
              <EmptyState onOpenSettings={() => setSettingsOpen(true)} />
            </div>
          ) : (
            <div className="flex flex-col gap-2">
              {providers.map((provider, index) => {
                // Codex Desktop & Codex CLI counterpart notice check
                const counterpart =
                  provider.id === 'codex'
                    ? 'codex-cli'
                    : provider.id === 'codex-cli'
                    ? 'codex'
                    : null;

                return (
                  <Fragment key={provider.id}>
                    {index > 0 && <div className="border-t border-white/[0.08]" />}
                    {claude.length > 1 && provider.id === firstClaude && (
                      <div className="flex flex-wrap items-center justify-between gap-1 px-2 py-1 rounded bg-[#282828] border border-white/[0.06] text-[10px] text-zinc-300">
                        <span className="font-medium text-zinc-300">Claude profiles</span>
                        <span className="font-mono text-zinc-400">
                          {claude.length} profiles • {claudeKnown ? `${nearLimit} near limit` : 'Quota unavailable'}
                        </span>
                        {showUsageDetails && hasClaudeTotals && (
                          <span className="basis-full text-zinc-500 text-[9px]">
                            est. API cost: {money(claude.reduce((total, item) => total + item.consumption!.todayCostUsd, 0))} today •{' '}
                            {money(claude.reduce((total, item) => total + item.consumption!.weekCostUsd, 0))} this week
                          </span>
                        )}
                      </div>
                    )}
                    <ProviderCard
                      provider={provider}
                      hideSharedNotice={counterpart !== null && !switches[counterpart as ProviderKey]}
                      showUsageDetails={showUsageDetails}
                    />
                  </Fragment>
                );
              })}
            </div>
          )}
        </div>

        <footer className="pt-1.5 border-t border-white/[0.06] shrink-0 text-center text-[10px] font-sans text-zinc-500 truncate">
          {lastUpdated}
        </footer>
      </div>

      {/* Browser-only synthetic scenario switcher */}
      {!native && (
        <div className="w-[360px] mt-2.5 mb-4 p-2 bg-[#181818] border border-white/5 rounded-lg flex items-center justify-between text-[11px] text-zinc-400">
          <span>Synthetic scenario:</span>
          <select
            value={scenario}
            onChange={e => setScenario(e.target.value)}
            className="bg-[#282828] border border-white/10 rounded px-2 py-0.5 text-zinc-200 text-xs focus:outline-none"
          >
            <option value="multi">Multi-account (2 Plus + Antigravity)</option>
            <option value="edge">Edge cases (Rate limit / Expired)</option>
            <option value="loading">Loading state</option>
          </select>
        </div>
      )}

      {settingsOpen && (
        <SettingsModal
          isOpen
          onClose={closeSettings}
          switches={switches}
          onToggleSwitch={toggle}
          isDesktop={native}
          settingsReady={!native || desktop.settings !== null}
          persistence={desktop.persistence}
          startWithWindows={desktop.runtime?.startWithWindows}
          onSetStartWithWindows={desktopApi?.setStartWithWindows ? desktop.setStartWithWindows : undefined}
          paths={desktop.settings?.paths}
          refreshMinutes={desktop.settings?.refreshMinutes}
          antigravitySource={antigravitySource}
          onSavePaths={savePathsAndSource}
          showUsageDetails={showUsageDetails}
          onToggleShowUsageDetails={toggleShowUsageDetails}
          notifications={notifications}
          onSaveNotifications={saveNotifications}
          onTestNotification={testNotification}
        />
      )}
    </div>
  );
}
