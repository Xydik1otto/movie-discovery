"use client";

import { useEffect, useRef, useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { guardedSearch } from "../lib/fallback";
import { GENRES } from "../lib/constants";
import type { SimilarSeedsFilters, TitleCard as TitleCardType } from "../lib/types";

const MAX_SEEDS = 10;
const MAX_GENRES = 3;

// FR-21 «Студія підборів»: ті самі чіпи-референси, що й в «Еталонів», плюс
// рамки — рік від/до (два number-input), тип (усі/фільм/серіал/аніме) і
// жанровий акцент (мультіселект до 3). Кнопки «знайти» нема: панель лише
// збирає стан, живий дебаунс-пошук (500 мс) робить сторінка /similar.
export default function StudioPanel({
  seeds,
  filters,
  onAdd,
  onRemove,
  onClear,
  onFilters,
}: {
  seeds: TitleCardType[];
  filters: SimilarSeedsFilters;
  onAdd: (t: TitleCardType) => void;
  onRemove: (id: number) => void;
  onClear: () => void;
  onFilters: (f: SimilarSeedsFilters) => void;
}) {
  const { t, lang } = useI18n();
  const [query, setQuery] = useState("");
  const [options, setOptions] = useState<TitleCardType[]>([]);
  const [open, setOpen] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);

  // Клік поза полем закриває дропдаун.
  useEffect(() => {
    function onDoc(e: MouseEvent) {
      if (boxRef.current && !boxRef.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, []);

  // Debounced автокомпліт референсів (як у SeedsBuilder).
  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      setOptions([]);
      return;
    }
    const timer = setTimeout(async () => {
      try {
        const r = await guardedSearch({ q, pageSize: 8, page: 1 }, lang);
        // FR-21: by-seeds резолвить внутрішні id — опції без обрізання по tmdbId.
        setOptions(r.data.items);
        setOpen(true);
      } catch {
        // помилки автокомпліту не блокують студію
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

  const genreActive = filters.genres ?? [];

  const toggleGenre = (g: string) => {
    if (genreActive.includes(g)) {
      onFilters({ ...filters, genres: genreActive.filter((x) => x !== g) });
    } else if (genreActive.length < MAX_GENRES) {
      onFilters({ ...filters, genres: [...genreActive, g] });
    }
  };

  const setYear = (key: "yearFrom" | "yearTo", v: string) => {
    const num = v.trim() === "" ? null : Number(v);
    onFilters({ ...filters, [key]: num != null && Number.isFinite(num) ? num : null });
  };

  const inputCls =
    "w-full rounded-xl border border-line bg-bg px-3 py-2 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30";

  return (
    <div className="rounded-2xl border border-line bg-panel p-4 sm:p-5">
      <div className="mb-1 flex flex-wrap items-baseline justify-between gap-2">
        <h2 className="text-lg font-bold text-ink">{t("studio.title")}</h2>
        <span className="text-xs text-mute">{t("similar.seedsCount", { n: seeds.length })}</span>
      </div>
      <p className="mb-3 text-xs leading-relaxed text-mute">{t("studio.hint")}</p>

      {/* Пошук референсів з автокомплітом */}
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

      {/* Чіпи референсів */}
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
      {seeds.length === 0 && (
        <p className="mt-3 text-xs text-mute">{t("similar.seedsRequired")}</p>
      )}

      {/* Рамки: роки */}
      <div className="mt-4 grid grid-cols-2 gap-3">
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
            className={inputCls}
          />
        </label>
        <label className="block">
          <span className="mb-1 block text-xs font-semibold text-mute">{t("studio.yearTo")}</span>
          <input
            type="number"
            min={1900}
            max={2100}
            value={filters.yearTo ?? ""}
            onChange={(e) => setYear("yearTo", e.target.value)}
            placeholder="2020"
            className={inputCls}
          />
        </label>
      </div>

      {/* Рамки: тип */}
      <label className="mt-3 block">
        <span className="mb-1 block text-xs font-semibold text-mute">{t("filters.type")}</span>
        <select
          value={filters.type ?? ""}
          onChange={(e) => onFilters({ ...filters, type: e.target.value || null })}
          className={inputCls}
        >
          <option value="">{t("filters.any")}</option>
          <option value="movie">{t("filters.movie")}</option>
          <option value="series">{t("filters.series")}</option>
          <option value="anime">{t("filters.anime")}</option>
        </select>
      </label>

      {/* Рамки: жанровий акцент (до 3) */}
      <div className="mt-3">
        <div className="mb-1.5 flex items-baseline justify-between gap-2">
          <span className="text-xs font-semibold text-mute">{t("studio.genres")}</span>
          <span className="text-[11px] text-mute">{t("studio.genresHint")}</span>
        </div>
        <div className="flex flex-wrap gap-1.5">
          {GENRES.map((g) => {
            const active = genreActive.includes(g.value);
            const disabled = !active && genreActive.length >= MAX_GENRES;
            return (
              <button
                key={g.value}
                type="button"
                disabled={disabled}
                onClick={() => toggleGenre(g.value)}
                aria-pressed={active}
                className={`rounded-full border px-2.5 py-1 text-xs transition-colors ${
                  active
                    ? "border-accent bg-accent/15 text-accent-2"
                    : "border-line bg-bg text-mute hover:border-accent/50 hover:text-accent-2"
                } disabled:opacity-40`}
              >
                {lang === "uk" ? g.uk : g.en}
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
