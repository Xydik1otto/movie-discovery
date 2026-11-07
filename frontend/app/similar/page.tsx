"use client";

import { useRef, useState } from "react";
import { useI18n } from "@/i18n/I18nContext";
import { guardedBySeeds, guardedByText } from "@/lib/fallback";
import type { SimilarItem, TitleCard as TitleCardType } from "@/lib/types";
import SeedsBuilder from "@/components/SeedsBuilder";
import QuickSimilar from "@/components/QuickSimilar";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";

interface HistoryEntry {
  label: string;
  items: SimilarItem[];
  unavailable: boolean;
}

type Mode = "quick" | "seeds";

// Дворежимна сторінка «Схоже на...»:
//  — «Швидко» (дефолт): один тайтл з дропдауна → одразу результати;
//  — «Еталони»: старий мультисід-конструктор (1–10 seed'ів) для точного тюнінгу.
export default function SimilarPage() {
  const { t, lang } = useI18n();

  const [mode, setMode] = useState<Mode>("quick");

  const [seeds, setSeeds] = useState<TitleCardType[]>([]);
  const [picked, setPicked] = useState<TitleCardType | null>(null);
  const [busy, setBusy] = useState(false);
  const [items, setItems] = useState<SimilarItem[] | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);
  const [history, setHistory] = useState<HistoryEntry[]>([]);

  const [textForm, setTextForm] = useState("");
  const [textBusy, setTextBusy] = useState(false);
  const [textError, setTextError] = useState(false);

  // Захист від гонок: відповіді застарілих запитів (після нового запиту,
  // скидання чіпа чи перемикання режиму) не застосовуються.
  const seqRef = useRef(0);

  function pushHistory(label: string, data: SimilarItem[], un: boolean) {
    setHistory((prev) => [{ label, items: data, unavailable: un }, ...prev].slice(0, 8));
  }

  function clearResults() {
    setItems(null);
    setUnavailable(false);
    setError(false);
  }

  async function runBySeeds(list: TitleCardType[], label: string) {
    // by-seeds приймає лише tmdbId — тайтли без нього (аніме/манга з імпорту) відкидаємо.
    const seedIds = list
      .map((s) => s.tmdbId)
      .filter((x): x is number => x != null);
    if (!seedIds.length) {
      setError(true);
      return;
    }
    const seq = ++seqRef.current;
    setBusy(true);
    setError(false);
    setUnavailable(false);
    try {
      const r = await guardedBySeeds(seedIds, lang);
      if (seq !== seqRef.current) return;
      setItems(r.data);
      setUnavailable(r.unavailable);
      pushHistory(label, r.data, r.unavailable);
    } catch {
      if (seq !== seqRef.current) return;
      setError(true);
      setItems(null);
    } finally {
      if (seq === seqRef.current) setBusy(false);
    }
  }

  // Режим «Еталони»: мультисід-пошук за кнопкою.
  async function find() {
    if (!seeds.length) return;
    await runBySeeds(seeds, seeds.map((s) => s.title).join(" + "));
  }

  // Режим «Швидко»: вибір тайтла в дропдауні = одразу пошук за одним сидом.
  function quickPick(x: TitleCardType) {
    setPicked(x);
    void runBySeeds([x], x.title);
  }

  function quickClear() {
    seqRef.current += 1; // скидаємо відповідь, що ще летить
    setBusy(false);
    setPicked(null);
    clearResults();
  }

  function switchMode(next: Mode) {
    if (next === mode) return;
    seqRef.current += 1; // відповідь, що летить, не має «догнати» інший режим
    setBusy(false);
    setTextBusy(false);
    setMode(next);
  }

  async function findByText() {
    const text = textForm.trim();
    if (text.length < 3) return;
    const seq = ++seqRef.current;
    setTextBusy(true);
    setTextError(false);
    try {
      const r = await guardedByText(text, lang);
      if (seq !== seqRef.current) return;
      setItems(r.data);
      setUnavailable(r.unavailable);
      pushHistory(`✍️ ${text.slice(0, 60)}${text.length > 60 ? "…" : ""}`, r.data, r.unavailable);
    } catch {
      if (seq !== seqRef.current) return;
      setTextError(true);
    } finally {
      if (seq === seqRef.current) setTextBusy(false);
    }
  }

  // Спільний блок результатів (грід карток) для обох режимів.
  function resultsBlock(loading: boolean, emptyTitle: string) {
    return (
      <div className="min-w-0">
        {unavailable && <ApiBanner />}
        {error ? (
          <div
            role="alert"
            className="flex items-center gap-2 rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-2.5 text-sm text-amber-300"
          >
            <span aria-hidden>⚠️</span>
            <span>{t("similar.similarError")}</span>
          </div>
        ) : loading ? (
          <LoadingSkeleton count={10} />
        ) : items ? (
          items.length === 0 ? (
            <EmptyState title={t("search.noResults")} />
          ) : (
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-4">
              {items.map((item) => (
                <TitleCard
                  key={`${item.id}-${item.tmdbId}`}
                  item={item}
                  score={item.score}
                  reasonsLabel={t("title.why")}
                />
              ))}
            </div>
          )
        ) : (
          <EmptyState icon="🔍" title={emptyTitle} />
        )}
      </div>
    );
  }

  const tabCls = (active: boolean) =>
    `rounded-lg px-4 py-2 text-sm font-bold transition-colors ${
      active ? "bg-accent text-white" : "text-mute hover:text-ink"
    }`;

  return (
    <div>
      <h1 className="mb-1 text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("similar.seedsTitle")}
      </h1>
      <p className="mb-5 max-w-2xl text-sm text-mute">
        {mode === "quick" ? t("similar.quickHint") : t("similar.seedsHint")}
      </p>

      {/* Перемикач режимів: «Швидко» (дефолт) | «Еталони» */}
      <div className="mb-5 inline-flex rounded-xl border border-line bg-panel p-1" role="tablist">
        <button
          type="button"
          role="tab"
          aria-selected={mode === "quick"}
          onClick={() => switchMode("quick")}
          className={tabCls(mode === "quick")}
        >
          {t("similar.quickMode")}
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={mode === "seeds"}
          onClick={() => switchMode("seeds")}
          className={tabCls(mode === "seeds")}
        >
          {t("similar.seedsMode")}
        </button>
      </div>

      {mode === "quick" ? (
        <div className="space-y-5">
          <QuickSimilar picked={picked} onPick={quickPick} onClear={quickClear} />
          {resultsBlock(busy, t("similar.quickHint"))}
        </div>
      ) : (
        <div className="grid gap-5 lg:grid-cols-[420px_1fr]">
          <div className="space-y-4">
            <SeedsBuilder
              seeds={seeds}
              onAdd={(x) => setSeeds((prev) => [...prev, x])}
              onRemove={(id) => setSeeds((prev) => prev.filter((s) => s.id !== id))}
              onClear={() => setSeeds([])}
              onFind={find}
              busy={busy}
            />

            {/* Пошук за вільним текстом */}
            <div className="rounded-2xl border border-line bg-panel p-4 sm:p-5">
              <h2 className="mb-1 text-base font-bold text-ink">{t("similar.byText")}</h2>
              <textarea
                value={textForm}
                onChange={(e) => setTextForm(e.target.value)}
                rows={3}
                placeholder={t("similar.byTextPlaceholder")}
                className="w-full resize-none rounded-xl border border-line bg-bg px-4 py-2.5 text-sm text-ink placeholder:text-mute/60 focus:border-accent focus:outline-none focus:ring-2 focus:ring-accent/30"
              />
              <button
                type="button"
                disabled={textBusy || textForm.trim().length < 3}
                onClick={findByText}
                className="mt-3 w-full rounded-xl border border-accent/50 bg-accent/10 px-4 py-2.5 text-sm font-bold text-accent-2 transition-colors enabled:hover:bg-accent/20 disabled:opacity-50"
              >
                {textBusy ? t("common.loading") : t("similar.textFind")}
              </button>
              {textError && (
                <p className="mt-2 text-sm text-amber-300">{t("common.error")}</p>
              )}
            </div>

            {/* Історія добірок */}
            {history.length > 0 && (
              <div className="rounded-2xl border border-line bg-panel p-4 sm:p-5">
                <h2 className="mb-2 text-base font-bold text-ink">{t("similar.history")}</h2>
                <ul className="space-y-1">
                  {history.map((h, i) => (
                    <li key={i}>
                      <button
                        type="button"
                        onClick={() => {
                          setItems(h.items);
                          setUnavailable(h.unavailable);
                        }}
                        className="w-full truncate rounded-lg px-2 py-1.5 text-left text-sm text-mute transition-colors hover:bg-panel-2 hover:text-ink"
                        title={h.label}
                      >
                        {h.label}
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>

          {resultsBlock(busy || textBusy, t("similar.seedsHint"))}
        </div>
      )}
    </div>
  );
}
