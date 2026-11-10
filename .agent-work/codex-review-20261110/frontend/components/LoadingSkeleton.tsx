// Скелетон завантаження: сітка карток-плейсхолдерів.

export default function LoadingSkeleton({ count = 12 }: { count?: number }) {
  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="overflow-hidden rounded-xl border border-line bg-panel">
          <div className="aspect-[2/3] animate-pulse bg-panel-2" />
          <div className="space-y-2 p-3">
            <div className="h-3.5 w-4/5 animate-pulse rounded bg-panel-2" />
            <div className="h-3 w-2/5 animate-pulse rounded bg-panel-2" />
          </div>
        </div>
      ))}
    </div>
  );
}
