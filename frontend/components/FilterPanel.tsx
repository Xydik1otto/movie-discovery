"use client";

// Плаваюча адаптивна панель фільтрів.
//
// Desktop (md+): компактний липкий бар над контентом (sticky top-16 під хедером,
// z-30) — кнопка «Фільтри» з лічильником активних + чіпи активних фільтрів;
// кнопка розгортає повну панель (absolute-випад під баром, закривається Esc/
// кліком поза панеллю). Mobile (<md): плаваюча кнопка (FAB, bottom-right) →
// bottom sheet висувається знизу (backdrop + скрол, Esc/×/тап по фону).
//
// АДАПТИВНІСТЬ: секції беруться з GET /api/filters?category=<поточна>
// (кеш TTL 10 хв + дедуп у lib/useFiltersData) і рендеряться ЛИШЕ релевантні
// категорії (SECTIONS_BY_CATEGORY); секція з порожніми/відсутніми даними НЕ
// рендериться; ендпоінт недоступний → статичні довідники constants.ts
// («наявні хардкод-фільтри») без падіння. Поки фільтри вантажаться — скелетон
// всередині панелі; сітка результатів не блокується.
//
// Контракт зі сторінкою: сторінка володіє станом; панель лише малює.
//  • onPatch  — негайна (застосована) зміна: чіпи/селюшки/скидання чіпа;
//  • onDraft  — чернеткова зміна числових інпутів (без рефетчу);
//  • onCommit — закомітити чернетки діапазонів (blur/Enter у панелі).
// Студії: вибір зберігається в URL ?studio=, але пошук поки НЕ фільтрує за
// студією (бекенд /api/search ще не приймає studio) — секція з'являється, щойно
// /api/filters поверне studios, і запрацює в пошуку, коли бекенд додасть параметр.

import { useEffect, useRef, useState, type ReactNode } from "react";
import { useI18n } from "../i18n/I18nContext";
import type { Lang } from "../i18n/I18nContext";
import { ActiveFilterChip, Chip, ChipGroup } from "./FilterChips";
import {
  ANIME_MEDIA_FORMATS,
  COUNTRY_OPTIONS,
  GENRES,
  MANGA_FORMATS,
  SORTS,
  formatLabel,
  type LocalizedOption,
} from "../lib/constants";
import type { FilterCountOption, FiltersPayload } from "../lib/types";

/** Чернетковий стан фільтрів панелі (числові поля — рядки інпутів до commit). */
export interface PanelFilterState {
  format: string | null;
  status: string | null;
  studio: string | null;
  genres: string[];
  country: string | null;
  yearFrom: string;
  yearTo: string;
  minRating: string;
  maxRating: string;
  minRuntime: string;
  maxRuntime: string;
  sort: string;
}

export type PanelPatch = Partial<{
  format: string | null;
  status: string | null;
  studio: string | null;
  genres: string[];
  country: string | null;
  yearFrom: string;
  yearTo: string;
  minRating: string;
  maxRating: string;
  minRuntime: string;
  maxRuntime: string;
  sort: string;
}>;

/** Застосований (committed) стан — з нього будуються чіпи активних фільтрів. */
export interface PanelAppliedState {
  format: string | null;
  status: string | null;
  studio: string | null;
  genres: string[];
  country: string | null;
  yearFrom: number | null;
  yearTo: number | null;
  minRating: number | null;
  maxRating: number | null;
  minRuntime: number | null;
  maxRuntime: number | null;
}

export interface ActiveFilter {
  key: string;
  label: string;
  /** Що зняти зі стану (застосовується негайно через onPatch). */
  clear: PanelPatch;
}

type SectionKey =
  | "format"
  | "genres"
  | "years"
  | "countries"
  | "studios"
  | "statuses"
  | "rating"
  | "runtime"
  | "sort";

