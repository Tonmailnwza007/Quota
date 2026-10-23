import React from 'react';
import { RefreshCw, Settings, X } from 'lucide-react';

interface HeaderProps {
  appName: string;
  isRefreshing: boolean;
  onRefresh: () => void;
  onOpenSettings: () => void;
  isDesktop?: boolean;
  isDemo?: boolean;
  onHide?: () => void;
}

export const Header: React.FC<HeaderProps> = ({
  appName,
  isRefreshing,
  onRefresh,
  onOpenSettings,
  isDesktop = false,
  isDemo = true,
  onHide,
}) => {
  return (
    <header className="flex items-center justify-between pb-1">
      {/* Title & Status */}
      <div className="flex items-center gap-2">
        <span className="text-[13px] font-bold tracking-tight text-white">
          {appName}
        </span>
        {(!isDesktop || isDemo) && <span className="text-[9px] text-zinc-400">{isDesktop ? 'Demo' : 'Preview'}</span>}
      </div>

      {/* Action buttons: Refresh, Settings, Hide */}
      <div className="flex items-center gap-0.5">
        <button
          type="button"
          onClick={onRefresh}
          disabled={isRefreshing}
          className="p-1.5 rounded-md text-zinc-400 hover:text-white hover:bg-white/[0.08] transition-colors disabled:opacity-40 focus:outline-none focus:ring-1 focus:ring-zinc-400"
          aria-label={isDesktop ? 'Refresh usage' : 'Refresh preview data'}
          title={isDesktop ? 'Refresh usage' : 'Refresh preview data'}
        >
          <RefreshCw className={`w-3.5 h-3.5 ${isRefreshing ? 'animate-spin text-zinc-200' : ''}`} />
        </button>

        <button
          type="button"
          onClick={onOpenSettings}
          className="p-1.5 rounded-md text-zinc-400 hover:text-white hover:bg-white/[0.08] transition-colors focus:outline-none focus:ring-1 focus:ring-zinc-400"
          aria-label="Open provider settings"
          title="Open provider settings"
        >
          <Settings className="w-3.5 h-3.5" />
        </button>

        {onHide && (
          <button
            type="button"
            onClick={onHide}
            aria-label="Hide popover"
            title="Hide popover"
            className="p-1.5 rounded-md text-zinc-400 hover:text-white hover:bg-white/[0.08] transition-colors focus:outline-none focus:ring-1 focus:ring-zinc-400"
          >
            <X className="w-3.5 h-3.5" />
          </button>
        )}
      </div>
    </header>
  );
};
