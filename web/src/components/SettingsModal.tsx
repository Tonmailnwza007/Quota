import { useEffect, useRef, useState } from 'react';
import { X, Sliders, ChevronDown, ChevronRight, Bell } from 'lucide-react';
import type { AdvancedPaths, NotificationSettings, AntigravitySource, ProviderKey } from '../../../contracts';

interface SettingsProps {
  isOpen: boolean;
  onClose: () => void;
  switches: Record<string, boolean>;
  onToggleSwitch: (key: string) => void;
  isDesktop?: boolean;
  settingsReady?: boolean;
  persistence?: string;
  startWithWindows?: boolean | null;
  onSetStartWithWindows?: (enabled: boolean) => Promise<{ enabled: boolean }>;
  paths?: AdvancedPaths;
  refreshMinutes?: number;
  antigravitySource?: AntigravitySource;
  onSavePaths?: (paths: AdvancedPaths, minutes: number, source: 'saved' | 'custom') => Promise<void>;
  showUsageDetails: boolean;
  onToggleShowUsageDetails: (value: boolean) => void;
  notifications: NotificationSettings;
  onSaveNotifications: (patch: Partial<NotificationSettings>) => Promise<void> | void;
  onTestNotification: () => Promise<{ accepted: boolean }>;
}

const codexFields: [keyof AdvancedPaths, string][] = [
  ['codexAuthPath', 'Codex App auth JSON'],
  ['codexLogDir', 'Codex App session directory'],
  ['codexCliHome', 'Codex CLI home'],
  ['codexCliAuthPath', 'Codex CLI auth JSON'],
  ['codexCliLogDir', 'Codex CLI session directory'],
  ['codexDesktopZeronAccountId', 'Codex App Zeron account ID'],
  ['codexCliZeronAccountId', 'Codex CLI Zeron account ID'],
];

const customAntigravityFields: [keyof AdvancedPaths, string][] = [
  ['antigravityAuthPath', 'Antigravity auth JSON'],
  ['antigravityCredentialTarget', 'Antigravity credential target'],
];

const providers: [ProviderKey, string, string][] = [
  ['codex', 'Codex App', 'Account signed in to the Codex app'],
  ['codex-cli', 'Codex CLI', 'Your separate Codex CLI account'],
  ['antigravity', 'Antigravity', 'Quotas in the background using your saved sign-in'],
  ['claude', 'Claude', 'Claude and discovered account profiles'],
  ['copilot', 'GitHub Copilot', 'Your saved Copilot subscription'],
];