/** Які секції релевантні категорії (порожні з даних все одно не рендеряться).
 *  movie: формати(короткометражка)/жанри/роки/країна/рейтинг/тривалість/сортування;
 *  series: як фільми + статус; anime: + студії/статус; cartoon: як фільми без країн;
 *  manga/dorama: мінімум. */
const SECTIONS_BY_CATEGORY: Record<string, SectionKey[]> = {
  movie: ["format", "genres", "years", "countries", "rating", "runtime", "sort"],
  series: ["format", "genres", "years", "countries", "statuses", "rating", "runtime", "sort"],
  anime: ["format", "genres", "years", "countries", "studios", "statuses", "rating", "runtime", "sort"],
  cartoon: ["format", "genres", "years", "rating", "runtime", "sort"],
  manga: ["format", "years", "sort"],
  dorama: ["genres", "years", "countries", "sort"],
  tvshow: ["genres", "years", "sort"],
};
// Без категорії (Головна без вибраної тими) — базовий набір.
const DEFAULT_SECTIONS: SectionKey[] = ["genres", "years", "countries", "rating", "runtime", "sort"];

const STATUS_LABEL_KEY: Record<string, string> = {
  airing: "anime.airing",
  finished: "anime.finished",
  returning: "title.status.returning",
  ended: "title.status.ended",
  released: "title.status.released",
  canceled: "title.status.canceled",
};

const SORT_LABEL_KEY: Record<string, string> = {
  relevance: "filters.sort.relevance",
  rating: "filters.sort.rating",
  imdb: "filters.sort.imdb",
  votes: "filters.sort.votes",
  year: "filters.sort.year",
  popularity: "filters.sort.popularity",
};

// Статичні статуси (деградація без /api/filters): серіали «виходить/завершено»,
// аніме — airing/finished (як на каталозі зараз).
const STATIC_STATUSES: Record<string, FilterCountOption[]> = {
  series: [{ value: "returning" }, { value: "ended" }],
  anime: [{ value: "airing" }, { value: "finished" }],
};

function optionLabel(options: LocalizedOption[], value: string, lang: Lang): string {
  return options.find((o) => o.value === value)?.[lang] ?? value;
}

/** Активні фільтри → локалізовані чіпи (викликає сторінка зі ЗДІЙСНЕНОГО стану). */
export function buildActiveChips(
  a: PanelAppliedState,
  t: (key: string, vars?: Record<string, string | number>) => string,
  lang: Lang,
): ActiveFilter[] {
  const chips: ActiveFilter[] = [];
  if (a.format) {
    chips.push({
      key: "format",
      label: a.format === "short" ? t("filters.short") : (formatLabel(a.format, lang) ?? a.format),
      clear: { format: null },
    });
  }
  if (a.status) {
    const k = STATUS_LABEL_KEY[a.status];
    chips.push({ key: "status", label: k ? t(k) : a.status, clear: { status: null } });
  }
  if (a.studio) {
    chips.push({ key: "studio", label: a.studio, clear: { studio: null } });
  }
  for (const g of a.genres) {
    chips.push({
      key: `genre:${g}`,
      label: optionLabel(GENRES, g, lang),
      clear: { genres: a.genres.filter((x) => x !== g) },
    });
  }
  if (a.country) {
    chips.push({
      key: "country",
      label: optionLabel(COUNTRY_OPTIONS, a.country, lang),
      clear: { country: null },
    });
  }
  if (a.yearFrom != null || a.yearTo != null) {
    chips.push({
      key: "years",
      label: `${t("filters.year")}: ${a.yearFrom ?? "…"}–${a.yearTo ?? "…"}`,
      clear: { yearFrom: "", yearTo: "" },
    });
  }
  if (a.minRating != null) {
    chips.push({
      key: "minRating",
      label: `${t("filters.rating")}: ≥${a.minRating}`,
      clear: { minRating: "" },
    });
  }
  if (a.maxRating != null) {
    chips.push({
      key: "maxRating",
      label: `${t("filters.rating")}: ≤${a.maxRating}`,
      clear: { maxRating: "" },
    });
  }
  if (a.minRuntime != null) {
    chips.push({
      key: "minRuntime",
      label: `${t("filters.runtime")}: ≥${t("title.runtimeMin", { n: a.minRuntime })}`,
      clear: { minRuntime: "" },
    });
  }
  if (a.maxRuntime != null) {
    chips.push({
      key: "maxRuntime",
      label: `${t("filters.runtime")}: ≤${t("title.runtimeMin", { n: a.maxRuntime })}`,
      clear: { maxRuntime: "" },
    });
  }
  return chips;
}

