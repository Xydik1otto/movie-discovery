"use client";

// Спільний каталог для /anime та /manga: сітка тайтлів type=anime|manga
// через звичайний /api/search + швидкі фільтри (формат, рік, статус) і сортування.
// Демо-підмінки немає: у демо-пулі немає аніме/манги, тож при недоступному API —
// ApiBanner + підказка про синхронізацію.

import { useEffect, useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { searchTitles } from "../lib/api";
import { isUnavailable } from "../lib/fallback";
import { ANIME_MEDIA_FORMATS, MANGA_FORMATS } from "../lib/constants";
import type { Paged, TitleCard as TitleCardType } from "../lib/types";
import TitleCard from "./TitleCard";
import ApiBanner from "./ApiBanner";
import EmptyState from "./EmptyState";
import LoadingSkeleton from "./LoadingSkeleton";
import Pagination from "./Pagination";
import { Chip, ChipGroup } from "./FilterChips";

const PAGE_SIZE = 24;

export type CatalogKind = "anime" | "manga";
type CatalogSort = "popularity" | "rating" | "year";

const STATUS_CHIPS: { value: string; key: string }[] = [
  { value: "airing", key: "anime.airing" },
  { value: "finished", key: "anime.finished" },
];

const SORT_CHIPS: { value: CatalogSort; key: string }[] = [
  { value: "popularity", key: "filters.sort.popularity" },
  { value: "rating", key: "filters.sort.rating" },
  { value: "year", key: "filters.sort.year" },
];

function numOrNull(s: string): number | null {
  const v = s.trim();
  if (!v) return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

export default function CatalogPage({ kind }: { kind: CatalogKind }) {
  const { t, lang } = useI18n();
  const formatChips = kind === "anime" ? ANIME_MEDIA_FORMATS : MANGA_FORMATS;

  const [format, setFormat] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [yearFrom, setYearFrom] = useState("");
  const [yearTo, setYearTo] = useState("");
  const [years, setYears] = useState<{ from: number | null; to: number | null }>({
    from: null,
    to: null,
  });
  const [sort, setSort] = useState<CatalogSort>("popularity");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<TitleCardType> | null>(null);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);

  useEffect(() => {
    let alive = true;
    setLoading(true);
    setError(false);
    searchTitles(
      {
        type: kind,
        format,
        status,
        yearFrom: years.from,
        yearTo: years.to,
        sort,
        page,
        pageSize: PAGE_SIZE,
      },
      lang,
    )
      .then((r) => {
        if (!alive) return;
        setData(r);
        setUnavailable(false);
      })
      .catch((e) => {
        if (!alive) return;
        if (isUnavailable(e)) {
          setUnavailable(true);
          setData(null);
        } else {
          setError(true);
        }
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [kind, format, status, years, sort, page, lang]);

  function applyYears() {
    setYears({ from: numOrNull(yearFrom), to: numOrNull(yearTo) });
    setPage(1);
  }

  function resetFilters() {
    setFormat(null);
    setStatus(null);
    setYearFrom("");
    setYearTo("");
    setYears({ from: null, to: null });
    setSort("popularity");
    setPage(1);
  }

  const emptyKey = kind === "anime" ? "anime.empty" : "manga.empty";
  const titleKey = kind === "anime" ? "anime.title" : "manga.title";
  const subtitleKey = kind === "anime" ? "anime.subtitle" : "manga.subtitle";
  const emptyIcon = kind === "anime" ? "📺" : "📚";

  return (
    <div>
      <section className="mb-6">
        <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
          {t(titleKey)}
        </h1>
        <p className="mt-1 max-w-2xl text-sm text-mute">{t(subtitleKey)}</p>
      </section>

      {/* Швидкі фільтри */}
      <div className="mb-5 space-y-3 rounded-2xl border border-line bg-panel p-4">
        <div>
          <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-mute">
            {t("filters.format")}
          </div>
          <ChipGroup>
            {formatChips.map((f) => (
              <Chip
                key={f.value}
                small
                selected={format === f.value}
                onClick={() => {
                  setFormat(format === f.value ? null : f.value);
                  setPage(1);
                }}
              >
                {lang === "uk" ? f.uk : f.en}
              </Chip>
            ))}
          </ChipGroup>
        </div>

        <div>
          <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-mute">
            {t("filters.year")}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <input
              type="number"
              inputMode="numeric"
              value={yearFrom}
              onChange={(e) => setYearFrom(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && applyYears()}
              onBlur={applyYears}
              placeholder={t("filters.min")}
              aria-label={`${t("filters.year")} ${t("filters.min")}`}
              className="w-24 rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
            />
            <span className="text-mute">–</span>
            <input
              type="number"
              inputMode="numeric"
              value={yearTo}
              onChange={(e) => setYearTo(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && applyYears()}
              onBlur={applyYears}
              placeholder={t("filters.max")}
              aria-label={`${t("filters.year")} ${t("filters.max")}`}
              className="w-24 rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
            />
          </div>
        </div>

        <div className="flex flex-wrap items-end gap-x-6 gap-y-3">
          <div>
            <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-mute">
              {t("title.status")}
            </div>
            <ChipGroup>
              {STATUS_CHIPS.map((s) => (
                <Chip
                  key={s.value}
                  small
                  selected={status === s.value}
                  onClick={() => {
                    setStatus(status === s.value ? null : s.value);
                    setPage(1);
                  }}
                >
                  {t(s.key)}
                </Chip>
              ))}
            </ChipGroup>
          </div>

          <label className="block">
            <span className="mb-1 block text-xs text-mute">{t("filters.sort")}</span>
            <select
              value={sort}
              onChange={(e) => {
                setSort(e.target.value as CatalogSort);
                setPage(1);
              }}
              className="rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
            >
              {SORT_CHIPS.map((s) => (
                <option key={s.value} value={s.value}>
                  {t(s.key)}
                </option>
              ))}
            </select>
          </label>

          <button
            type="button"
            onClick={resetFilters}
            className="rounded-xl border border-line bg-panel-2 px-4 py-2 text-sm text-mute transition-colors hover:text-ink"
          >
            {t("filters.reset")}
          </button>
        </div>
      </div>

      {/* Результати */}
      {unavailable && <ApiBanner />}
      {loading ? (
        <LoadingSkeleton />
      ) : error ? (
        <EmptyState icon="⚠️" title={t("common.error")} />
      ) : data && data.items.length === 0 ? (
        <EmptyState icon={emptyIcon} title={t(emptyKey)} />
      ) : data ? (
        <>
          <p className="mb-3 text-xs text-mute">
            {t("search.results", { n: data.total })}
          </p>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
            {data.items.map((item) => (
              <TitleCard key={item.id} item={item} eager={page === 1} />
            ))}
          </div>
          <Pagination
            page={data.page}
            total={data.total}
            pageSize={data.pageSize}
            onChange={setPage}
          />
        </>
      ) : unavailable ? (
        <EmptyState icon={emptyIcon} title={t(emptyKey)} />
      ) : null}
    </div>
  );
}
