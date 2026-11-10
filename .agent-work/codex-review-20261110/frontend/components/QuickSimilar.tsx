"use client";

import { useEffect, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import { useI18n } from "../i18n/I18nContext";
import { getTitle, searchTitles } from "../lib/api";
import { posterUrlFull } from "../lib/constants";
import type { TitleCard as TitleCardType } from "../lib/types";
import PosterImage from "./PosterImage";

const DEBOUNCE_MS = 400;
const MIN_QUERY = 2;

function typeKey(type: TitleCardType["type"]): string {
  switch (type) {
    case "movie":
      return "filters.movie";
    case "series":
      return "filters.series";
    case "anime":
      return "filters.anime";
    case "manga":
      return "filters.manga";
    default:
      return "filters.show";
  }
}

// Швидкий режим «Схоже на...»: одне поле → дропдаун підказок → вибір тайтла
// = ОДРАЗУ пошук схожих за одним сидом (без кнопок-посередників).
// Помилки пошуку показуємо інлайн-банером — мовчазний автокомпліт (стара
// поведінка SeedsBuilder) тут свідомо не повторюємо.
export default function QuickSimilar({
  picked,
  onPick,
  onClear,
}: {
  picked: TitleCardType | null;
  onPick: (t: TitleCardType) => void;
  onClear: () => void;
}) {
  const { t, lang } = useI18n();
  const [query, setQuery] = useState("");
  const [options, setOptions] = useState<TitleCardType[]>([]);
  const [open, setOpen] = useState(false);
  const [noResults, setNoResults] = useState(false);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState(false);
  const boxRef = useRef<HTMLDivElement>(null);
  // Захист від гонок: відповіді застарілих запитів не застосовуються.
  const seqRef = useRef(0);

  // Клік поза полем закриває дропдаун.
  useEffect(() => {
    function onDoc(e: MouseEvent) {
      if (boxRef.current && !boxRef.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener("mousedown", onDoc);
    return () => document.removeEventListener("mousedown", onDoc);
  }, []);

  // FR-21: ?seed={id} — авто-прогін зі сторінки тайтла/картки (внутрішній id —
  // універсальний ключ: у 2.8 млн IMDb-рядків tmdb_id = NULL). Резолвимо тайтл
  // через detail-endpoint, додаємо чіп і запускаємо пошук через onPick.
  // Без резолву — чіп-заглушка: пошук усе одно йде по id (graceful).
  const searchParams = useSearchParams();
  const seedNum = Number(searchParams.get("seed"));
  const appliedSeedRef = useRef<number | null>(null);
  useEffect(() => {
    if (!Number.isFinite(seedNum) || seedNum <= 0) return;
    if (appliedSeedRef.current === seedNum) return;
    appliedSeedRef.current = seedNum;
    let alive = true;
    void (async () => {
      let card: TitleCardType | null = null;
      try {
        const d = await getTitle(seedNum, lang);
        if (d) card = d;
      } catch {
        // нема резолву — чіп-заглушка, пошук не блокуємо
      }
      if (!alive) return;
      onPick(
        card ?? {
          id: seedNum,
          tmdbId: null,
          type: "movie",
          title: t("similar.seedFallback", { n: seedNum }),
        },
      );
    })();
    return () => {
      alive = false;
    };
  }, [seedNum, lang, onPick, t]);

  // Debounced автокомпліт (400 мс).
  useEffect(() => {
    const q = query.trim();
    if (q.length < MIN_QUERY) {
      seqRef.current += 1; // інвалідуємо відповідь, що ще летить
      setOptions([]);
      setOpen(false);
      setNoResults(false);
      setSearching(false);
      setSearchError(false);
      return;
    }
    const timer = setTimeout(async () => {
      const seq = ++seqRef.current;
      setSearching(true);
      setSearchError(false);
      try {
        const r = await searchTitles({ q, pageSize: 8, page: 1 }, lang);
        if (seq !== seqRef.current) return;
        // FR-21: by-seeds резолвить внутрішні id — сидом може бути будь-який тайтл
        // каталогу (фільтр по tmdbId знімано: він обрізав 2.8 млн IMDb-рядків).
        const items = r.items ?? [];
        setOptions(items);
        setNoResults(items.length === 0);
        setOpen(true);
        setSearchError(false);
      } catch {
        if (seq !== seqRef.current) return;
        setOptions([]);
        setOpen(false);
        setNoResults(false);
        setSearchError(true);
      } finally {
        if (seq === seqRef.current) setSearching(false);
      }
    }, DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [query, lang]);

  const resetSuggestions = () => {
    setOptions([]);
    setOpen(false);
    setNoResults(false);
    setSearchError(false);
  };

  const pick = (o: TitleCardType) => {
    onPick(o);
    setQuery("");
    resetSuggestions();
  };

  const examples = t("similar.examples")
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean);
  const showExamples = !picked && query.trim().length === 0;

  return (
    <div className="rounded-2xl border border-line bg-panel p-4 sm:p-5">
      {/* Пошукове поле з дропдауном підказок */}
      <div ref={boxRef} className="relative">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onFocus={() => (options.length > 0 || noResults) && setOpen(true)}
          onKeyDown={(e) => {
            if (e.key === "Escape") {
              setQuery("");
              resetSuggestions();
            }
            if (e.key === "Enter" && open && options.length > 0) {
              e.preventDefault();
              pick(options[0]);
            }
          }}
          placeholder={t("similar.searchPlaceholder")}
          className="w-full rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30"
        />
        {open && (options.length > 0 || noResults) && (
          <ul className="absolute z-30 mt-1 max-h-72 w-full overflow-y-auto rounded-xl border border-line bg-panel shadow-xl">
            {options.map((o) => {
              const thumb = posterUrlFull(o.posterUrl, "w92");
              return (
                <li key={o.id}>
                  <button
                    type="button"
                    onMouseDown={(e) => {
                      e.preventDefault(); // не втрачати фокус
                      pick(o);
                    }}
                    className="flex w-full items-center gap-3 px-3 py-2 text-left text-sm text-ink transition-colors hover:bg-panel-2"
                  >
                    {thumb ? (
                      // UX-02: thumbnail з чесними станами; ytimg-прев'ю posterUrlFull відкидає
                      // (cheap-guard) → metadata-missing, бо прев'ю відео — не постер тайтла.
                      <span className="block h-12 w-9 shrink-0 overflow-hidden rounded-md bg-panel-2">
                        <PosterImage src={thumb} title={o.title} compact />
                      </span>
                    ) : (
                      <span
                        aria-hidden
                        className="flex h-12 w-9 shrink-0 items-center justify-center rounded-md bg-panel-2 text-lg"
                      >
                        🎞️
                      </span>
                    )}
                    <span className="min-w-0 flex-1 truncate font-medium">{o.title}</span>
                    <span className="shrink-0 text-xs text-mute">
                      {o.year != null ? `${o.year} · ` : ""}
                      {t(typeKey(o.type))}
                    </span>
                  </button>
                </li>
              );
            })}
            {noResults && options.length === 0 && (
              <li className="px-3 py-2 text-sm text-mute">{t("search.noResults")}</li>
            )}
          </ul>
        )}
      </div>

      {/* Обраний тайтл лишається чіпом — × прибирає його і скидає результати */}
      {picked && (
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <span className="inline-flex items-center gap-1.5 rounded-full border border-accent/40 bg-accent/10 py-1 pl-3 pr-1.5 text-xs font-medium text-accent-2">
            {picked.title}
            {picked.year != null ? ` (${picked.year})` : ""}
            <button
              type="button"
              aria-label={`${t("similar.clear")}: ${picked.title}`}
              onClick={onClear}
              className="rounded-full px-1 text-accent-2/70 hover:bg-accent/20 hover:text-accent-2"
            >
              ✕
            </button>
          </span>
        </div>
      )}

      {/* Порожній стан: приклади-підказки (клік підставляє назву в поле) */}
      {showExamples && examples.length > 0 && (
        <div className="mt-3 flex flex-wrap items-center gap-1.5 text-xs text-mute">
          <span>{t("similar.tryExamples")}</span>
          {examples.map((ex) => (
            <button
              key={ex}
              type="button"
              onClick={() => setQuery(ex)}
              className="rounded-full border border-line bg-bg px-2.5 py-1 text-xs text-mute transition-colors hover:border-accent/50 hover:text-accent-2"
            >
              {ex}
            </button>
          ))}
        </div>
      )}

      {/* Індикатор пошуку підказок — щоб поле не «мовчало» поки летить запит */}
      {searching && <p className="mt-2 text-xs text-mute">{t("common.loading")}</p>}

      {/* Помилка пошуку підказок — інлайн-банер, не мовчки */}
      {searchError && (
        <div
          role="alert"
          className="mt-3 flex items-center gap-2 rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-2.5 text-sm text-amber-300"
        >
          <span aria-hidden>⚠️</span>
          <span>{t("similar.searchUnavailable")}</span>
        </div>
      )}
    </div>
  );
}
