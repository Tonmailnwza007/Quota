import { useState } from 'react';
import type { ProviderReading } from '../types';
import { QuotaGauge } from './QuotaGauge';
import { Link2, AlertTriangle, ExternalLink, ChevronDown, ChevronUp } from 'lucide-react';

interface Props {
  provider: ProviderReading;
  hideSharedNotice?: boolean;
  showUsageDetails?: boolean;
}

const brandFor = (provider: ProviderReading) => {
  if (provider.id === 'codex') return { src: '/brands/openai.svg', alt: 'OpenAI' };
  if (provider.family === 'codex') return { src: '/brands/codex.svg', alt: 'Codex' };
  if (provider.family === 'antigravity') return { src: '/brands/antigravity.svg', alt: 'Antigravity' };
  if (provider.family === 'claude') return { src: '/brands/claude.svg', alt: 'Claude' };
  return { src: '/brands/githubcopilot.svg', alt: 'GitHub Copilot' };
};

export function ProviderCard({ provider, hideSharedNotice = false, showUsageDetails = false }: Props) {
  const [expandedOverride, setExpandedOverride] = useState<boolean | null>(null);
  const expanded = expandedOverride ?? (provider.status === 'ok');
  const [detailsOpen, setDetailsOpen] = useState(false);

  const brand = brandFor(provider);
  const isAntigravity = provider.family === 'antigravity';
  const hasWeekly = !isAntigravity && provider.family !== 'copilot' && provider.weeklyProgress !== undefined;
  const hasDetails = Boolean(
    provider.usageDetails?.length ||
    provider.tokenUsage ||
    provider.estimatedCost ||
    provider.creditsRemaining ||
    provider.portalUrl ||
    provider.accountId
  );

  return (
    <article
      data-provider-id={provider.id}
      aria-labelledby={`provider-${provider.id}-title`}
      className="flex flex-col gap-1.5"
    >
      {/* Header row: Logo, Name, Plan, Shared, Email, and Expand/Collapse Chevron */}
      <div className="flex items-start justify-between gap-2">
        <div className="flex items-start gap-2 min-w-0">
          <img src={brand.src} alt={brand.alt} className="w-[18px] h-[18px] mt-0.5 shrink-0 object-contain" />
          <div className="min-w-0">
            <div className="flex items-center gap-1.5 min-w-0">
              <h3
                id={`provider-${provider.id}-title`}
                className="text-[12px] leading-[18px] font-semibold text-zinc-100 truncate"
                title={provider.displayName}
              >
                {provider.displayName}
              </h3>
              {provider.plan && (
                <span className="text-[10px] text-zinc-400 shrink-0" title={provider.plan}>
                  {provider.plan}
                </span>
              )}
              {/* Codex Shared account quota badge */}
              {!hideSharedNotice && !isAntigravity && provider.isSharedAccountQuota && provider.sharedNotice && (
                <span className="inline-flex items-center gap-1 text-[9px] text-zinc-400 shrink-0" title={provider.sharedNotice}>
                  <Link2 className="w-2.5 h-2.5" />
                  Shared
                </span>
              )}
            </div>
            {provider.accountEmail && (
              <p className="text-[11px] leading-4 text-zinc-400 truncate" title={provider.accountEmail}>
                {provider.accountEmail}
              </p>
            )}
            {/* Quiet background quota note for Antigravity */}
            {isAntigravity && (
              <p
                className="text-[10px] leading-4 text-zinc-500"
                title="Quota is read in the background using your saved sign-in. Antigravity and CLI clients do not need to stay open."
              >
                Background quota
              </p>
            )}
          </div>
        </div>

        {/* Header chevron collapses ENTIRE provider body */}
        <button
          type="button"
          onClick={() => setExpandedOverride(!expanded)}
          aria-expanded={expanded}
          aria-controls={`provider-${provider.id}-body`}
          aria-label={expanded ? `Collapse ${provider.displayName}` : `Expand ${provider.displayName}`}
          title={expanded ? `Collapse ${provider.displayName}` : `Expand ${provider.displayName}`}
          className="p-1 text-zinc-400 hover:text-zinc-100 rounded focus:outline-none focus:ring-1 focus:ring-zinc-400 shrink-0 transition-colors"
        >
          {expanded ? <ChevronUp className="w-3.5 h-3.5" /> : <ChevronDown className="w-3.5 h-3.5" />}
        </button>
      </div>

      {/* Collapsible Provider Body */}
      {expanded && (
        <div id={`provider-${provider.id}-body`} className="flex flex-col gap-1.5">
          {/* Standard session and weekly gauges */}
          {!isAntigravity && (
            <div className="flex flex-col gap-1.5">
              <QuotaGauge
                label={provider.family === 'copilot' ? 'Allowance' : 'Session limit'}
                usedPercent={provider.sessionProgress}
                resetLabel={provider.sessionResetLabel}
                type="session"
              />
              {hasWeekly && (
                <QuotaGauge
                  label="Weekly limit"
                  usedPercent={provider.weeklyProgress}
                  resetLabel={provider.weeklyResetLabel}
                  type="weekly"
                />
              )}
            </div>
          )}

          {/* Antigravity named model buckets or fallback gauge */}
          {isAntigravity && Boolean(provider.namedBuckets?.length) && (
            <div className="flex flex-col gap-1">
              {provider.namedBuckets!.map((bucket, i) => (
                <QuotaGauge
                  key={`${bucket.name}-${i}`}
                  label={bucket.name}
                  usedPercent={bucket.percentage}
                  resetLabel={bucket.resetLabel}
                  compact
                />
              ))}
            </div>
          )}

          {isAntigravity && !provider.namedBuckets?.length && provider.sessionProgress !== null && provider.sessionProgress !== undefined && (
            <QuotaGauge
              label="Quota"
              usedPercent={provider.sessionProgress}
              resetLabel={provider.sessionResetLabel}
            />
          )}

          {/* Status message */}
          {provider.statusMessage && (
            <p className="flex items-start gap-1.5 text-[11px] leading-4 text-zinc-400" title={provider.statusMessage}>
              <AlertTriangle
                className={`w-3 h-3 mt-0.5 shrink-0 ${provider.status === 'expired' ? 'text-rose-400' : 'text-zinc-500'}`}
              />
              <span className="line-clamp-2">{provider.statusMessage}</span>
            </p>
          )}

          {isAntigravity && !provider.namedBuckets?.length && !provider.statusMessage && provider.sessionProgress == null && (
            <p className="text-[11px] text-zinc-500">Quota unavailable</p>
          )}

          {/* Optional Usage Details: Only visible when showUsageDetails setting is enabled */}
          {showUsageDetails && hasDetails && (
            <div className="pt-0.5 flex flex-col gap-1.5">
              <button
                type="button"
                onClick={() => setDetailsOpen(prev => !prev)}
                aria-expanded={detailsOpen}
                aria-controls={`provider-${provider.id}-details`}
                aria-label={`Usage details for ${provider.displayName}`}
                title="Usage details"
                className="self-start inline-flex items-center gap-1 text-[10px] text-zinc-400 hover:text-zinc-200 rounded focus:outline-none focus:underline"
              >
                <span>Usage details</span>
                {detailsOpen ? <ChevronUp className="w-3 h-3" /> : <ChevronDown className="w-3 h-3" />}
              </button>

              {detailsOpen && (
                <div
                  id={`provider-${provider.id}-details`}
                  className="rounded-lg bg-[#282828] p-2.5 grid grid-cols-2 gap-2 text-[11px]"
                >
                  {provider.usageDetails?.map(item => (
                    <div key={item.label}>
                      <span className="text-zinc-500 block text-[10px]">{item.label}</span>
                      <span className="text-zinc-200 font-mono">{item.value}</span>
                    </div>
                  ))}
                  {!provider.usageDetails?.length && provider.tokenUsage && (
                    <div>
                      <span className="text-zinc-500 block text-[10px]">Token usage</span>
                      <span className="font-mono text-zinc-200">{provider.tokenUsage}</span>
                    </div>
                  )}
                  {!provider.usageDetails?.length && provider.estimatedCost && (
                    <div>
                      <span className="text-zinc-500 block text-[10px]">est. API cost</span>
                      <span className="font-mono text-zinc-200">{provider.estimatedCost}</span>
                    </div>
                  )}
                  {provider.creditsRemaining && (
                    <div>
                      <span className="text-zinc-500 block text-[10px]">Credits remaining</span>
                      <span className="font-mono text-zinc-200">{provider.creditsRemaining}</span>
                    </div>
                  )}
                  {provider.accountId && (
                    <div className="col-span-2 min-w-0">
                      <span className="text-zinc-500 block text-[10px]">Account reference</span>
                      <span className="font-mono text-[10px] block truncate text-zinc-200" title={provider.accountId}>
                        {provider.accountId}
                      </span>
                    </div>
                  )}
                  {provider.portalUrl && (
                    <a
                      href={provider.portalUrl}
                      target="_blank"
                      rel="noreferrer"
                      className="col-span-2 flex justify-end items-center gap-1 text-zinc-300 hover:text-white"
                      onClick={
                        window.costats
                          ? (event) => {
                              event.preventDefault();
                              void window.costats?.openUsage(provider.id).catch(() => {});
                            }
                          : undefined
                      }
                    >
                      Provider dashboard <ExternalLink className="w-3 h-3" />
                    </a>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </article>
  );
}
