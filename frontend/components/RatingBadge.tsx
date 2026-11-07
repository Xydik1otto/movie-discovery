// Рейтинговий бейдж (TMDB / IMDb).

export default function RatingBadge({
  source,
  value,
  size = "sm",
}: {
  source: "TMDB" | "IMDb";
  value?: number | null;
  size?: "sm" | "lg";
}) {
  if (value == null || value <= 0) return null;
  const styles =
    source === "TMDB"
      ? "bg-emerald-500/15 text-emerald-300 border-emerald-500/30"
      : "bg-amber-400/15 text-amber-300 border-amber-400/30";
  const pad = size === "lg" ? "px-2.5 py-1 text-sm" : "px-1.5 py-0.5 text-[11px]";
  return (
    <span
      className={`inline-flex items-baseline gap-1 rounded-md border font-semibold ${styles} ${pad}`}
      title={`${source}: ${value.toFixed(1)}`}
    >
      <span className="opacity-70">{source}</span>
      <span>{value.toFixed(1)}</span>
    </span>
  );
}
