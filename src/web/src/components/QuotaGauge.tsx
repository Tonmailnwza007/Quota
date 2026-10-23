interface QuotaGaugeProps {
  label: string;
  usedPercent: number | null | undefined;
  resetLabel?: string | null;
  type?: 'session' | 'weekly' | 'default';
  compact?: boolean;
}

export function QuotaGauge({ label, usedPercent, resetLabel, type = 'default', compact = false }: QuotaGaugeProps) {
  const used = typeof usedPercent === 'number' && Number.isFinite(usedPercent)
    ? Math.max(0, Math.min(100, usedPercent)) : null;
  const remaining = used === null ? null : Math.round((100 - used) * 10) / 10;
  const metric = remaining === null ? '--%' : `${remaining}%`;
  const color = used === null ? '#85858b' : used >= 95 ? '#ef7377' : used >= 80 ? '#e5ad55'
    : type === 'weekly' ? '#a687e9' : '#57b77d';
  const arc = 2 * Math.PI * 18 * 2 / 3;
  return (
    <div role="group" aria-label={`${label}: ${metric} remaining`}
      className={`quota-row flex items-center gap-2.5 min-w-0 ${compact ? 'py-1' : 'py-1.5'}`}>
      <svg width="36" height="36" viewBox="0 0 48 48" aria-hidden="true" className="shrink-0">
        <circle cx="24" cy="24" r="18" fill="none" stroke="#424244" strokeWidth="4"
          strokeDasharray={`${arc} 114`} strokeLinecap="round" transform="rotate(150 24 24)" />
        {remaining !== null && remaining > 0 && <circle cx="24" cy="24" r="18" fill="none"
          stroke={color} strokeWidth="4" strokeDasharray={`${arc * remaining / 100} 114`}
          strokeLinecap="round" transform="rotate(150 24 24)" />}
      </svg>
      <div className="min-w-0 flex-1">
        <p className="text-[12px] leading-4 font-semibold text-zinc-100 truncate" title={label}>{label}</p>
        <p className="text-[11px] leading-4 text-zinc-400 truncate" title={resetLabel ?? 'Reset time unavailable'}>
          {resetLabel ?? 'Reset time unavailable'}
        </p>
      </div>
      <div className="text-right shrink-0">
        <p className="font-mono text-[13px] leading-4 font-semibold tabular-nums" style={{color}}>{metric}</p>
        <p className="text-[10px] leading-3 text-zinc-400">remaining</p>
      </div>
    </div>
  );
}
