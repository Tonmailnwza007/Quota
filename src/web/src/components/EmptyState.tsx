interface EmptyStateProps { onOpenSettings: () => void }
export function EmptyState({onOpenSettings}: EmptyStateProps) {
  return <div className="py-3 flex flex-col items-center gap-2 text-center">
    <h3 className="text-[13px] font-semibold text-zinc-200">No providers selected</h3>
    <p className="text-[11px] leading-4 text-zinc-400 max-w-[260px]">Choose which accounts to show in settings.</p>
    <button onClick={onOpenSettings} className="rounded-md bg-white/[0.08] px-3 py-1.5 text-xs text-zinc-200 hover:bg-white/[0.12] focus-visible:outline focus-visible:outline-zinc-400">Open settings</button>
  </div>;
}
