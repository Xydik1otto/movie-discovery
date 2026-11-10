"use client";

// Спільний каталог для /anime та /manga: сітка тайтлів type=anime|manga
// через звичайний /api/search + швидкі фільтри (формат, рік, статус) і сортування.
// UX-01 (10.11): серверна пагінація по 20 (usePagedSearch замість прогресивного
// акумулятора) + синхронізація format/status/років/sort/page з URL (useSearchParams
// у <Suspense> — пастка Next 15 на пререндері). Зміна фільтра/сортування скидає
// page=1; мова і фільтри переживають навігацію через URL.
// Демо-підмінки немає: у демо-пулі немає аніме/манги, тож при недоступному API —
// ApiBanner + підказка про синхронізацію.

import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useI18n } from "../i18n/I18nContext";
import { searchTitles } from "../lib/api";
import { MAX_PAGES, PAGE_SIZE, usePagedSearch } from "../lib/usePagedSearch";
import type { FetchPageArgs } from "../lib/usePagedSearch";
import { ANIME_MEDIA_FORMATS, MANGA_FORMATS } from "../lib/constants";
import TitleCard from "./TitleCard";
import ApiBanner from "./ApiBanner";
import EmptyState from "./EmptyState";
import LoadingSkeleton from "./LoadingSkeleton";
import Pagination from "./Pagination";
import { Chip, ChipGroup } from "./FilterChips";

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

/** Стан каталогу, відновлюваний з URL (?format=&status=&yearFrom=&yearTo=&sort=&page=). */
function parseCatalogParams(sp: URLSearchParams) {
  const sortRaw = sp.get("sort");
  return {
    format: sp.get("format") || null,
    status: sp.get("status") || null,
    yearFrom: sp.get("yearFrom") ?? "",
    yearTo: sp.get("yearTo") ?? "",
    sort:
      sortRaw === "rating" || sortRaw === "year" || sortRaw === "popularity"
        ? (sortRaw as CatalogSort)
        : ("popularity" as CatalogSort),
    page: Math.min(MAX_PAGES, Math.max(1, Number(sp.get("page")) || 1)),
  };
}

export default function CatalogPage({ kind }: { kind: CatalogKind }) {
  // useSearchParams на пререндері без Suspense ламає next build (Next 15).
  return (
    <Suspense fallback={<LoadingSkeleton count={4} />}>
      <CatalogInner kind={kind} />
    </Suspense>
  );
}

