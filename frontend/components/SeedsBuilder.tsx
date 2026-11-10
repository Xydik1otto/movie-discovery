"use client";

import { useEffect, useRef, useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { guardedSearch } from "../lib/fallback";
import { GENRES } from "../lib/constants";
import type { SimilarSeedsFilters, TitleCard as TitleCardType } from "../lib/types";

const MAX_SEEDS = 10;

// Чіпи типів вхідного фільтра (movie|series|anime|manga). TITLE_TYPES у
// lib/constants.ts не містить anime/manga — тому список локальний.
const SEED_TYPES: { value: string; key: string }[] = [
  { value: "movie", key: "filters.movie" },
  { value: "series", key: "filters.series" },
  { value: "anime", key: "filters.anime" },
  { value: "manga", key: "filters.manga" },
];

// Пресети настрою бекенду (MovieDiscovery.Core.Similarity.MoodPresets) —
// одновибір; повторний клік на активному чіпі знімає вибір.
const MOODS: { value: string; key: string }[] = [
  { value: "cozy", key: "mood.cozy" },
  { value: "tense", key: "mood.tense" },
  { value: "funny", key: "mood.funny" },
  { value: "sad", key: "mood.sad" },
  { value: "epic", key: "mood.epic" },
  { value: "romantic", key: "mood.romantic" },
  { value: "family", key: "mood.family" },
  { value: "mindbender", key: "mood.mindbender" },
];

// Мультисід-конструктор: пошук-автокомпліт додає 1–10 еталонів чіпами, плюс
// панель вхідних фільтрів (типи / жанри / настрій / роки). Еталони тепер
// ОПЦІЙНІ: «Знайти» активне, коли є еталони АБО жанри АБО настрій — бекенд
// приймає 0 ids за наявності genres/mood. Панель фільтрів рендериться лише
// коли передані filters/onFilters (головна сторінка юзає конструктор без неї).
export default function SeedsBuilder({
  seeds,
  filters,
  onAdd,
  onRemove,
  onClear,
  onFind,
  onFilters,
  busy,
}: {
  seeds: TitleCardType[];
  filters?: SimilarSeedsFilters;
  onAdd: (t: TitleCardType) => void;
  onRemove: (id: number) => void;
  onClear: () => void;
  onFind: () => void;
  onFilters?: (f: SimilarSeedsFilters) => void;
  busy: boolean;
}) {
  const { t, lang } = useI18n();
  const [query, setQuery] = useState("");
  const [options, setOptions] = useState<TitleCardType[]>([]);
  const [open, setOpen] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);

  // Клік поза полем закриває дропдаун.
  useEffect(() => {
    function onDoc(e: MouseEvent) {
      if (boxRef.current && !boxRef.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, []);

  // Debounced автокомпліт.
  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      setOptions([]);
      return;
    }
    const timer = setTimeout(async () => {
      abortRef.current?.abort();
      const ctl = new AbortController();
      abortRef.current = ctl;
      try {
        const r = await guardedSearch({ q, pageSize: 8, page: 1 }, lang);
        if (!ctl.signal.aborted) {
          // FR-21: by-seeds резолвить внутрішні id — опції без обрізання по tmdbId.
          setOptions(r.data.items);
          setOpen(true);
        }
      } catch {
        // помилки автокомпліту не блокуємо
      }
    }, 350);
    return () => clearTimeout(timer);
  }, [query, lang]);

  const add = (item: TitleCardType) => {
    if (seeds.length >= MAX_SEEDS) return;
    if (seeds.some((s) => s.id === item.id)) return;
    onAdd(item);
    setQuery("");
    setOptions([]);
    setOpen(false);
  };

  const typesActive = filters?.types ?? [];
  const genresActive = filters?.genres ?? [];
  // Панель фільтрів — тільки коли батько дав стан і сетер (на головній її нема).
  const panelOn = filters != null && onFilters != null;

  const toggleType = (v: string) => {
    if (!filters || !onFilters) return;
    onFilters({
      ...filters,
      types: typesActive.includes(v)
        ? typesActive.filter((x) => x !== v)
        : [...typesActive, v],
    });
  };

  const toggleGenre = (g: string) => {
    if (!filters || !onFilters) return;
    onFilters({
      ...filters,
      genres: genresActive.includes(g)
        ? genresActive.filter((x) => x !== g)
        : [...genresActive, g],
    });
  };

  // Настрій — одновибір: клік на активному чіпі знімає вибір.
  const toggleMood = (m: string) => {
    if (!filters || !onFilters) return;
    onFilters({ ...filters, mood: filters.mood === m ? null : m });
  };

  const setYear = (key: "yearFrom" | "yearTo", v: string) => {
    if (!filters || !onFilters) return;
    const num = v.trim() === "" ? null : Number(v);
    onFilters({ ...filters, [key]: num != null && Number.isFinite(num) ? num : null });
  };

  // Валідація років на фронті: from <= to (інакше блокуємо «Знайти»).
  const yearInvalid =
    filters != null &&
    filters.yearFrom != null &&
    filters.yearTo != null &&
    filters.yearFrom > filters.yearTo;

  const hasFilterInput = genresActive.length >= 1 || Boolean(filters?.mood);
  const hasInput = seeds.length >= 1 || hasFilterInput;
  const canFind = hasInput && seeds.length <= MAX_SEEDS && !yearInvalid && !busy;

  const chipCls = (active: boolean) =>
    `rounded-full border px-2.5 py-1 text-xs transition-colors ${
      active
        ? "border-accent bg-accent/15 text-accent-2"
        : "border-line bg-bg text-mute hover:border-accent/50 hover:text-accent-2"
    }`;
  const yearInputCls =
    "w-full rounded-xl border border-line bg-bg px-3 py-2 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30";

  return (
    <div className="rounded-2xl border border-line bg-panel p-4 sm:p-5">
      <div className="mb-1 flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-lg font-bold text-ink">{t("similar.seedsTitle")}</h2>
        <span className="text-xs text-mute">{t("similar.seedsCount", { n: seeds.length })}</span>
      </div>
      <p className="mb-3 text-xs leading-relaxed text-mute">{t("similar.seedsHint")}</p>

      {/* Пошук еталонів з автокомплітом */}
      <div ref={boxRef} className="relative">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onFocus={() => options.length > 0 && setOpen(true)}
          onKeyDown={(e) => {
            if (e.key === "Escape") setOpen(false);
            if (e.key === "Enter" && options.length > 0 && open) {
              e.preventDefault();
              add(options[0]);
            }
          }}
          placeholder={t("similar.searchSeed")}
          disabled={seeds.length >= MAX_SEEDS}
          className="w-full rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30 disabled:opacity-50"
        />
        {open && options.length > 0 && (
          <ul className="absolute z-30 mt-1 max-h-72 w-full overflow-y-auto rounded-xl border border-line bg-panel shadow-xl">
            {options.map((o) => {
              const picked = seeds.some((s) => s.id === o.id);
              return (
                <li key={o.id}>
                  <button
                    type="button"
                    disabled={picked}
                    onMouseDown={(e) => {
                      e.preventDefault(); // не втрачати фокус
                      add(o);
                    }}
                    className={`flex w-full items-center gap-2 px-3 py-2 text-left text-sm transition-colors ${
                      picked
                        ? "cursor-default text-mute opacity-50"
                        : "text-ink hover:bg-panel-2"
                    }`}
                  >
                    <span aria-hidden>🎞️</span>
                    <span className="min-w-0 flex-1 truncate">
                      {o.title}
                      {o.year != null ? ` (${o.year})` : ""}
                    </span>
                    {picked && <span aria-hidden>✓</span>}
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </div>

      {/* Чіпи еталонів */}
      {seeds.length > 0 && (
        <div className="mt-3 flex flex-wrap gap-1.5">
          {seeds.map((s) => (
            <span
              key={s.id}
              className="inline-flex items-center gap-1.5 rounded-full border border-accent/40 bg-accent/10 py-1 pl-3 pr-1.5 text-xs font-medium text-accent-2"
            >
              {s.title}
              {s.year != null ? ` (${s.year})` : ""}
              <button
                type="button"
                aria-label={`${t("similar.clear")}: ${s.title}`}
                onClick={() => onRemove(s.id)}
                className="rounded-full px-1 text-accent-2/70 hover:bg-accent/20 hover:text-accent-2"
              >
                ✕
              </button>
            </span>
          ))}
          <button
            type="button"
            onClick={onClear}
            className="rounded-full px-2 py-1 text-xs text-mute underline-offset-2 hover:text-ink hover:underline"
          >
            {t("similar.clear")}
          </button>
        </div>
      )}

      {/* Панель вхідних фільтрів: працює і БЕЗ еталонів (жанри/настрій самодостатні).
          Рендериться лише з filters/onFilters — головна юзає конструктор без неї. */}
      {panelOn && (
      <div className="mt-4 space-y-3 border-t border-line pt-3">
        {/* Типи: мультівибір чіпами (hard-фільтр) */}
        <div>
          <span className="mb-1.5 block text-xs font-semibold text-mute">
            {t("similar.fTypes")}
          </span>
          <div className="flex flex-wrap gap-1.5">
            {SEED_TYPES.map((ty) => {
              const active = typesActive.includes(ty.value);
              return (
                <button
                  key={ty.value}
                  type="button"
                  aria-pressed={active}
                  onClick={() => toggleType(ty.value)}
                  className={chipCls(active)}
                >
                  {t(ty.key)}
                </button>
              );
            })}
          </div>
        </div>

        {/* Жанри: чіп-мультівибір (значення українською — бекенд нормалізує uk/en) */}
        <div>
          <span className="mb-1.5 block text-xs font-semibold text-mute">
            {t("similar.fGenres")}
          </span>
          <div className="flex flex-wrap gap-1.5">
            {GENRES.map((g) => {
              const active = genresActive.includes(g.value);
              return (
                <button
                  key={g.value}
                  type="button"
                  aria-pressed={active}
                  onClick={() => toggleGenre(g.value)}
                  className={chipCls(active)}
                >
                  {g[lang]}
                </button>
              );
            })}
          </div>
        </div>

        {/* Настрій: чіп-одновибір (8 пресетів бекенду) */}
        <div>
          <span className="mb-1.5 block text-xs font-semibold text-mute">
            {t("similar.fMood")}
          </span>
          <div className="flex flex-wrap gap-1.5">
            {MOODS.map((m) => {
              const active = filters.mood === m.value;
              return (
                <button
                  key={m.value}
                  type="button"
                  aria-pressed={active}
                  onClick={() => toggleMood(m.value)}
                  className={chipCls(active)}
                >
                  {t(m.key)}
                </button>
              );
            })}
          </div>
        </div>

        {/* Роки: опційні, from <= to перевіряється на фронті */}
        <div className="grid grid-cols-2 gap-3">
          <label className="block">
            <span className="mb-1 block text-xs font-semibold text-mute">
              {t("studio.yearFrom")}
            </span>
            <input
              type="number"
              min={1900}
              max={2100}
              value={filters.yearFrom ?? ""}
              onChange={(e) => setYear("yearFrom", e.target.value)}
              placeholder="1980"
              className={yearInputCls}
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-semibold text-mute">
              {t("studio.yearTo")}
            </span>
            <input
              type="number"
              min={1900}
              max={2100}
              value={filters.yearTo ?? ""}
              onChange={(e) => setYear("yearTo", e.target.value)}
              placeholder="2020"
              className={yearInputCls}
            />
          </label>
        </div>
        {yearInvalid && (
          <p className="text-xs text-amber-300">{t("similar.yearRangeError")}</p>
        )}
      </div>
      )}

      <div className="mt-4 flex items-center gap-3">
        <button
          type="button"
          disabled={!canFind}
          onClick={onFind}
          className="rounded-xl bg-accent px-5 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-95 disabled:cursor-not-allowed opacity-50 enabled:opacity-100"
        >
          {busy ? `${t("common.loading")}` : t("similar.find")}
        </button>
        {!canFind && !busy && !yearInvalid && !hasInput && (
          <span className="text-xs text-mute">{t("similar.seedsOrFiltersRequired")}</span>
        )}
      </div>
    </div>
  );
}
