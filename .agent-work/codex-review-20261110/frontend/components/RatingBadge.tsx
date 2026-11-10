// Рейтинговий бейдж: TMDB / IMDb (кіно) та MAL / AL / Shiki (аніме/манга).

export type RatingSource = "TMDB" | "IMDb" | "MAL" | "AL" | "Shiki";

const STYLES: Record<RatingSource, string> = {
  TMDB: "bg-emerald-500/15 text-emerald-300 border-emerald-500/30",
  IMDb: "bg-amber-400/15 text-amber-300 border-amber-400/30",
  // MAL — синій (#2e51a2-стиль), AL — зелений, Shiki — фіолетовий.
  MAL: "bg-[#2e51a2]/30 text-sky-200 border-[#4a6fb5]/50",
  AL: "bg-green-500/15 text-green-300 border-green-500/30",
  Shiki: "bg-violet-500/15 text-violet-300 border-violet-500/30",
};

export default function RatingBadge({
  source,
  value,
  size = "sm",
  title,
}: {
  source: RatingSource;
  value?: number | null;
  size?: "sm" | "lg";
  title?: string;
}) {
  if (value == null || value <= 0) return null;
  const styles = STYLES[source];
  const pad = size === "lg" ? "px-2.5 py-1 text-sm" : "px-1.5 py-0.5 text-[11px]";
  return (
    <span
      className={`inline-flex items-baseline gap-1 rounded-md border font-semibold ${styles} ${pad}`}
      title={title ?? `${source}: ${value.toFixed(1)}`}
    >
      <span className="opacity-70">{source}</span>
      <span>{value.toFixed(1)}</span>
    </span>
  );
}
