"use client";

// Спільний каталог для /anime та /manga: сітка тайтлів type=anime|manga
// через звичайний /api/search. UX (тими+фільтри): верхньорівневі тими-чіпи
// (ThemeTabs; category=movie|series|cartoon|anime — вибір тими веде на
// відповідний каталог) + плаваюча адаптивна панель фільтрів (FilterPanel:
// desktop — sticky бар з випадом, mobile — bottom sheet; секції з
// /api/filters?category=<kind>, деградація до статики без падіння).
// UX-01 (10.11): серверна пагінація по 20 (usePagedSearch); зміна фільтра/
// сортування скидає page=1; фільтри переживають навігацію через URL
// (useSearchParams у <Suspense> — пастка Next 15 на пререндері).
// Демо-підмінки немає: у демо-пулі немає аніме/манги, тож при недоступному
// API — ApiBanner + підказка про синхронізацію.

import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useI18n } from "../i18n/I18nContext";
import { searchTitles } from "../lib/api";
import { MAX_PAGES, PAGE_SIZE, usePagedSearch } from "../lib/usePagedSearch";
import type { FetchPageArgs } from "../lib/usePagedSearch";
import { usePosterBackfill } from "../lib/usePosterBackfill";
import { useFiltersData } from "../lib/useFiltersData";
import TitleCard from "./TitleCard";
import ApiBanner from "./ApiBanner";
import EmptyState from "./EmptyState";
import LoadingSkeleton from "./LoadingSkeleton";
import Pagination from "./Pagination";
import ThemeTabs from "./ThemeTabs";
import FilterPanel, { buildActiveChips } from "./FilterPanel";
import type { PanelFilterState, PanelPatch } from "./FilterPanel";

export type CatalogKind = "anime" | "manga";
type CatalogSort = "popularity" | "rating" | "year";

const CATALOG_SORTS = ["popularity", "rating", "year"] as const;