function CatalogInner({ kind }: { kind: CatalogKind }) {
  const { t, lang } = useI18n();
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const formatChips = kind === "anime" ? ANIME_MEDIA_FORMATS : MANGA_FORMATS;

  // Початковий стан — з URL (переживає reload/back-forward); далі URL — дзеркало стану.
  const [init] = useState(() => parseCatalogParams(new URLSearchParams(searchParams.toString())));
  const [format, setFormat] = useState<string | null>(init.format);
  const [status, setStatus] = useState<string | null>(init.status);
  const [yearFrom, setYearFrom] = useState(init.yearFrom);
  const [yearTo, setYearTo] = useState(init.yearTo);
  const [years, setYears] = useState<{ from: number | null; to: number | null }>({
    from: numOrNull(init.yearFrom),
    to: numOrNull(init.yearTo),
  });
  const [sort, setSort] = useState<CatalogSort>(init.sort);

  // Серверна пагінація: page належить хуку, старт — з URL.
  const fetchPage = useCallback(
    ({ page: pg, omitTotal }: FetchPageArgs) =>
      searchTitles(
        {
          type: kind,
          format,
          status,
          yearFrom: years.from,
          yearTo: years.to,
          sort,
          page: pg,
          pageSize: PAGE_SIZE,
          omitTotal,
        },
        lang,
      ),
    [kind, format, status, years, sort, lang],
  );
  const list = usePagedSearch(
    fetchPage,
    JSON.stringify([kind, format, status, years, sort]) + "|" + lang,
    true,
    init.page,
  );

  // ── URL-дзеркало: стан → ?params ──
  // replace — для зміни фільтрів (reset не засмічує історію);
  // push — для перегортань сторінок (Back повертає до попередньої сторінки результатів).
  const buildQs = useCallback(
    (page: number) => {
      const sp = new URLSearchParams();
      if (format) sp.set("format", format);
      if (status) sp.set("status", status);
      if (years.from != null) sp.set("yearFrom", String(years.from));
      if (years.to != null) sp.set("yearTo", String(years.to));
      if (sort !== "popularity") sp.set("sort", sort);
      if (page > 1) sp.set("page", String(page));
      return sp.toString();
    },
    [format, status, years, sort],
  );
  const urlQs = buildQs(list.page);
  const lastWrittenRef = useRef<string | null>(null);
  useEffect(() => {
    if (lastWrittenRef.current === urlQs) return;
    lastWrittenRef.current = urlQs;
    router.replace(urlQs ? `${pathname}?${urlQs}` : pathname, { scroll: false });
  }, [urlQs, router, pathname]);

  // ── Back/forward: URL змінили зовнішньо → гідратуємо стан з нього ──
  useEffect(() => {
    const current = searchParams.toString();
    if (current === lastWrittenRef.current) return; // наш власний запис — ехо
    lastWrittenRef.current = current;
    const p = parseCatalogParams(new URLSearchParams(current));
    setFormat(p.format);
    setStatus(p.status);
    setYearFrom(p.yearFrom);
    setYearTo(p.yearTo);
    setYears({ from: numOrNull(p.yearFrom), to: numOrNull(p.yearTo) });
    setSort(p.sort);
    list.goTo(p.page);
    // list.goTo стабільний; читаємо лише searchParams, інакше цикл на кожен рендер
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  function applyYears() {
    setYears({ from: numOrNull(yearFrom), to: numOrNull(yearTo) });
  }

  function resetFilters() {
    setFormat(null);
    setStatus(null);
    setYearFrom("");
    setYearTo("");
    setYears({ from: null, to: null });
    setSort("popularity");
  }

  // Перегортання: нова сторінка замінює поточну. PUSH-навігація (not replace):
  // кожна сторінка — запис історії, Back повертає до попередньої сторінки результатів.
  const { goTo: listGoTo } = list;
  const changePage = useCallback(
    (p: number) => {
      const qs = buildQs(p);
      lastWrittenRef.current = qs; // push сам оновлює URL — дзеркало не дублює
      router.push(qs ? `${pathname}?${qs}` : pathname);
      listGoTo(p);
      window.scrollTo({ top: 0 });
    },
    [buildQs, router, pathname, listGoTo],
  );

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
                }}
              >
                {f[lang]}
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

      {/* Результати — серверні сторінки по 20: наступна замінює поточну */}
      {list.unavailable && <ApiBanner />}
      {list.firstLoad ? (
        <LoadingSkeleton />
      ) : list.error ? (
        <div>
          <EmptyState icon="⚠️" title={t("common.error")} />
          <div className="mt-3 flex justify-center">
            <button
              type="button"
              onClick={list.retry}
              className="rounded-xl border border-line bg-panel px-5 py-2.5 text-sm font-semibold text-ink transition-colors hover:border-accent/50"
            >
              {t("common.retry")}
            </button>
          </div>
        </div>
      ) : list.items.length === 0 && !list.busy ? (
        <EmptyState icon={emptyIcon} title={t(emptyKey)} />
      ) : (
        <>
          {list.total != null && (
            <p className="mb-3 text-xs text-mute">
              {list.totalCapped ? t("search.resultsCapped") : t("search.results", { n: list.total })}
            </p>
          )}
          <div
            className={`grid grid-cols-2 gap-3 transition-opacity duration-200 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 ${
              list.busy ? "pointer-events-none opacity-60" : ""
            }`}
          >
            {list.items.map((item, i) => (
              <TitleCard key={item.id} item={item} eager={i < PAGE_SIZE} />
            ))}
          </div>
          {list.busy && (
            <div className="mt-4 flex items-center justify-center gap-2 py-3 text-sm text-mute">
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-accent border-t-transparent" />
              {t("common.loading")}
            </div>
          )}
          {!list.busy && list.total != null && (
            <Pagination
              page={list.page}
              total={list.total}
              pageSize={list.pageSize}
              capped={list.totalCapped}
              maxPage={MAX_PAGES}
              onChange={changePage}
            />
          )}
        </>
      )}
    </div>
  );
}
