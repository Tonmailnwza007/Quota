export type ProviderKey = 'codex' | 'codex-cli' | 'antigravity' | 'claude' | 'copilot';
export type AntigravitySource = 'saved' | 'custom' | 'choose';
export type ProviderFamily = 'codex' | 'antigravity' | 'claude' | 'copilot' | 'other';
export type ReadingStatus = 'ok' | 'unknown' | 'offline' | 'expired' | 'error';

export interface NamedBucket {
  id: string;
  name: string;
  percentage: number | null;
  resetAt: string | null;
  detail: string | null;
}

export interface ConsumptionDetails {
  todayTokens: number;
  todayCostUsd: number;
  monthTokens: number;
  monthCostUsd: number;
  weekTokens: number;
  weekCostUsd: number;
}

export interface SpendingDetails {
  kind: 'credits' | 'overage';
  consumed: number;
  limit: number | null;
  remaining: number | null;
  currencySymbol: string;
}

export interface ProviderReading {
  id: string;
  displayName: string;
  family: ProviderFamily;
  plan: string | null;
  accountEmail: string | null;
  accountId: string | null;
  sourceLabel: string;
  status: ReadingStatus;
  statusMessage: string | null;
  isSharedAccountQuota: boolean;
  sharedNotice: string | null;
  sessionProgress: number | null;
  weeklyProgress: number | null;
  sessionResetAt: string | null;
  weeklyResetAt: string | null;
  namedBuckets: NamedBucket[];
  lastUpdated: string;
  consumption: ConsumptionDetails | null;
  spending: SpendingDetails | null;
}

export interface DashboardState {
  protocolVersion: 1;
  mode: 'live' | 'demo';
  providers: ProviderReading[];
  isRefreshing: boolean;
  lastRefreshTime: string | null;
  globalErrors: string[];
}

export interface AdvancedPaths {
  codexAuthPath: string;
  codexLogDir: string;
  codexCliHome: string;
  codexCliAuthPath: string;
  codexCliLogDir: string;
  codexDesktopZeronAccountId: string;
  codexCliZeronAccountId: string;
  antigravityAuthPath: string;
  antigravityCredentialTarget: string;
}

export interface NotificationSettings {
  enabled: boolean;
  warningPercent: number;
  criticalPercent: number;
}

export interface SettingsSnapshot {
  enabled: Record<ProviderKey, boolean>;
  refreshMinutes: number;
  paths: AdvancedPaths;
  showUsageDetails: boolean;
  notifications: NotificationSettings;
  antigravitySource: AntigravitySource;
}

export interface SettingsPatch {
  enabled?: Partial<Record<ProviderKey, boolean>>;
  refreshMinutes?: number;
  paths?: Partial<AdvancedPaths>;
  showUsageDetails?: boolean;
  notifications?: Partial<NotificationSettings>;
  antigravitySource?: Exclude<AntigravitySource, 'choose'>;
}

export interface RuntimeInfo {
  version: string;
  connected: boolean;
  hotkeyAvailable: boolean;
  demo: boolean;
  startWithWindows?: boolean | null;
}

export interface ConnectionState { connected: boolean; message: string | null }

export interface CostatsApi {
  getState(): Promise<DashboardState>;
  refresh(): Promise<{ accepted: boolean }>;
  getSettings(): Promise<SettingsSnapshot>;
  updateSettings(patch: SettingsPatch): Promise<SettingsSnapshot>;
  onState(listener: (state: DashboardState) => void): () => void;
  onConnection(listener: (state: ConnectionState) => void): () => void;
  getRuntimeInfo(): Promise<RuntimeInfo>;
  setStartWithWindows?(enabled: boolean): Promise<{ enabled: boolean }>;
  hide(): Promise<void>;
  openUsage(providerId: string): Promise<void>;
  chooseDirectory(): Promise<string | null>;
  testNotification(): Promise<{ accepted: boolean }>;
}

declare global { interface Window { costats?: CostatsApi } }
