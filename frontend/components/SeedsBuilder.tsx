"use client";

import { useEffect, useRef, useState } from "react";
import { useI18n } from "../i18n/I18nContext";
import { guardedSearch } from "../lib/fallback";
import type { TitleCard as TitleCardType } from "../lib/types";

const MAX_SEEDS = 10;

// Мультисід-конструктор: пошук-автокомпліт додає 1–10 еталонів чіпами.
export default function SeedsBuilder({
  seeds,
  onAdd,
  onRemove,
  onClear,
  onFind,
  busy,
}: {
  seeds: TitleCardType[];
  onAdd: (t: TitleCardType) => void;
  onRemove: (id: number) => void;
  onClear: () => void;
  onFind: () => void;
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

  const canFind = seeds.length >= 1 && seeds.length <= MAX_SEEDS && !busy;

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

      <div className="mt-4 flex items-center gap-3">
        <button
          type="button"
          disabled={!canFind}
          onClick={onFind}
          className="rounded-xl bg-accent px-5 py-2.5 text-sm font-bold text-white transition-all enabled:hover:bg-accent-2 enabled:active:scale-95 disabled:cursor-not-allowed opacity-50 enabled:opacity-100"
        >
          {busy ? `${t("common.loading")}` : t("similar.find")}
        </button>
        {seeds.length === 0 && <span className="text-xs text-mute">{t("similar.seedsRequired")}</span>}
      </div>
    </div>
  );
}
