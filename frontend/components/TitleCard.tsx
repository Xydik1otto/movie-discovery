"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { WATCH_KIND_BADGE_STYLE, formatLabel, posterUrlFull } from "../lib/constants";
import { dedupeWatchKinds, freeOnly, watchBadgeLabelKey, watchKindLabelKey } from "../lib/watch";
import type { SimilarItem, TitleCard as TitleCardType } from "../lib/types";
import { useI18n } from "../i18n/I18nContext";
import RatingBadge from "./RatingBadge";
import ReasonBadges from "./ReasonBadges";

// Картка тайтла (форма TitleCard за PLAN). Форма використовується всюди.
// eager=true — постери першого екрана БЕЗ loading=lazy (пітфолл lazy після ре-рендера);
// lazy — лише для глибоких елементів.
// FR-21: onSimilar — ланцюг на сторінці /similar (кнопка «Схожі» на картках
// результатів підміняє еталони, не виходячи зі сторінки); без onSimilar кнопка
// веде на /similar?seed={id} — внутрішній id (є в усіх тайтлів, на відміну від tmdbId). Кнопка з'являється при hover
// у кутку постера й не змінює лейаут картки.
export default function TitleCard({
  item,
  eager = false,
  score,
  onSimilar,
  footer,
}: {
  item: TitleCardType;
  eager?: boolean;
  score?: number | null;
  onSimilar?: (item: TitleCardType) => void;
  footer?: React.ReactNode;
}) {
  const { t, lang } = useI18n();
  const router = useRouter();
  const similar = item as SimilarItem;
  const url = posterUrlFull(item.posterUrl);
  const isAnime = item.type === "anime";
  const isManga = item.type === "manga";
  const fmt = formatLabel(item.format, lang);
  const typeKey =
    item.type === "movie"
      ? "filters.movie"
      : item.type === "series"
        ? "filters.series"
        : isAnime
          ? "filters.anime"
          : isManga
            ? "filters.manga"
            : "filters.show";

  // FR-14: маленькі бейджі доступності перегляду у верхньому правому куті —
  // разом із % схожості (одна колонка, лейаут картки не змінюється).
  const watchBadges = dedupeWatchKinds(freeOnly(item.watchProviders ?? [])).slice(0, 3);

  // FR-21: кнопка «Схожі» всередині Link-картки — preventDefault гасить
  // навігацію на /title/{id}, кнопка абсолютна (лейаут не змінюється).
  function openSimilar(e: React.MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    if (onSimilar) {
      onSimilar(item);
      return;
    }
    router.push(`/similar?seed=${item.id}`);
  }

  return (
    <div className="group">
      <Link
        href={`/title/${item.id}`}
        className="block overflow-hidden rounded-xl border border-line bg-panel transition-all hover:-translate-y-0.5 hover:border-accent/60 hover:shadow-lg hover:shadow-accent/10"
      >
        <div className="relative aspect-[2/3] overflow-hidden bg-panel-2">
          {url ? (
            <img
              src={url}
              alt={item.title}
              loading={eager ? "eager" : "lazy"}
              decoding="async"
              className="h-full w-full object-cover transition-transform duration-300 group-hover:scale-105"
            />
          ) : (
            <div className="flex h-full w-full flex-col items-center justify-center gap-2 bg-gradient-to-br from-panel-2 via-[#232a3d] to-[#3a1a2b] p-3 text-center">
              <span className="text-3xl opacity-60" aria-hidden>
                🎬
              </span>
              <span className="line-clamp-3 text-xs font-semibold text-ink/80">
                {item.title}
              </span>
            </div>
          )}

          <div className="absolute left-1.5 top-1.5 flex flex-col items-start gap-1">
            <RatingBadge source="TMDB" value={item.ratingTmdb} />
            <RatingBadge source="IMDb" value={item.ratingImdb} />
            {/* MAL/AL/Shiki замінюють TMDB/IMDb, коли тих нема (аніме/манга). */}
            <RatingBadge source="MAL" value={item.scoreMal} title={t("badges.mal")} />
            <RatingBadge source="AL" value={item.scoreAnilist} title={t("badges.al")} />
            <RatingBadge
              source="Shiki"
              value={item.scoreShikimori}
              title={t("badges.shiki")}
            />
          </div>

          {(score != null || watchBadges.length > 0) && (
            <div className="absolute right-1.5 top-1.5 flex flex-col items-end gap-1">
              {score != null && (
                <div className="rounded-md border border-accent/40 bg-accent/20 px-1.5 py-0.5 text-[11px] font-bold text-white backdrop-blur-sm">
                  {Math.round(score * 100)}%
                </div>
              )}
              {watchBadges.map((w) => {
                const badgeKey = watchBadgeLabelKey(w.kind);
                const fullKey = watchKindLabelKey(w.kind);
                return (
                  <span
                    key={w.kind}
                    title={fullKey ? t(fullKey) : w.kind}
                    className={`rounded-md border px-1.5 py-0.5 text-[10px] font-bold uppercase tracking-wide backdrop-blur-sm ${
                      WATCH_KIND_BADGE_STYLE[w.kind] ?? "border-line bg-black/60 text-white/90"
                    }`}
                  >
                    {badgeKey ? t(badgeKey) : w.kind}
                  </span>
                );
              })}
            </div>
          )}

          <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 to-transparent px-2.5 pb-2 pt-8">
            <div className="line-clamp-2 text-sm font-semibold leading-tight text-white">
              {item.title}
            </div>
            <div className="mt-0.5 flex flex-wrap items-center gap-1.5 text-[11px] text-white/60">
              <span>{t(typeKey)}</span>
              {item.year != null && <span>· {item.year}</span>}
              {isAnime && (
                <>
                  {fmt && <span>· {fmt}</span>}
                  {item.episodes != null && (
                    <span>
                      · {t("anime.episodes")}: {item.episodes}
                    </span>
                  )}
                </>
              )}
              {isManga && (item.volumes != null || item.chapters != null) && (
                <span>
                  ·{" "}
                  {[
                    item.volumes != null && `${t("manga.volumes")}: ${item.volumes}`,
                    item.chapters != null && `${t("manga.chapters")}: ${item.chapters}`,
                  ]
                    .filter(Boolean)
                    .join(" · ")}
                </span>
              )}
              {!isAnime && !isManga && item.runtimeMin != null && item.runtimeMin > 0 && (
                <span>· {t("title.runtimeMin", { n: item.runtimeMin })}</span>
              )}
              {!isAnime && !isManga && item.runtimeMin == null && item.seasons != null && (
                <span>
                  · {t("title.seasons")}: {item.seasons}
                </span>
              )}
            </div>
          </div>

          {/* FR-21: кнопка «Схожі» — у кутку постера, видно при hover/фокусі. */}
          {item.tmdbId != null && (
            <button
              type="button"
              onClick={openSimilar}
              aria-label={t("similar.cardAria", { title: item.title })}
              className="absolute bottom-2 right-2 z-10 rounded-lg border border-white/25 bg-black/70 px-2 py-1 text-[11px] font-bold text-white opacity-0 shadow-md backdrop-blur-sm transition-opacity focus-visible:opacity-100 group-hover:opacity-100"
            >
              ✦ {t("similar.cardBtn")}
            </button>
          )}
        </div>
      </Link>

      {!!item.genres?.length && (
        <div className="mt-1.5 line-clamp-1 px-0.5 text-[11px] text-mute">
          {item.genres.slice(0, 3).join(" · ")}
        </div>
      )}

      {/* FR-21: бейджі-пояснення (коди shared_genres/shared_actor/decade → i18n). */}
      {(similar.reasons?.length ?? 0) > 0 && (
        <div className="px-0.5">
          <ReasonBadges reasons={similar.reasons} />
        </div>
      )}
      {footer}
    </div>
  );
}
