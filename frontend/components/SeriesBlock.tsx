"use client";

import Link from "next/link";
import { posterUrlFull } from "@/lib/constants";
import type { SeriesResponse } from "@/lib/types";
import { useI18n } from "@/i18n/I18nContext";

// FR: «Фільми з тієї ж лінійки» — франшиза/сага тайтла як порядок перегляду
// (бекенд сортує: рік ASC, потім назва) з per-account позначками watched/watchlist.
// Блок ховається повністю, коли тайтл поза серією (seriesKey=null) або в каталозі
// менше 2 тайтлів групи — одиничний елемент «серії» користувачу нічого не каже.
export default function SeriesBlock({ series }: { series: SeriesResponse | null }) {
  const { t } = useI18n();
  if (!series?.seriesKey || series.items.length < 2) return null;

  return (
    <section className="mt-8" aria-label={t("series.title")}>
      <div className="mb-3 flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h2 className="text-lg font-bold">{t("series.title")}</h2>
        <span className="text-xs text-mute">{t("series.order")}</span>
      </div>
      <ol className="divide-y divide-line overflow-hidden rounded-2xl border border-line bg-panel">
        {series.items.map((item, i) => {
          const poster = posterUrlFull(item.posterUrl);
          return (
            <li key={item.id}>
              <Link
                href={`/title/${item.id}`}
                className={`flex items-center gap-3 px-3 py-2.5 transition-colors hover:bg-panel-2 ${
                  item.isCurrent ? "bg-accent/10" : ""
                }`}
              >
                {/* Порядковий номер у порядку перегляду */}
                <span className="w-5 shrink-0 text-right text-xs font-bold text-mute">
                  {i + 1}
                </span>

                {/* Постер-мініатюра */}
                <div className="h-14 w-10 shrink-0 overflow-hidden rounded-md bg-panel-2">
                  {poster ? (
                    <img
                      src={poster}
                      alt=""
                      loading="lazy"
                      decoding="async"
                      className="h-full w-full object-cover"
                    />
                  ) : (
                    <div
                      className="flex h-full w-full items-center justify-center text-base opacity-60"
                      aria-hidden
                    >
                      🎬
                    </div>
                  )}
                </div>

                <div className="min-w-0 flex-1">
                  <div
                    className={`line-clamp-1 text-sm font-semibold ${
                      item.isCurrent ? "text-accent-2" : "text-ink"
                    }`}
                  >
                    {item.title}
                  </div>
                  <div className="mt-0.5 flex flex-wrap items-center gap-2 text-[11px] text-mute">
                    <span>{item.year ?? "—"}</span>
                    {item.isCurrent && (
                      <span className="rounded-md border border-accent/40 bg-accent/15 px-1.5 py-0.5 font-bold text-accent-2">
                        {t("series.current")}
                      </span>
                    )}
                    {item.watched && (
                      <span
                        title={t("series.watched")}
                        className="rounded-md bg-emerald-500/15 px-1.5 py-0.5 font-bold text-emerald-300"
                      >
                        ✓ {t("series.watched")}
                      </span>
                    )}
                    {item.watchlist && (
                      <span
                        title={t("series.watchlist")}
                        className="rounded-md bg-accent/15 px-1.5 py-0.5 font-bold text-accent-2"
                      >
                        🔖 {t("series.watchlist")}
                      </span>
                    )}
                  </div>
                </div>
              </Link>
            </li>
          );
        })}
      </ol>
    </section>
  );
}