export function SettingsModal({
  isOpen,
  onClose,
  switches,
  onToggleSwitch,
  isDesktop = false,
  settingsReady = true,
  persistence = '',
  startWithWindows,
  onSetStartWithWindows,
  paths,
  refreshMinutes = 5,
  antigravitySource = 'saved',
  onSavePaths,
  showUsageDetails,
  onToggleShowUsageDetails,
  notifications,
  onSaveNotifications,
  onTestNotification,
}: SettingsProps) {
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [draft, setDraft] = useState<AdvancedPaths>(() =>
    paths
      ? { ...paths }
      : ({
          codexAuthPath: '',
          codexLogDir: '',
          codexCliHome: '',
          codexCliAuthPath: '',
          codexCliLogDir: '',
          codexDesktopZeronAccountId: '',
          codexCliZeronAccountId: '',
          antigravityAuthPath: '',
          antigravityCredentialTarget: '',
        } as AdvancedPaths)
  );
  const [sourceDraft, setSourceDraft] = useState<AntigravitySource>(antigravitySource);
  const [minutes, setMinutes] = useState(refreshMinutes);
  const [applying, setApplying] = useState(false);
  const [localError, setLocalError] = useState('');
  const [savingStartup, setSavingStartup] = useState(false);
  const [startupError, setStartupError] = useState('');

  // Notifications threshold draft and feedback state
  const [warningDraft, setWarningDraft] = useState(() => String(notifications?.warningPercent ?? 10));
  const [criticalDraft, setCriticalDraft] = useState(() => String(notifications?.criticalPercent ?? 5));
  const [alertError, setAlertError] = useState('');
  const [alertStatus, setAlertStatus] = useState('');
  const [savingAlerts, setSavingAlerts] = useState(false);
  const [testingNotification, setTestingNotification] = useState(false);

  const prevExternalNotifications = useRef(notifications);
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const initialized = useRef(Boolean(paths));

  useEffect(() => {
    if (paths && !initialized.current) {
      initialized.current = true;
      setDraft({ ...paths });
      setMinutes(refreshMinutes);
      setSourceDraft(antigravitySource);
    }
  }, [paths, refreshMinutes, antigravitySource]);

  // Keep notification thresholds synced with external settings without overwriting active user edits
  useEffect(() => {
    if (notifications.warningPercent !== prevExternalNotifications.current.warningPercent) {
      setWarningDraft(String(notifications.warningPercent));
    }
    if (notifications.criticalPercent !== prevExternalNotifications.current.criticalPercent) {
      setCriticalDraft(String(notifications.criticalPercent));
    }
    prevExternalNotifications.current = notifications;
  }, [notifications]);

  useEffect(() => {
    if (!isOpen) return;
    const previous = document.activeElement as HTMLElement | null;
    closeRef.current?.focus();

    const keydown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        onClose();
        return;
      }
      if (event.key !== 'Tab' || !dialogRef.current) return;
      const focusable = [
        ...dialogRef.current.querySelectorAll<HTMLElement>(
          'button:not(:disabled), [href], input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex="0"]'
        ),
      ].filter(element => element.getClientRects().length > 0);
      const first = focusable[0];
      const last = focusable.at(-1);
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last?.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first?.focus();
      }
    };

    window.addEventListener('keydown', keydown);
    return () => {
      window.removeEventListener('keydown', keydown);
      previous?.focus();
    };
  }, [isOpen, onClose]);

  if (!isOpen) return null;

  const toggleStartup = async () => {
    if (!onSetStartWithWindows || typeof startWithWindows !== 'boolean' || savingStartup) return;
    setSavingStartup(true);
    setStartupError('');
    try { await onSetStartWithWindows(!startWithWindows); }
    catch { setStartupError('Could not change Windows startup. Try again.'); }
    finally { setSavingStartup(false); }
  };

  const savePaths = async () => {
    if (!onSavePaths) return;
    if (sourceDraft === 'choose') {
      setLocalError('Please select either "Antigravity saved sign-in" or "Custom credential source".');
      return;
    }
    if (sourceDraft === 'custom' && !draft.antigravityAuthPath.trim() && !draft.antigravityCredentialTarget.trim()) {
      setLocalError('Enter an Antigravity auth JSON path or credential target.');
      return;
    }
    if (!Number.isInteger(minutes) || minutes < 1 || minutes > 60) {
      setLocalError('Choose an interval between 1 and 60 minutes.');
      return;
    }
    setApplying(true);
    setLocalError('');
    try {
      await onSavePaths(draft, minutes, sourceDraft);
    } catch {
      setLocalError('Could not save source preferences.');
    } finally {
      setApplying(false);
    }
  };

  const handleSaveAlertThresholds = async () => {
    setAlertError('');
    setAlertStatus('');
    const w = parseInt(warningDraft, 10);
    const c = parseInt(criticalDraft, 10);

    if (
      warningDraft.trim() === '' ||
      criticalDraft.trim() === '' ||
      isNaN(w) ||
      isNaN(c) ||
      String(w) !== warningDraft.trim() ||
      String(c) !== criticalDraft.trim()
    ) {
      setAlertError('Use whole percentages between 0 and 100.');
      return;
    }

    if (w < 0 || w > 100 || c < 0 || c > 100) {
      setAlertError('Thresholds must be between 0% and 100%.');
      return;
    }

    if (c >= w) {
      setAlertError('Critical % remaining must be less than Warning % remaining.');
      return;
    }

    setSavingAlerts(true);
    try {
      await onSaveNotifications({ warningPercent: w, criticalPercent: c });
      setAlertStatus('Alert thresholds saved');
    } catch {
      setAlertError('Could not save alert thresholds.');
    } finally {
      setSavingAlerts(false);
    }
  };

  const handleTestNotification = async () => {
    setTestingNotification(true);
    setAlertError('');
    setAlertStatus('');
    try {
      const res = await onTestNotification();
      if (res.accepted) {
        setAlertStatus(
          isDesktop
            ? 'Test notification requested. Check Windows notification settings if no banner appears.'
            : 'Preview notification simulated.'
        );
      } else {
        setAlertError('Notifications are unavailable in this app host.');
      }
    } catch {
      setAlertError('Could not send test notification');
    } finally {
      setTestingNotification(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-3 bg-black/75 backdrop-blur-sm" role="presentation">
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        data-tray-settings="true"
        aria-labelledby="settings-dialog-title"
        className="w-full max-w-[360px] rounded-xl p-4 flex flex-col max-h-[min(520px,calc(100dvh_-_24px))] bg-[#202020] border border-white/10 shadow-2xl relative overflow-hidden"
      >
        {/* Header */}
        <div className="flex items-center justify-between pb-2 border-b border-white/[0.08] shrink-0">
          <div className="flex items-center gap-2">
            <Sliders className="w-4 h-4 text-zinc-300" />
            <h2 id="settings-dialog-title" className="text-xs font-semibold text-zinc-100">
              Provider settings
            </h2>
          </div>
          <button
            ref={closeRef}
            type="button"
            onClick={onClose}
            aria-label="Close settings dialog"
            className="p-1 rounded text-zinc-400 hover:text-white hover:bg-white/[0.08] focus:outline-none focus:ring-1 focus:ring-zinc-400"
          >
            <X className="w-4 h-4" />
          </button>
        </div>

        {/* Scrollable Settings Body */}
        <div className="space-y-3 py-2 overflow-y-auto flex-1 pr-1 min-h-0">
          <p className="text-[11px] text-zinc-400">
            {isDesktop ? 'Choose which accounts appear. Switches apply immediately and are saved.' : 'Preview switches change this demo only.'}
          </p>

          {/* Visible cue when Antigravity source requires choice */}
          {antigravitySource === 'choose' && (
            <div role="alert" className="p-2 rounded-lg border border-amber-500/30 bg-amber-500/10 text-[11px] text-amber-200">
              Select the Antigravity account source before quotas can update. Open{' '}
              <button
                type="button"
                onClick={() => setAdvancedOpen(true)}
                className="underline font-medium hover:text-white focus:outline-none"
              >
                Advanced settings
              </button>{' '}
              to choose your credential source.
            </div>
          )}

          {/* 5 Provider Switches */}
          <div className="space-y-1.5 pt-0.5">
            {providers.map(([key, label, description]) => (
              <div key={key} className="flex items-center justify-between p-2 rounded-lg bg-[#282828] border border-white/[0.06] gap-2">
                <div className="min-w-0">
                  <p id={`label-${key}`} className="text-xs font-medium text-zinc-200 truncate">{label}</p>
                  <p className="text-[10px] text-zinc-400 truncate" title={description}>{description}</p>
                </div>
                <button
                  type="button"
                  role="switch"
                  aria-checked={switches[key] ?? true}
                  aria-labelledby={`label-${key}`}
                  disabled={!settingsReady}
                  onClick={() => onToggleSwitch(key)}
                  className={`w-8 h-4.5 rounded-full p-0.5 shrink-0 transition-colors focus:outline-none focus:ring-1 focus:ring-zinc-400 disabled:opacity-40 ${
                    switches[key] ? 'bg-zinc-200' : 'bg-zinc-800'
                  }`}
                >
                  <span
                    className={`block w-3.5 h-3.5 rounded-full transition-transform ${
                      switches[key] ? 'translate-x-3.5 bg-zinc-950' : 'translate-x-0 bg-zinc-400'
                    }`}
                  />
                </button>
              </div>
            ))}
          </div>

          {onSetStartWithWindows && (
            <div className="pt-2 border-t border-white/[0.08] space-y-1">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <p id="label-start-with-windows" className="text-xs font-medium text-zinc-200">Start with Windows</p>
                  <p id="startup-description" className="text-[10px] text-zinc-400 mt-0.5">
                    Starts hidden in the tray when you sign in. Keep this portable folder in place.
                  </p>
                </div>
                <button
                  type="button"
                  role="switch"
                  aria-checked={startWithWindows === true}
                  aria-labelledby="label-start-with-windows"
                  aria-describedby="startup-description"
                  aria-busy={savingStartup}
                  disabled={typeof startWithWindows !== 'boolean' || savingStartup}
                  onClick={() => { void toggleStartup(); }}
                  className={`w-8 h-4.5 rounded-full p-0.5 shrink-0 transition-colors focus:outline-none focus:ring-1 focus:ring-zinc-400 disabled:opacity-40 ${startWithWindows ? 'bg-zinc-200' : 'bg-zinc-800'}`}
                >
                  <span className={`block w-3.5 h-3.5 rounded-full transition-transform ${startWithWindows ? 'translate-x-3.5 bg-zinc-950' : 'translate-x-0 bg-zinc-400'}`} />
                </button>
              </div>
              {typeof startWithWindows !== 'boolean' && <p className="text-[10px] text-zinc-400">Windows startup setting is unavailable.</p>}
              {savingStartup && <p role="status" className="text-[10px] text-zinc-400">Saving startup preference…</p>}
              {startupError && <p role="alert" className="text-[10px] text-rose-300">{startupError}</p>}
            </div>
          )}

          {/* Low-Quota Notifications Section */}
          <div className="pt-2 border-t border-white/[0.08] space-y-2">
            <div className="flex items-start justify-between gap-2">
              <div className="min-w-0">
                <div className="flex items-center gap-1.5">
                  <Bell className="w-3.5 h-3.5 text-zinc-300" />
                  <p id="label-notifications" className="text-xs font-medium text-zinc-200">Low-quota notifications</p>
                </div>
                <p className="text-[10px] text-zinc-400 mt-0.5">
                  Alerts follow successful refreshes for enabled providers. Quota must remain running in tray.
                </p>
              </div>
              <button
                type="button"
                role="switch"
                aria-checked={notifications.enabled}
                aria-labelledby="label-notifications"
                disabled={!settingsReady}
                onClick={() => {
                  setAlertError('');
                  void Promise.resolve(onSaveNotifications({ enabled: !notifications.enabled })).catch(() =>
                    setAlertError('Could not save notification preferences.')
                  );
                }}
                className={`w-8 h-4.5 rounded-full p-0.5 shrink-0 transition-colors focus:outline-none focus:ring-1 focus:ring-zinc-400 disabled:opacity-40 ${
                  notifications.enabled ? 'bg-zinc-200' : 'bg-zinc-800'
                }`}
              >
                <span
                  className={`block w-3.5 h-3.5 rounded-full transition-transform ${
                    notifications.enabled ? 'translate-x-3.5 bg-zinc-950' : 'translate-x-0 bg-zinc-400'
                  }`}
                />
              </button>
            </div>

            {/* Threshold Inputs & Actions */}
            <div className="p-2.5 rounded-lg bg-[#282828] border border-white/[0.06] space-y-2">
              <div className="grid grid-cols-2 gap-2">
                <div>
                  <label htmlFor="warning-threshold-input" className="block text-[10px] text-zinc-300 mb-1">
                    Warning % remaining
                  </label>
                  <input
                    id="warning-threshold-input"
                    type="number"
                    min={0}
                    max={100}
                    disabled={!settingsReady || !notifications.enabled}
                    value={warningDraft}
                    onChange={event => {
                      setWarningDraft(event.target.value);
                      setAlertError('');
                      setAlertStatus('');
                    }}
                    className="block w-full px-2 py-1 rounded-md bg-white/[0.04] border border-white/10 text-xs font-mono text-zinc-200 focus:outline-none focus:border-zinc-400 disabled:opacity-40"
                  />
                </div>
                <div>
                  <label htmlFor="critical-threshold-input" className="block text-[10px] text-zinc-300 mb-1">
                    Critical % remaining
                  </label>
                  <input
                    id="critical-threshold-input"
                    type="number"
                    min={0}
                    max={100}
                    disabled={!settingsReady || !notifications.enabled}
                    value={criticalDraft}
                    onChange={event => {
                      setCriticalDraft(event.target.value);
                      setAlertError('');
                      setAlertStatus('');
                    }}
                    className="block w-full px-2 py-1 rounded-md bg-white/[0.04] border border-white/10 text-xs font-mono text-zinc-200 focus:outline-none focus:border-zinc-400 disabled:opacity-40"
                  />
                </div>
              </div>

              <div className="flex items-center gap-2 pt-0.5">
                <button
                  type="button"
                  disabled={savingAlerts || !settingsReady || !notifications.enabled}
                  onClick={() => { void handleSaveAlertThresholds(); }}
                  className="px-3 py-1 rounded-md bg-white/10 hover:bg-white/15 text-xs text-zinc-200 font-medium disabled:opacity-40 transition-colors"
                >
                  {savingAlerts ? 'Saving…' : 'Save alerts'}
                </button>
                <button
                  type="button"
                  disabled={testingNotification || !settingsReady || !notifications.enabled}
                  onClick={() => { void handleTestNotification(); }}
                  className="px-3 py-1 rounded-md bg-white/[0.04] hover:bg-white/[0.08] border border-white/10 text-xs text-zinc-300 hover:text-white font-medium disabled:opacity-40 transition-colors"
                >
                  {testingNotification ? 'Testing…' : 'Test notification'}
                </button>
              </div>

              {alertError && <p role="alert" className="text-[10px] text-rose-400">{alertError}</p>}
              {alertStatus && <p role="status" className="text-[10px] text-emerald-400">{alertStatus}</p>}
            </div>
          </div>

          {/* Advanced source settings */}
          <div className="pt-2 border-t border-white/[0.08]">
            <button
              type="button"
              onClick={() => setAdvancedOpen(previous => !previous)}
              aria-expanded={advancedOpen}
              className="flex items-center gap-1.5 w-full py-1 text-xs text-zinc-400 hover:text-zinc-200 focus:outline-none focus:ring-1 focus:ring-zinc-400 rounded"
            >
              {advancedOpen ? <ChevronDown className="w-3.5 h-3.5" /> : <ChevronRight className="w-3.5 h-3.5" />}
              <span>Advanced settings</span>
            </button>

            {advancedOpen && (
              <div className="mt-2 p-2.5 rounded-lg bg-[#181818] border border-white/5 space-y-2.5">
                {/* Show usage details switch */}
                <div className="flex items-center justify-between p-2 rounded-lg bg-[#282828] border border-white/[0.06] gap-2">
                  <div className="min-w-0">
                    <p id="label-show-usage-details" className="text-xs font-medium text-zinc-200">Show usage details</p>
                    <p className="text-[10px] text-zinc-400">Display token counts, estimated API costs, and account references</p>
                  </div>
                  <button
                    type="button"
                    role="switch"
                    aria-checked={showUsageDetails}
                    aria-labelledby="label-show-usage-details"
                    disabled={!settingsReady}
                    onClick={() => onToggleShowUsageDetails(!showUsageDetails)}
                    className={`w-8 h-4.5 rounded-full p-0.5 shrink-0 transition-colors focus:outline-none focus:ring-1 focus:ring-zinc-400 disabled:opacity-40 ${
                      showUsageDetails ? 'bg-zinc-200' : 'bg-zinc-800'
                    }`}
                  >
                    <span
                      className={`block w-3.5 h-3.5 rounded-full transition-transform ${
                        showUsageDetails ? 'translate-x-3.5 bg-zinc-950' : 'translate-x-0 bg-zinc-400'
                      }`}
                    />
                  </button>
                </div>

                {/* Antigravity Source Chooser */}
                <div className="space-y-1">
                  <label htmlFor="antigravity-source-select" className="block text-[10px] text-zinc-300">
                    Antigravity credential source
                  </label>
                  <select
                    id="antigravity-source-select"
                    value={sourceDraft}
                    disabled={!settingsReady}
                    onChange={e => {
                      setSourceDraft(e.target.value as AntigravitySource);
                      setLocalError('');
                    }}
                    className="w-full px-2 py-1 rounded-md bg-white/[0.04] border border-white/10 text-xs text-zinc-200 focus:outline-none focus:border-zinc-400 disabled:opacity-40"
                  >
                    {sourceDraft === 'choose' && (
                      <option value="choose" disabled>
                        Choose credential source…
                      </option>
                    )}
                    <option value="saved">Antigravity saved sign-in</option>
                    <option value="custom">Custom credential source</option>
                  </select>
                  <p className="text-[10px] text-zinc-400">
                    {sourceDraft === 'saved'
                      ? 'Reads quota in the background using your saved sign-in.'
                      : sourceDraft === 'custom'
                      ? 'Provide an auth JSON file or credential target. The file takes priority if both are set.'
                      : 'Please choose a source option before saving.'}
                  </p>
                </div>

                <p className="text-[10px] text-zinc-400">
                  Leave blank for automatic detection. Configured paths must point to valid files.
                </p>

                {/* Codex Advanced Paths */}
                {codexFields.map(([key, label]) => (
                  <div key={key}>
                    <label htmlFor={`path-${key}`} className="block text-[10px] text-zinc-300 mb-1">{label}</label>
                    <input
                      id={`path-${key}`}
                      type="text"
                      value={draft[key]}
                      placeholder="Automatic"
                      maxLength={4096}
                      title={draft[key]}
                      disabled={!settingsReady}
                      onChange={event => setDraft(previous => ({ ...previous, [key]: event.target.value }))}
                      className="min-w-0 w-full px-2 py-1 rounded-md bg-white/[0.04] border border-white/10 text-[11px] font-mono text-zinc-200 focus:outline-none focus:border-zinc-400 placeholder:text-zinc-600"
                    />
                  </div>
                ))}

                {/* Custom Antigravity Paths (only visible when custom selected, drafts preserved when saved) */}
                {sourceDraft === 'custom' && (
                  <div className="space-y-2 pt-1 border-t border-white/5">
                    <p className="text-[10px] font-medium text-zinc-300">Custom Antigravity credentials</p>
                    {customAntigravityFields.map(([key, label]) => (
                      <div key={key}>
                        <label htmlFor={`path-${key}`} className="block text-[10px] text-zinc-300 mb-1">{label}</label>
                        <input
                          id={`path-${key}`}
                          type="text"
                          value={draft[key]}
                          placeholder="Automatic"
                          maxLength={4096}
                          title={draft[key]}
                          disabled={!settingsReady}
                          onChange={event => setDraft(previous => ({ ...previous, [key]: event.target.value }))}
                          className="min-w-0 w-full px-2 py-1 rounded-md bg-white/[0.04] border border-white/10 text-[11px] font-mono text-zinc-200 focus:outline-none focus:border-zinc-400 placeholder:text-zinc-600"
                        />
                      </div>
                    ))}
                  </div>
                )}

                <div>
                  <label htmlFor="refresh-minutes-input" className="block text-[10px] text-zinc-300 mb-1">
                    Refresh interval (minutes)
                  </label>
                  <input
                    id="refresh-minutes-input"
                    type="number"
                    min={1}
                    max={60}
                    value={minutes}
                    onChange={event => setMinutes(Number(event.target.value))}
                    className="block px-2 py-1 w-20 rounded-md bg-white/[0.04] border border-white/10 text-xs font-mono text-zinc-200"
                  />
                </div>

                {onSavePaths && (
                  <button
                    type="button"
                    disabled={applying || !settingsReady || sourceDraft === 'choose'}
                    onClick={() => { void savePaths(); }}
                    className="px-3 py-1 rounded-md bg-white/10 hover:bg-white/15 text-xs text-zinc-200 font-medium disabled:opacity-40"
                  >
                    {applying ? 'Saving…' : 'Save source settings'}
                  </button>
                )}
              </div>
            )}
          </div>
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between gap-2 pt-2 border-t border-white/[0.08] shrink-0">
          <span role="status" className="text-[10px] text-zinc-400 truncate">
            {startupError || localError || persistence || (!isDesktop ? 'Preview mode' : '')}
          </span>
          <button
            type="button"
            onClick={onClose}
            className="px-3.5 py-1.5 rounded-md bg-white text-zinc-950 font-medium text-xs hover:bg-zinc-200 focus:outline-none focus:ring-1 focus:ring-zinc-400"
          >
            Done
          </button>
        </div>
      </div>
    </div>
  );
}
