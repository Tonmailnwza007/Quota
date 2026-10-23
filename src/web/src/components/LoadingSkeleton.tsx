export function LoadingSkeleton() {
  return <div className="space-y-4 animate-pulse" aria-hidden="true">
    {[1,2].map(account=><div key={account} className="space-y-2">
      <div className="h-7 w-36 rounded bg-white/[0.06]" />
      {[1,2].map(row=><div key={row} className="h-12 rounded-lg bg-white/[0.05] flex items-center px-3 gap-3">
        <div className="w-7 h-7 rounded-full border-2 border-white/[0.07]" />
        <div className="space-y-1.5"><div className="h-2.5 w-20 bg-white/[0.06] rounded" /><div className="h-2 w-14 bg-white/[0.04] rounded" /></div>
      </div>)}
    </div>)}
  </div>;
}
