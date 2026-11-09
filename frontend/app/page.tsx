"use client";

import { useCallback, useState } from "react";
import Link from "next/link";
import { useI18n } from "@/i18n/I18nContext";
import { guardedBySeedsEx, guardedSearch } from "@/lib/fallback";
import { useProgressiveSearch } from "@/lib/useProgressiveSearch";
import {
  AUDIO_LANG_OPTIONS,
  CATEGORY_OPTIONS,
  COUNTRY_OPTIONS,
  GENRES,
  LANGUAGES,
  QUALITY_OPTIONS,
  SORTS,
  TITLE_TYPES,
} from "@/lib/constants";
import type { SimilarItem, TitleCard as TitleCardType } from "@/lib/types";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";
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
  actor: string;
  director: string;
  keyword: string;
  language: string | null;
  minVotes: number | null;
  seasonsFrom: number | null;
  seasonsTo: number | null;
  excludeGenres: string[];
  hasTrailer: boolean | null;
  watchable: boolean | null;
  ratingSource: string | null;
  // FR-19: категорія/країна/озвучка/якість — усі опційні (null = не фільтр).
  category: string | null;
  audioLang: string | null;
  minQuality: number | null;
  sort: "relevance" | "rating" | "imdb" | "votes" | "year" | "popularity";
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
  actor: "",
  director: "",
  keyword: "",
  language: "",
  minVotes: null,
  seasonsFrom: null,
  seasonsTo: null,
  excludeGenres: [],
  hasTrailer: null,
  watchable: null,
  ratingSource: null,
  category: null,
  audioLang: null,
  minQuality: null,
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
  const [actor, setActor] = useState("");
  const [director, setDirector] = useState("");
  const [keyword, setKeyword] = useState("");
  const [language, setLanguage] = useState("");
  const [minVotes, setMinVotes] = useState("");
  const [seasonsFrom, setSeasonsFrom] = useState("");
  const [seasonsTo, setSeasonsTo] = useState("");
  const [excludeGenres, setExcludeGenres] = useState<string[]>([]);
  const [hasTrailer, setHasTrailer] = useState(false);
  // FR-14: «Можна подивитись» → ?watchable=true (лише тайтли з watch_links).
  const [watchable, setWatchable] = useState(false);
  const [ratingSource, setRatingSource] = useState("");
  // FR-19: категорія/країна/озвучка/якість — dropdown'и, порожнє = не фільтр.
  const [category, setCategory] = useState("");
  const [audioLang, setAudioLang] = useState("");
  const [minQuality, setMinQuality] = useState("");
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [applied, setApplied] = useState<Applied>(EMPTY);

  // Прогресивне завантаження: перша сторінка одразу, решта — інфініт-скролом;
  // під час нового запиту попередні результати лишаються видимими (opacity).
  const fetchPage = useCallback(
    (pg: number) =>
      guardedSearch({ ...applied, page: pg, pageSize: PAGE_SIZE }, lang).then((r) => {
        if (r.unavailable) {
          const e = new Error("api_unavailable");
          e.name = "ApiUnavailableError";
          throw e;
        }
        return r.data;
      }),
    [applied, lang],
  );
  const list = useProgressiveSearch(fetchPage, JSON.stringify(applied) + "|" + lang);

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
      actor: actor.trim(),
      director: director.trim(),
      keyword: keyword.trim(),
      language: language || null,
      minVotes: numOrNull(minVotes),
      seasonsFrom: numOrNull(seasonsFrom),
      seasonsTo: numOrNull(seasonsTo),
      excludeGenres,
      hasTrailer: hasTrailer ? true : null,
      watchable: watchable ? true : null,
      ratingSource: ratingSource || null,
      category: category || null,
      audioLang: audioLang || null,
      minQuality: numOrNull(minQuality),
      sort: q ? "relevance" : "popularity",
    });
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
    setActor("");
    setDirector("");
    setKeyword("");
    setLanguage("");
    setMinVotes("");
    setSeasonsFrom("");
    setSeasonsTo("");
    setExcludeGenres([]);
    setHasTrailer(false);
    setWatchable(false);
    setRatingSource("");
    setCategory("");
    setAudioLang("");
    setMinQuality("");
    setApplied(EMPTY);
  }

  // --- Вкладка «Схоже на...» ---
  const [seeds, setSeeds] = useState<TitleCardType[]>([]);
  const [similarBusy, setSimilarBusy] = useState(false);
  const [similarItems, setSimilarItems] = useState<SimilarItem[] | null>(null);
  const [similarUnavailable, setSimilarUnavailable] = useState(false);
  const [similarError, setSimilarError] = useState(false);

  async function findSimilar() {
    if (!seeds.length) return;
    // MD-03: by-seeds резолвить ВНУТРІШНІ id — вони є в усіх тайтлів, тож
    // аніме/мангу/IMDb-тайтли (tmdbId=null) більше не викидаємо з змішаної
    // вибірки й нічого не втрачаємо мовчки.
    const seedIds = seeds.map((s) => s.id).filter((n) => Number.isFinite(n));
    if (!seedIds.length) {
      // Чесний стан: жодного валідного еталона замість генеричної «Помилки».
      setSimilarError(true);
      return;
    }
    setSimilarBusy(true);
    setSimilarError(false);
    try {
      const r = await guardedBySeedsEx(seedIds, lang);
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

            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
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
              {/* Розширені фільтри (згортаються) */}
            <button
              type="button"
              onClick={() => setAdvancedOpen((v) => !v)}
              className="flex w-full items-center justify-between rounded-xl border border-line bg-panel-2 px-4 py-2 text-sm text-mute transition-colors hover:text-ink"
              aria-expanded={advancedOpen}
            >
              <span className="font-semibold">{t("filters.advanced")}</span>
              <span aria-hidden>{advancedOpen ? "▲" : "▼"}</span>
            </button>

            {advancedOpen && (
              <div className="space-y-3 rounded-xl border border-line/60 bg-panel-2/40 p-3">
                {/* FR-19: категорія / країна / озвучка / якість — усе опційне. */}
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.category")}</span>
                    <select
                      value={category}
                      onChange={(e) => setCategory(e.target.value)}
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    >
                      <option value="">{t("filters.any")}</option>
                      {CATEGORY_OPTIONS.map((c) => (
                        <option key={c.value} value={c.value}>
                          {t(c.key)}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.country")}</span>
                    <select
                      value={country}
                      onChange={(e) => setCountry(e.target.value)}
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    >
                      <option value="">{t("filters.any")}</option>
                      {COUNTRY_OPTIONS.map((c) => (
                        <option key={c.value} value={c.value}>
                          {lang === "uk" ? c.uk : c.en}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.audioLang")}</span>
                    <select
                      value={audioLang}
                      onChange={(e) => setAudioLang(e.target.value)}
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    >
                      <option value="">{t("filters.any")}</option>
                      {AUDIO_LANG_OPTIONS.map((a) => (
                        <option key={a.value} value={a.value}>
                          {t(a.key)}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.minQuality")}</span>
                    <select
                      value={minQuality}
                      onChange={(e) => setMinQuality(e.target.value)}
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    >
                      <option value="">{t("filters.any")}</option>
                      {QUALITY_OPTIONS.map((q) => (
                        <option key={q.value} value={q.value}>
                          {t(q.key)}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>

                <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.actor")}</span>
                    <input
                      type="text"
                      value={actor}
                      onChange={(e) => setActor(e.target.value)}
                      onKeyDown={(e) => e.key === "Enter" && apply()}
                      placeholder="DiCaprio"
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    />
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.director")}</span>
                    <input
                      type="text"
                      value={director}
                      onChange={(e) => setDirector(e.target.value)}
                      onKeyDown={(e) => e.key === "Enter" && apply()}
                      placeholder="Nolan"
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    />
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.keyword")}</span>
                    <input
                      type="text"
                      value={keyword}
                      onChange={(e) => setKeyword(e.target.value)}
                      onKeyDown={(e) => e.key === "Enter" && apply()}
                      placeholder="heist"
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    />
                  </label>
                </div>

                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.language")}</span>
                    <select
                      value={language}
                      onChange={(e) => setLanguage(e.target.value)}
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    >
                      <option value="">{t("filters.any")}</option>
                      {LANGUAGES.map((l) => (
                        <option key={l.value} value={l.value}>
                          {lang === "uk" ? l.uk : l.en}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.ratingSource")}</span>
                    <select
                      value={ratingSource}
                      onChange={(e) => setRatingSource(e.target.value)}
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    >
                      <option value="">{t("filters.ratingAny")}</option>
                      <option value="tmdb">TMDB</option>
                      <option value="imdb">IMDb</option>
                    </select>
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.minVotes")}</span>
                    <input
                      type="number"
                      inputMode="numeric"
                      min="0"
                      value={minVotes}
                      onChange={(e) => setMinVotes(e.target.value)}
                      placeholder="10000"
                      className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                    />
                  </label>
                  <label className="block">
                    <span className="mb-1 block text-xs text-mute">{t("filters.seasons")}</span>
                    <div className="flex items-center gap-1.5">
                      <input
                        type="number"
                        inputMode="numeric"
                        min="1"
                        value={seasonsFrom}
                        onChange={(e) => setSeasonsFrom(e.target.value)}
                        placeholder={t("filters.min")}
                        aria-label={`${t("filters.seasons")} ${t("filters.min")}`}
                        className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                      />
                      <span className="text-mute">–</span>
                      <input
                        type="number"
                        inputMode="numeric"
                        min="1"
                        value={seasonsTo}
                        onChange={(e) => setSeasonsTo(e.target.value)}
                        placeholder={t("filters.max")}
                        aria-label={`${t("filters.seasons")} ${t("filters.max")}`}
                        className="w-full rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none"
                      />
                    </div>
                  </label>
                </div>

                <div className="flex flex-wrap gap-4">
                  <label className="flex items-center gap-2 text-sm text-mute">
                    <input
                      type="checkbox"
                      checked={hasTrailer}
                      onChange={(e) => setHasTrailer(e.target.checked)}
                      className="h-4 w-4 accent-[var(--color-accent)]"
                    />
                    {t("filters.hasTrailer")}
                  </label>
                  <label className="flex items-center gap-2 text-sm text-mute">
                    <input
                      type="checkbox"
                      checked={watchable}
                      onChange={(e) => setWatchable(e.target.checked)}
                      className="h-4 w-4 accent-[var(--color-accent)]"
                    />
                    {t("search.watchable")}
                  </label>
                </div>

                <div>
                  <div className="mb-1.5 text-xs font-semibold uppercase tracking-wide text-mute">
                    {t("filters.excludeGenres")}
                  </div>
                  <ChipGroup>
                    {GENRES.map((g) => (
                      <Chip
                        key={g.value}
                        small
                        selected={excludeGenres.includes(g.value)}
                        onClick={() =>
                          setExcludeGenres((prev) =>
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
              </div>
            )}
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

          {/* Результати — прогресивно: перша сторінка одразу, решта догружається скролом */}
          {list.unavailable && <ApiBanner />}
          {list.firstLoad ? (
            <LoadingSkeleton />
          ) : list.error ? (
            <EmptyState icon="⚠️" title={t("common.error")} />
          ) : list.items.length === 0 && !list.busy ? (
            <EmptyState title={t("search.noResults")} />
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
              {list.canMore && !list.busy && (
                <div className="mt-4 flex justify-center">
                  <button
                    type="button"
                    onClick={list.showMore}
                    className="rounded-xl border border-line bg-panel px-5 py-2.5 text-sm font-semibold text-ink transition-colors hover:border-accent/50"
                  >
                    {t("common.showMore")}
                  </button>
                </div>
              )}
              <div ref={list.sentinelRef} aria-hidden className="h-px w-full" />
            </>
          )}
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
                    <TitleCard key={item.id} item={item} score={item.score} />
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