function numOrNull(s: string): number | null {
  const v = s.trim();
  if (!v) return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

/** Стан каталогу, відновлюваний з URL (?format=&status=&genres=&country=&minRating=…&sort=&page=). */
function parseCatalogParams(sp: URLSearchParams) {
  const csv = (k: string): string[] => {
    const v = sp.get(k);
    return v
      ? v
          .split(",")
          .map((x) => x.trim())
          .filter(Boolean)
      : [];
  };
  const num = (k: string): number | null => numOrNull(sp.get(k) ?? "");
  const sortRaw = sp.get("sort");
  return {
    format: sp.get("format") || null,
    status: sp.get("status") || null,
    genres: csv("genres"),
    country: sp.get("country") || null,
    studio: sp.get("studio") || null,
    yearFrom: sp.get("yearFrom") ?? "",
    yearTo: sp.get("yearTo") ?? "",
    minRating: num("minRating"),
    maxRating: num("maxRating"),
    minRuntime: num("minRuntime"),
    maxRuntime: num("maxRuntime"),
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

  // Довідник фільтрів цієї категорії (кеш TTL 10 хв + дедуп у useFiltersData;
  // ендпоінт недоступний → null → панель деградує до статики, сітка не блокується).
  const filtersData = useFiltersData(kind);

  // Початковий стан — з URL (переживає reload/back-forward); далі URL — дзеркало стану.
  const [init] = useState(() => parseCatalogParams(new URLSearchParams(searchParams.toString())));
  const [format, setFormat] = useState<string | null>(init.format);
  const [status, setStatus] = useState<string | null>(init.status);
  const [genres, setGenres] = useState<string[]>(init.genres);
  const [country, setCountry] = useState<string | null>(init.country);
  const [studio, setStudio] = useState<string | null>(init.studio);
  const [yearFrom, setYearFrom] = useState(init.yearFrom);
  const [yearTo, setYearTo] = useState(init.yearTo);
  const [years, setYears] = useState<{ from: number | null; to: number | null }>({
    from: numOrNull(init.yearFrom),
    to: numOrNull(init.yearTo),
  });
  const [ratingFrom, setRatingFrom] = useState(init.minRating != null ? String(init.minRating) : "");
  const [ratingTo, setRatingTo] = useState(init.maxRating != null ? String(init.maxRating) : "");
  const [rating, setRating] = useState<{ min: number | null; max: number | null }>({
    min: init.minRating,
    max: init.maxRating,
  });
  const [runtimeFrom, setRuntimeFrom] = useState(init.minRuntime != null ? String(init.minRuntime) : "");
  const [runtimeTo, setRuntimeTo] = useState(init.maxRuntime != null ? String(init.maxRuntime) : "");
  const [runtime, setRuntime] = useState<{ min: number | null; max: number | null }>({
    min: init.minRuntime,
    max: init.maxRuntime,
  });
  const [sort, setSort] = useState<CatalogSort>(init.sort);

  // Серверна пагінація: page належить хуку, старт — з URL.
  // studio поки НЕ летить у пошук (бекенд /api/search не приймає studio) і не
  // входить у resetKey — значення живе в URL, фільтр запрацює з підтримкою бекенда.
  const fetchPage = useCallback(
    ({ page: pg, omitTotal }: FetchPageArgs) =>
      searchTitles(
        {
          type: kind,
          format,
          status,
          genres,
          country,
          yearFrom: years.from,
          yearTo: years.to,
          minRating: rating.min,
          maxRating: rating.max,
          minRuntime: runtime.min,
          maxRuntime: runtime.max,
          sort,
          page: pg,
          pageSize: PAGE_SIZE,
          omitTotal,
        },
        lang,
      ),
    [kind, format, status, genres, country, years, rating, runtime, sort, lang],
  );
  const list = usePagedSearch(
    fetchPage,
    JSON.stringify([kind, format, status, genres, country, years, rating, runtime, sort]) +
      "|" +
      lang,
    true,
    init.page,
  );

  // UX-медіа: батч-підгрузка постерів для карток без posterUrl (≤24 id/запит,
  // 1 in-flight, дебаунс 300 мс). Мердж — через mergeItems usePagedSearch:
  // той самий state-апдейт, що й у пагінації; resolved id отримують posterUrl
  // і більше не колектяться (misses не повторюються) — циклів нема.
  usePosterBackfill(list.items, (map) => {
    list.mergeItems((prev) =>
      prev.map((it) => {
        if (it.posterUrl) return it;
        const hit = map.get(it.id);
        return hit ? { ...it, posterUrl: hit.posterUrl ?? null } : it;
      }),
    );
  });

  // ── URL-дзеркало: стан → ?params ──
  // replace — для зміни фільтрів (reset не засмічує історію);
  // push — для перегортань сторінок (Back повертає до попередньої сторінки результатів).
  const buildQs = useCallback(
    (page: number) => {
      const sp = new URLSearchParams();
      if (format) sp.set("format", format);
      if (status) sp.set("status", status);
      if (genres.length) sp.set("genres", genres.join(","));
      if (country) sp.set("country", country);
      if (years.from != null) sp.set("yearFrom", String(years.from));
      if (years.to != null) sp.set("yearTo", String(years.to));
      if (rating.min != null) sp.set("minRating", String(rating.min));
      if (rating.max != null) sp.set("maxRating", String(rating.max));
      if (runtime.min != null) sp.set("minRuntime", String(runtime.min));
      if (runtime.max != null) sp.set("maxRuntime", String(runtime.max));
      if (studio) sp.set("studio", studio);
      if (sort !== "popularity") sp.set("sort", sort);
      if (page > 1) sp.set("page", String(page));
      return sp.toString();
    },
    [format, status, genres, country, years, rating, runtime, studio, sort],
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
    setGenres(p.genres);
    setCountry(p.country);
    setStudio(p.studio);
    setRatingFrom(p.minRating != null ? String(p.minRating) : "");
    setRatingTo(p.maxRating != null ? String(p.maxRating) : "");
    setRuntimeFrom(p.minRuntime != null ? String(p.minRuntime) : "");
    setRuntimeTo(p.maxRuntime != null ? String(p.maxRuntime) : "");
    setRating({ min: p.minRating, max: p.maxRating });
    setRuntime({ min: p.minRuntime, max: p.maxRuntime });
    setSort(p.sort);
    list.goTo(p.page);
    // list.goTo стабільний; читаємо лише searchParams, інакше цикл на кожен рендер
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  // ── Панель фільтрів: чіпи/селюшки застосовуються негайно (onPatch),
  // числові інпути — чернетка (onDraft) із commit на blur/Enter (onCommit). ──
  const panelState: PanelFilterState = {
    format,
    status,
    studio,
    genres,
    country,
    yearFrom,
    yearTo,
    minRating: ratingFrom,
    maxRating: ratingTo,
    minRuntime: runtimeFrom,
    maxRuntime: runtimeTo,
    sort,
  };
  const patchPanel = useCallback(
    (p: PanelPatch) => {
      if ("format" in p) setFormat(p.format ?? null);
      if ("status" in p) setStatus(p.status ?? null);
      if ("studio" in p) setStudio(p.studio ?? null);
      if ("genres" in p) setGenres(p.genres ?? []);
      if ("country" in p) setCountry(p.country ?? null);
      if ("sort" in p) setSort(p.sort as CatalogSort);
      if ("yearFrom" in p || "yearTo" in p) {
        const nf = "yearFrom" in p ? (p.yearFrom ?? "") : yearFrom;
        const nt = "yearTo" in p ? (p.yearTo ?? "") : yearTo;
        setYearFrom(nf);
        setYearTo(nt);
        setYears({ from: numOrNull(nf), to: numOrNull(nt) });
      }
      if ("minRating" in p || "maxRating" in p) {
        const nf = "minRating" in p ? (p.minRating ?? "") : ratingFrom;
        const nt = "maxRating" in p ? (p.maxRating ?? "") : ratingTo;
        setRatingFrom(nf);
        setRatingTo(nt);
        setRating({ min: numOrNull(nf), max: numOrNull(nt) });
      }
      if ("minRuntime" in p || "maxRuntime" in p) {
        const nf = "minRuntime" in p ? (p.minRuntime ?? "") : runtimeFrom;
        const nt = "maxRuntime" in p ? (p.maxRuntime ?? "") : runtimeTo;
        setRuntimeFrom(nf);
        setRuntimeTo(nt);
        setRuntime({ min: numOrNull(nf), max: numOrNull(nt) });
      }
    },
    [yearFrom, yearTo, ratingFrom, ratingTo, runtimeFrom, runtimeTo],
  );
  const draftPanel = useCallback((p: PanelPatch) => {
    if ("yearFrom" in p) setYearFrom(p.yearFrom ?? "");
    if ("yearTo" in p) setYearTo(p.yearTo ?? "");
    if ("minRating" in p) setRatingFrom(p.minRating ?? "");
    if ("maxRating" in p) setRatingTo(p.maxRating ?? "");
    if ("minRuntime" in p) setRuntimeFrom(p.minRuntime ?? "");
    if ("maxRuntime" in p) setRuntimeTo(p.maxRuntime ?? "");
  }, []);
  const commitRanges = useCallback(() => {
    setYears({ from: numOrNull(yearFrom), to: numOrNull(yearTo) });
    setRating({ min: numOrNull(ratingFrom), max: numOrNull(ratingTo) });
    setRuntime({ min: numOrNull(runtimeFrom), max: numOrNull(runtimeTo) });
  }, [yearFrom, yearTo, ratingFrom, ratingTo, runtimeFrom, runtimeTo]);

  function resetFilters() {
    setFormat(null);
    setStatus(null);
    setGenres([]);
    setCountry(null);
    setStudio(null);
    setYearFrom("");
    setYearTo("");
    setYears({ from: null, to: null });
    setRatingFrom("");
    setRatingTo("");
    setRating({ min: null, max: null });
    setRuntimeFrom("");
    setRuntimeTo("");
    setRuntime({ min: null, max: null });
    setSort("popularity");
  }

  const activeChips = buildActiveChips(
    {
      format,
      status,
      studio,
      genres,
      country,
      yearFrom: years.from,
      yearTo: years.to,
      minRating: rating.min,
      maxRating: rating.max,
      minRuntime: runtime.min,
      maxRuntime: runtime.max,
    },
    t,
    lang,
  );

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

  // Тими: /anime має активну тему anime; на /manga теми серед чіпів нема —
  // підсвічених нема (current поза списком тем). Вибір іншої тими веде на
  // відповідний каталог (?category= на Головній).
  const selectTheme = useCallback(
    (v: "movie" | "series" | "cartoon" | "anime" | null) => {
      if (v === "anime") {
        router.push("/anime");
        return;
      }
      router.push(v ? `/?category=${v}` : "/");
    },
    [router],
  );

  const emptyKey = kind === "anime" ? "anime.empty" : "manga.empty";
  const titleKey = kind === "anime" ? "anime.title" : "manga.title";
  const subtitleKey = kind === "anime" ? "anime.subtitle" : "manga.subtitle";
  const emptyIcon = kind === "anime" ? "📺" : "📚";

  return (
    <div>
      <section className="mb-4">
        <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
          {t(titleKey)}
        </h1>
        <p className="mt-1 max-w-2xl text-sm text-mute">{t(subtitleKey)}</p>
      </section>

      <ThemeTabs
        current={kind}
        counts={filtersData.data?.counts ?? null}
        onSelect={selectTheme}
      />

      <FilterPanel
        category={kind}
        data={filtersData.data}
        dataLoading={filtersData.loading}
        state={panelState}
        onPatch={patchPanel}
        onDraft={draftPanel}
        onCommit={commitRanges}
        onReset={resetFilters}
        chips={activeChips}
        allowedSorts={CATALOG_SORTS}
      />

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
            className={`grid grid-cols-2 gap-3 pb-24 transition-opacity duration-200 sm:grid-cols-3 md:grid-cols-4 md:pb-0 lg:grid-cols-5 xl:grid-cols-6 ${
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
              onNextHover={() => {
                if (list.canNext) list.prefetchPage(list.page + 1);
              }}
            />
          )}
        </>
      )}
    </div>
  );
}