const INPUT_CLS =
  "rounded-lg border border-line bg-bg px-2 py-1.5 text-sm focus:border-accent focus:outline-none";
const LABEL_CLS = "mb-1.5 text-xs font-semibold uppercase tracking-wide text-mute";

/** Пара числових інпутів «від–до»: зміна = чернетка (onDraft), Enter/blur = commit. */
function NumPair({
  label,
  from,
  to,
  onFrom,
  onTo,
  onCommit,
  step,
  min,
  max,
  placeholderFrom,
  placeholderTo,
}: {
  label: string;
  from: string;
  to: string;
  onFrom: (v: string) => void;
  onTo: (v: string) => void;
  onCommit: () => void;
  step?: string;
  min?: number;
  max?: number;
  placeholderFrom: string;
  placeholderTo: string;
}) {
  return (
    <div>
      <div className={LABEL_CLS}>{label}</div>
      <div className="flex items-center gap-1.5">
        <input
          type="number"
          inputMode="numeric"
          step={step}
          min={min}
          max={max}
          value={from}
          onChange={(e) => onFrom(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && onCommit()}
          onBlur={onCommit}
          placeholder={placeholderFrom}
          aria-label={`${label} ${placeholderFrom}`}
          className={`w-full min-w-16 ${INPUT_CLS}`}
        />
        <span className="text-mute">–</span>
        <input
          type="number"
          inputMode="numeric"
          step={step}
          min={min}
          max={max}
          value={to}
          onChange={(e) => onTo(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && onCommit()}
          onBlur={onCommit}
          placeholder={placeholderTo}
          aria-label={`${label} ${placeholderTo}`}
          className={`w-full min-w-16 ${INPUT_CLS}`}
        />
      </div>
    </div>
  );
}

/** Селюшка «один із» (країна/студія/сортування): порожнє значення = не фільтр. */
function SelectOne({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  options: { value: string; label: string }[];
}) {
  const { t } = useI18n();
  return (
    <div>
      <div className={LABEL_CLS}>{label}</div>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className={`w-full ${INPUT_CLS}`}
      >
        <option value="">{t("filters.any")}</option>
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </div>
  );
}

function PanelSkeleton() {
  return (
    <div className="grid gap-x-6 gap-y-4 sm:grid-cols-2" aria-hidden>
      {[0, 1, 2, 3].map((i) => (
        <div key={i} className="animate-pulse">
          <div className="mb-2 h-3 w-24 rounded bg-panel-2" />
          <div className="h-8 rounded-lg bg-panel-2" />
        </div>
      ))}
    </div>
  );
}

export default function FilterPanel({
  category,
  data,
  dataLoading,
  state,
  onPatch,
  onDraft,
  onCommit,
  onReset,
  chips,
  allowedSorts,
  onApply,
  advanced,
}: {
  /** Поточна категорія (movie|series|cartoon|anime|manga|dorama|tvshow) або null. */
  category: string | null;
  /** Довідник з /api/filters (кешований); null = вантажиться або недоступний. */
  data: FiltersPayload | null;
  dataLoading: boolean;
  state: PanelFilterState;
  onPatch: (p: PanelPatch) => void;
  onDraft: (p: PanelPatch) => void;
  onCommit: () => void;
  onReset: () => void;
  /** Активні фільтри (лічильник = chips.length — на кнопці-розгортачі). */
  chips: ActiveFilter[];
  /** Дозволені коди сортування цієї сторінки (невідомі значення з API відкидаються). */
  allowedSorts: readonly string[];
  /** Кнопка «Застосувати» (Головна: розширені текстові фільтри). */
  onApply?: () => void;
  /** Слот «Розширені фільтри» (JSX сторінки; стан живе на сторінці). */
  advanced?: ReactNode;
}) {
  const { t, lang } = useI18n();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement | null>(null);

  // Закриття: Escape будь-де; клік поза панеллю (desktop-випад).
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("mousedown", onDown);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("mousedown", onDown);
    };
  }, [open]);

  // Mobile bottom sheet: блокуємо скрол сторінки, поки відкрита (лише <md).
  useEffect(() => {
    if (!open) return;
    if (window.matchMedia("(min-width: 768px)").matches) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = prev;
    };
  }, [open]);

  const f = data?.filters ?? null;
  const apiGenres = f?.genres?.length ? f.genres : null;
  const apiYears = f?.years ?? null;
  const apiCountries = f?.countries?.length ? f.countries : null;
  const apiStudios = f?.studios?.length ? f.studios : null;
  const apiStatuses = f?.statuses?.length ? f.statuses : null;
  const apiSorts = f?.sorts?.length ? f.sorts : null;

  const genreLabel = (v: string) => optionLabel(GENRES, v, lang);
  const countryLabel = (v: string) => optionLabel(COUNTRY_OPTIONS, v, lang);
  const statusLabel = (v: string) => {
    const k = STATUS_LABEL_KEY[v];
    return k ? t(k) : v;
  };
  const withCount = (label: string, c: number | null | undefined) =>
    c != null && c > 0 ? `${label} (${c})` : label;

  const sections = SECTIONS_BY_CATEGORY[category ?? ""] ?? DEFAULT_SECTIONS;

  // Формати: статика за категорією (фільм/анімація → короткометражка;
  // аніме/манга → наявні довідники форматів).
  const formatOptions: { value: string; label: string }[] =
    category === "anime"
      ? ANIME_MEDIA_FORMATS.map((o) => ({ value: o.value, label: o[lang] }))
      : category === "manga"
        ? MANGA_FORMATS.map((o) => ({ value: o.value, label: o[lang] }))
        : category === "movie" || category === "cartoon"
          ? [{ value: "short", label: t("filters.short") }]
          : [];

  const sortChoices = (() => {
    const src: { value: string }[] = apiSorts ?? SORTS.map((s) => ({ value: s.value }));
    const allowed = src.filter((s) => allowedSorts.includes(s.value));
    const list = allowed.length
      ? allowed
      : SORTS.filter((s) => allowedSorts.includes(s.value)).map((s) => ({ value: s.value }));
    return list.map((s) => ({
      value: s.value,
      label: SORT_LABEL_KEY[s.value] ? t(SORT_LABEL_KEY[s.value]) : s.value,
    }));
  })();

  const renderSection = (key: SectionKey): ReactNode => {
    switch (key) {
      case "format": {
        if (!formatOptions.length) return null;
        return (
          <div key="format">
            <div className={LABEL_CLS}>{t("filters.format")}</div>
            <ChipGroup>
              {formatOptions.map((o) => (
                <Chip
                  key={o.value}
                  small
                  selected={state.format === o.value}
                  onClick={() => onPatch({ format: state.format === o.value ? null : o.value })}
                >
                  {o.label}
                </Chip>
              ))}
            </ChipGroup>
          </div>
        );
      }
      case "genres": {
        const list: FilterCountOption[] = apiGenres ?? GENRES.map((g) => ({ value: g.value }));
        return (
          <div key="genres">
            <div className={LABEL_CLS}>{t("filters.genre")}</div>
            <ChipGroup>
              {list.map((g) => (
                <Chip
                  key={g.value}
                  small
                  count={g.count}
                  selected={state.genres.includes(g.value)}
                  onClick={() =>
                    onPatch({
                      genres: state.genres.includes(g.value)
                        ? state.genres.filter((x) => x !== g.value)
                        : [...state.genres, g.value],
                    })
                  }
                >
                  {genreLabel(g.value)}
                </Chip>
              ))}
            </ChipGroup>
          </div>
        );
      }
      case "years": {
        return (
          <NumPair
            key="years"
            label={t("filters.year")}
            from={state.yearFrom}
            to={state.yearTo}
            onFrom={(v) => onDraft({ yearFrom: v })}
            onTo={(v) => onDraft({ yearTo: v })}
            onCommit={onCommit}
            placeholderFrom={apiYears?.min != null ? String(apiYears.min) : t("filters.min")}
            placeholderTo={apiYears?.max != null ? String(apiYears.max) : t("filters.max")}
          />
        );
      }
      case "countries": {
        const list: FilterCountOption[] =
          apiCountries ?? COUNTRY_OPTIONS.map((c) => ({ value: c.value }));
        return (
          <SelectOne
            key="countries"
            label={t("filters.country")}
            value={state.country ?? ""}
            onChange={(v) => onPatch({ country: v || null })}
            options={list.map((c) => ({
              value: c.value,
              label: withCount(countryLabel(c.value), c.count),
            }))}
          />
        );
      }
      case "studios": {
        if (!apiStudios) return null; // студії — лише з /api/filters (статичного довідника нема)
        return (
          <SelectOne
            key="studios"
            label={t("filters.studios")}
            value={state.studio ?? ""}
            onChange={(v) => onPatch({ studio: v || null })}
            options={apiStudios.map((s) => ({
              value: s.value,
              label: withCount(s.value, s.count),
            }))}
          />
        );
      }
      case "statuses": {
        const list = apiStatuses ?? STATIC_STATUSES[category ?? ""] ?? [];
        if (!list.length) return null;
        return (
          <div key="statuses">
            <div className={LABEL_CLS}>{t("title.status")}</div>
            <ChipGroup>
              {list.map((s) => (
                <Chip
                  key={s.value}
                  small
                  count={s.count}
                  selected={state.status === s.value}
                  onClick={() => onPatch({ status: state.status === s.value ? null : s.value })}
                >
                  {statusLabel(s.value)}
                </Chip>
              ))}
            </ChipGroup>
          </div>
        );
      }
      case "rating": {
        return (
          <NumPair
            key="rating"
            label={t("filters.rating")}
            from={state.minRating}
            to={state.maxRating}
            onFrom={(v) => onDraft({ minRating: v })}
            onTo={(v) => onDraft({ maxRating: v })}
            onCommit={onCommit}
            step="0.5"
            min={0}
            max={10}
            placeholderFrom={t("filters.min")}
            placeholderTo={t("filters.max")}
          />
        );
      }
      case "runtime": {
        return (
          <NumPair
            key="runtime"
            label={t("filters.runtime")}
            from={state.minRuntime}
            to={state.maxRuntime}
            onFrom={(v) => onDraft({ minRuntime: v })}
            onTo={(v) => onDraft({ maxRuntime: v })}
            onCommit={onCommit}
            min={0}
            placeholderFrom={t("filters.min")}
            placeholderTo={t("filters.max")}
          />
        );
      }
      case "sort": {
        return (
          <SelectOne
            key="sort"
            label={t("filters.sort")}
            value={state.sort}
            onChange={(v) => onPatch({ sort: v })}
            options={sortChoices}
          />
        );
      }
      default:
        return null;
    }
  };

  const body = (
    <>
      {dataLoading && !data ? (
        <PanelSkeleton />
      ) : (
        <div className="grid gap-x-6 gap-y-4 sm:grid-cols-2 xl:grid-cols-3">
          {sections.map(renderSection)}
        </div>
      )}
      {advanced ? <div className="mt-4 border-t border-line pt-3">{advanced}</div> : null}
      <div className="mt-4 flex flex-wrap gap-2 border-t border-line pt-3">
        {onApply ? (
          <button
            type="button"
            onClick={onApply}
            className="rounded-xl bg-accent px-5 py-2 text-sm font-bold text-white transition-all hover:bg-accent-2 active:scale-95"
          >
            {t("filters.apply")}
          </button>
        ) : null}
        <button
          type="button"
          onClick={onReset}
          className="rounded-xl border border-line bg-panel-2 px-4 py-2 text-sm text-mute transition-colors hover:text-ink"
        >
          {t("filters.reset")}
        </button>
      </div>
    </>
  );

  const badge =
    chips.length > 0 ? (
      <span className="flex h-5 min-w-5 items-center justify-center rounded-full bg-accent px-1.5 text-[11px] font-bold text-white">
        {chips.length}
      </span>
    ) : null;

  return (
    <>
      {/* Desktop: липкий бар + випадна панель */}
      <div ref={rootRef} className="sticky top-16 z-30 mb-5 hidden md:block">
        <div className="relative rounded-2xl border border-line bg-panel/95 shadow-lg shadow-black/20 backdrop-blur">
          <div className="flex items-center gap-2 px-3 py-2">
            <button
              type="button"
              onClick={() => setOpen((v) => !v)}
              aria-expanded={open}
              className="flex shrink-0 items-center gap-2 rounded-xl border border-line bg-panel-2 px-3.5 py-2 text-sm font-semibold text-ink transition-colors hover:border-accent/50"
            >
              <span aria-hidden>☰</span>
              {t("filters.panel.title")}
              {badge}
            </button>
            {chips.length > 0 ? (
              <>
                <div className="no-scrollbar flex min-w-0 flex-1 items-center gap-1.5 overflow-x-auto">
                  {chips.map((c) => (
                    <ActiveFilterChip
                      key={c.key}
                      label={c.label}
                      onClear={() => onPatch(c.clear)}
                    />
                  ))}
                </div>
                <button
                  type="button"
                  onClick={onReset}
                  className="shrink-0 rounded-xl border border-line bg-panel-2 px-3 py-2 text-xs text-mute transition-colors hover:text-ink"
                >
                  {t("filters.reset")}
                </button>
              </>
            ) : null}
          </div>
          {open ? (
            <div className="absolute inset-x-0 top-full mt-2 max-h-[70vh] overflow-y-auto rounded-2xl border border-line bg-panel p-4 shadow-2xl shadow-black/40">
              {body}
            </div>
          ) : null}
        </div>
      </div>

      {/* Mobile: плаваюча кнопка → bottom sheet */}
      <button
        type="button"
        onClick={() => setOpen(true)}
        className="fixed bottom-5 right-4 z-40 flex items-center gap-2 rounded-full bg-accent px-4 py-3 text-sm font-bold text-white shadow-lg shadow-black/30 transition-transform active:scale-95 md:hidden"
      >
        <span aria-hidden>☰</span>
        {t("filters.panel.title")}
        {badge}
      </button>
      {open ? (
        <div className="fixed inset-0 z-50 md:hidden">
          <div
            className="absolute inset-0 bg-black/60"
            onClick={() => setOpen(false)}
            aria-hidden
          />
          <div
            role="dialog"
            aria-modal="true"
            aria-label={t("filters.panel.title")}
            className="absolute inset-x-0 bottom-0 max-h-[85vh] overflow-y-auto rounded-t-2xl border-t border-line bg-panel"
          >
            <div className="sticky top-0 z-10 flex items-center justify-between gap-2 border-b border-line bg-panel px-4 py-3">
              <span className="text-sm font-bold">{t("filters.panel.title")}</span>
              <button
                type="button"
                onClick={() => setOpen(false)}
                aria-label={t("filters.panel.close")}
                className="rounded-full px-2 py-1 text-lg leading-none text-mute transition-colors hover:bg-panel-2 hover:text-ink"
              >
                ×
              </button>
            </div>
            <div className="p-4">{body}</div>
          </div>
        </div>
      ) : null}
    </>
  );
}
