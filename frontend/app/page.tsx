"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useI18n } from "@/i18n/I18nContext";
import { guardedBySeeds, guardedSearch } from "@/lib/fallback";
import { COUNTRIES, GENRES, SORTS, TITLE_TYPES } from "@/lib/constants";
import type { Paged, SimilarItem, TitleCard as TitleCardType } from "@/lib/types";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";
import Pagination from "@/components/Pagination";
import SeedsBuilder from "@/components/SeedsBuilder";
import { Chip, ChipGroup } from "@/components/FilterChips";

const PAGE_SIZE = 24;

interface Applied {
  q: string;
  type: string | null;
  genres: string[];
  yearFrom: number | null;
  yearTo: number | null;
  country: string | null;
  minRating: number | null;
  maxRating: number | null;
  minRuntime: number | null;
  maxRuntime: number | null;
  sort: "relevance" | "rating" | "year" | "popularity";
}

const EMPTY: Applied = {
  q: "",
  type: null,
  genres: [],
  yearFrom: null,
  yearTo: null,
  country: null,
  minRating: null,
  maxRating: null,
  minRuntime: null,
  maxRuntime: null,
  sort: "popularity",
};

function numOrNull(s: string): number | null {
  const v = s.trim();
  if (!v) return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

export default function HomePage() {
  const { t, lang } = useI18n();
  const [tab, setTab] = useState<"filters" | "similar">("filters");

  // --- Вкладка «За параметрами» ---
  const [qForm, setQForm] = useState("");
  const [type, setType] = useState<string | null>(null);
  const [genres, setGenres] = useState<string[]>([]);
  const [yearFrom, setYearFrom] = useState("");
  const [yearTo, setYearTo] = useState("");
  const [country, setCountry] = useState("");
  const [minRating, setMinRating] = useState("");
  const [maxRating, setMaxRating] = useState("");
  const [minRuntime, setMinRuntime] = useState("");
  const [maxRuntime, setMaxRuntime] = useState("");
  const [applied, setApplied] = useState<Applied>(EMPTY);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<TitleCardType> | null>(null);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);

  function apply() {
    const q = qForm.trim();
    setApplied({
      q,
      type,
      genres,
      yearFrom: numOrNull(yearFrom),
      yearTo: numOrNull(yearTo),
      country: country || null,
      minRating: numOrNull(minRating),
      maxRating: numOrNull(maxRating),
      minRuntime: numOrNull(minRuntime),
      maxRuntime: numOrNull(maxRuntime),
      sort: q ? "relevance" : "popularity",
    });
    setPage(1);
  }

  function reset() {
    setQForm("");
    setType(null);
    setGenres([]);
    setYearFrom("");
    setYearTo("");
    setCountry("");
    setMinRating("");
    setMaxRating("");
    setMinRuntime("");
    setMaxRuntime("");
    setApplied(EMPTY);
    setPage(1);
  }

  useEffect(() => {
    let alive = true;
    setLoading(true);
    setError(false);
    guardedSearch({ ...applied, page, pageSize: PAGE_SIZE }, lang)
      .then((r) => {
        if (!alive) return;
        setData(r.data);
        setUnavailable(r.unavailable);
      })
      .catch(() => {
        if (alive) setError(true);
      })
      .finally(() => {
        if (alive) setLoading(false);
      });
    return () => {
      alive = false;
    };
  }, [applied, page, lang]);

  // --- Вкладка «Схоже на...» ---
  const [seeds, setSeeds] = useState<TitleCardType[]>([]);
  const [similarBusy, setSimilarBusy] = useState(false);
  const [similarItems, setSimilarItems] = useState<SimilarItem[] | null>(null);
  const [similarUnavailable, setSimilarUnavailable] = useState(false);
  const [similarError, setSimilarError] = useState(false);

  async function findSimilar() {
    if (!seeds.length) return;
    setSimilarBusy(true);
    setSimilarError(false);
    try {
      const r = await guardedBySeeds(
        seeds.map((s) => s.tmdbId),
        lang,
      );
      setSimilarItems(r.data);
      setSimilarUnavailable(r.unavailable);
    } catch {
      setSimilarError(true);
    } finally {
      setSimilarBusy(false);
    }
  }

  const tabBtn = (key: "filters" | "similar", label: string) => (
    <button
      key={key}
      type="button"
      aria-selected={tab === key}
      role="tab"
      onClick={() => setTab(key)}
      className={`rounded-xl px-4 py-2 text-sm font-semibold transition-colors ${
        tab === key ? "bg-accent text-white" : "bg-panel text-mute hover:text-ink"
      }`}
    >
      {label}
    </button>
  );

  return (
    <div>
      {/* Герой */}
      <section className="mb-6">
        <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
          {t("brand.name")}
        </h1>
        <p className="mt-1 max-w-2xl text-sm text-mute">{t("brand.tagline")}</p>
      </section>

      {/* Вкладки */}
      <div role="tablist" className="mb-4 flex gap-2">
        {tabBtn("filters", t("home.tabFilters"))}
        {tabBtn("similar", t("home.tabSimilar"))}
      </div>

      {tab === "filters" ? (
        <section>
          {/* Пошуковий бар */}
          <div className="mb-4 flex gap-2">
            <input
              type="text"
              value={qForm}
              onChange={(e) => setQForm(e.target.value)}
              onKeyDown={(e) => e.key === "Enter" && apply()}
              placeholder={t("search.placeholder")}
              className="min-w-0 flex-1 rounded-xl border border-line bg-panel px-4 py-3 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30"
            />
            <button
              type="button"
              onClick={apply}
              className="shrink-0 rounded-xl bg-accent px-5 py-3 text-sm font-bold text-white transition-all hover:bg-accent-2 active:scale-95"
            >
              {t("search.button")}
            </button>
          </div>

          {/* Фільтри */}
          <div className="mb-5 space-y-3 rounded-2xl border border-line bg-panel p-4">
            <div>
              <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-mute">
                {t("filters.type")}
              </div>
              <ChipGroup>
                {TITLE_TYPES.map((tt) => (
                  <Chip
                    key={tt.value}
                    selected={type === tt.value}
                    onClick={() => setType(type === tt.value ? null : tt.value)}
                  >
                    {t(tt.key)}
                  </Chip>
                ))}
              </ChipGroup>
            </div>

            <div>
              <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-mute">
                {t("filters.genre")}
              </div>
              <ChipGroup>
                {GENRES.map((g) => (
                  <Chip
                    key={g.value}
                    small
                    selected={genres.includes(g.value)}
                    onClick={() =>
                      setGenres((prev) =>
                        prev.includes(g.value)
                          ? prev.filter((x) => x !== g.value)
                          : [...prev, g.value],
                      )
                    }
                  >
                    {lang === "uk" ? g.uk : g.en}
                  </Chip>
                ))}
              </ChipGroup>
            </div>

            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
              <label className="block">
                <span className="mb-1 block text-xs text-mute">{t("filters.year")}</span>
                <div className="flex items-center gap-1.5">
                  <input
                    type="number"
                    inputMode="numeric"
                    value={yearFrom}
                    onChange={(e) => setYearFrom(e.target.value)}
                    placeholder={t("filters.min")}
                    aria-label={`${t("filters.year")} ${t("filters.min")}`}
                    className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                  />
                  <span className="text-mute">–</span>
                  <input
                    type="number"
                    inputMode="numeric"
                    value={yearTo}
                    onChange={(e) => setYearTo(e.target.value)}
                    placeholder={t("filters.max")}
                    aria-label={`${t("filters.year")} ${t("filters.max")}`}
                    className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                  />
                </div>
              </label>

              <label className="block">
                <span className="mb-1 block text-xs text-mute">{t("filters.country")}</span>
                <select
                  value={country}
                  onChange={(e) => setCountry(e.target.value)}
                  className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                >
                  <option value="">{t("filters.any")}</option>
                  {COUNTRIES.map((c) => (
                    <option key={c.value} value={c.value}>
                      {lang === "uk" ? c.uk : c.en}
                    </option>
                  ))}
                </select>
              </label>

              <label className="block">
                <span className="mb-1 block text-xs text-mute">{t("filters.rating")}</span>
                <div className="flex items-center gap-1.5">
                  <input
                    type="number"
                    step="0.5"
                    min="0"
                    max="10"
                    value={minRating}
                    onChange={(e) => setMinRating(e.target.value)}
                    placeholder={t("filters.min")}
                    aria-label={`${t("filters.rating")} ${t("filters.min")}`}
                    className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                  />
                  <span className="text-mute">–</span>
                  <input
                    type="number"
                    step="0.5"
                    min="0"
                    max="10"
                    value={maxRating}
                    onChange={(e) => setMaxRating(e.target.value)}
                    placeholder={t("filters.max")}
                    aria-label={`${t("filters.rating")} ${t("filters.max")}`}
                    className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                  />
                </div>
              </label>

              <label className="block">
                <span className="mb-1 block text-xs text-mute">
                  {t("filters.runtime")} ({t("filters.min")})
                </span>
                <input
                  type="number"
                  inputMode="numeric"
                  min="0"
                  value={minRuntime}
                  onChange={(e) => setMinRuntime(e.target.value)}
                  placeholder="90"
                  className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                />
              </label>

              <div className="grid grid-cols-[1fr_auto] items-end gap-3">
                <label className="block">
                  <span className="mb-1 block text-xs text-mute">
                    {t("filters.runtime")} ({t("filters.max")})
                  </span>
                  <input
                    type="number"
                    inputMode="numeric"
                    min="0"
                    value={maxRuntime}
                    onChange={(e) => setMaxRuntime(e.target.value)}
                    placeholder="180"
                    className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-xs text-mute">{t("filters.sort")}</span>
                  <select
                    value={applied.sort}
                    onChange={(e) =>
                      setApplied((prev) => ({
                        ...prev,
                        sort: e.target.value as Applied["sort"],
                      }))
                    }
                    className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                  >
                    {SORTS.map((s) => (
                      <option key={s.value} value={s.value}>
                        {t(s.key)}
                      </option>
                    ))}
                  </select>
                </label>
              </div>
            </div>

            <div className="flex gap-2 pt-1">
              <button
                type="button"
                onClick={apply}
                className="rounded-xl bg-accent px-5 py-2 text-sm font-bold text-white transition-all hover:bg-accent-2 active:scale-95"
              >
                {t("filters.apply")}
              </button>
              <button
                type="button"
                onClick={reset}
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
            <EmptyState title={t("search.noResults")} />
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
          ) : null}
        </section>
      ) : (
        <section>
          <SeedsBuilder
            seeds={seeds}
            onAdd={(x) => setSeeds((prev) => [...prev, x])}
            onRemove={(id) => setSeeds((prev) => prev.filter((s) => s.id !== id))}
            onClear={() => setSeeds([])}
            onFind={findSimilar}
            busy={similarBusy}
          />

          {similarUnavailable && (
            <div className="mt-4">
              <ApiBanner />
            </div>
          )}

          {similarError ? (
            <div className="mt-4">
              <EmptyState icon="⚠️" title={t("common.error")} />
            </div>
          ) : similarItems ? (
            similarItems.length === 0 ? (
              <div className="mt-4">
                <EmptyState title={t("search.noResults")} />
              </div>
            ) : (
              <>
                <p className="mb-3 mt-5 text-xs text-mute">
                  {t("search.results", { n: similarItems.length })} ·{" "}
                  <Link href="/similar" className="text-accent-2 hover:underline">
                    {t("nav.similar")} →
                  </Link>
                </p>
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
                  {similarItems.map((item) => (
                    <TitleCard
                      key={item.id}
                      item={item}
                      score={item.score}
                      reasonsLabel={t("title.why")}
                    />
                  ))}
                </div>
              </>
            )
          ) : null}
        </section>
      )}
    </div>
  );
}
