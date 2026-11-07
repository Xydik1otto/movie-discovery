"use client";

// Картка свайп-стека «Кінотеки» (FR-18). Без Link на весь постер — клік
// під час drag не має вести на сторінку тайтла; окрема кнопка «Відкрити».

import { posterUrlFull } from "../lib/constants";
import type { TitleCard as TitleCardType } from "../lib/types";
import { useI18n } from "../i18n/I18nContext";

export default function SwipeCard({
  item,
  transform,
  transition,
  overlay,
  onOpen,
}: {
  item: TitleCardType;
  transform: string;
  transition: boolean;
  // "like" | "dislike" — підсвітка під час drag.
  overlay?: "like" | "dislike" | null;
  onOpen: () => void;
}) {
  const { t } = useI18n();
  const url = posterUrlFull(item.posterUrl);

  return (
    <div
      style={{ transform, transition: transition ? "transform 260ms ease-out, opacity 260ms ease-out" : "none" }}
      className="absolute inset-x-0 bottom-0 top-0 mx-auto max-w-sm will-change-transform"
    >
      <div className="relative h-full overflow-hidden rounded-3xl border border-line bg-panel shadow-2xl shadow-black/40 select-none">
        <div className="absolute inset-0 bg-panel-2">
          {url ? (
            <img
              src={url}
              alt={item.title}
              draggable={false}
              className="h-full w-full object-cover"
            />
          ) : (
            <div className="flex h-full w-full flex-col items-center justify-center gap-3 bg-gradient-to-br from-panel-2 via-[#232a3d] to-[#3a1a2b] p-6 text-center">
              <span className="text-5xl opacity-60" aria-hidden>
                🎬
              </span>
              <span className="text-lg font-bold text-ink/80">{item.title}</span>
            </div>
          )}
        </div>

        {/* Градієнт + підпис знизу */}
        <div className="absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/90 via-black/40 to-transparent px-5 pb-5 pt-16">
          <div className="text-xl font-extrabold leading-tight text-white">
            {item.title}
            {item.year != null && <span className="text-white/70"> · {item.year}</span>}
          </div>
          {!!item.genres?.length && (
            <div className="mt-1 line-clamp-1 text-xs text-white/70">
              {item.genres.slice(0, 4).join(" · ")}
            </div>
          )}
          {item.overview && (
            <p className="mt-2 line-clamp-3 text-xs leading-relaxed text-white/60">
              {item.overview}
            </p>
          )}
        </div>

        {/* Оверлеї drag: ПОДОБАЄТЬСЯ / НЕ МОЄ */}
        {overlay === "like" && (
          <div className="pointer-events-none absolute left-4 top-6 -rotate-12 rounded-xl border-4 border-emerald-400 px-4 py-1.5 text-2xl font-black uppercase tracking-wider text-emerald-400">
            {t("discover.like")}
          </div>
        )}
        {overlay === "dislike" && (
          <div className="pointer-events-none absolute right-4 top-6 rotate-12 rounded-xl border-4 border-red-400 px-4 py-1.5 text-2xl font-black uppercase tracking-wider text-red-400">
            {t("discover.dislike")}
          </div>
        )}

        {/* Кнопка «Відкрити картку» — свідомо поза зоною drag-обробника */}
        <button
          type="button"
          onPointerDown={(e) => e.stopPropagation()}
          onClick={onOpen}
          title={t("discover.openTitle")}
          className="absolute right-3 top-3 rounded-full border border-white/20 bg-black/50 px-3 py-1.5 text-xs font-semibold text-white/90 backdrop-blur-sm transition-colors hover:bg-black/70"
        >
          ↗
        </button>
      </div>
    </div>
  );
}
