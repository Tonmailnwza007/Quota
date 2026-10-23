export interface NamedBucket {
  name: string;
  percentage: number | null;
  detail?: string | null;
  resetLabel?: string | null;
}

export type ProviderStatus = 'ok' | 'unknown' | 'offline' | 'expired' | 'disabled';

export interface ProviderReading {
  id: string;
  displayName: string;
  family: 'codex' | 'antigravity' | 'claude' | 'copilot' | 'other';
  plan: string | null;
  accountEmail: string | null;
  accountId: string | null;
  isSharedAccountQuota?: boolean;
  sharedNotice?: string | null;
  status: ProviderStatus;
  statusMessage?: string | null;
  sessionProgress: number | null; // 0-100 or null if unknown
  sessionResetLabel?: string | null;
  weeklyProgress: number | null; // 0-100 or null if unknown
  weeklyResetLabel?: string | null;
  namedBuckets?: NamedBucket[];
  tokenUsage?: string | null;
  estimatedCost?: string | null;
  creditsRemaining?: string | null;
  portalUrl?: string | null;
  lastUpdated: string;
  sourceLabel?: string;
  usageDetails?: { label: string; value: string }[];
  consumption?: import('../../contracts').ConsumptionDetails | null;
}

export interface DashboardState {
  providers: ProviderReading[];
  isRefreshing: boolean;
  lastRefreshTime: string;
  globalErrors: string[];
}
