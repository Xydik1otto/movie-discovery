"use client";

import { useState } from "react";
import { useI18n } from "@/i18n/I18nContext";
import { guardedBySeeds, guardedByText } from "@/lib/fallback";
import type { SimilarItem, TitleCard as TitleCardType } from "@/lib/types";
import SeedsBuilder from "@/components/SeedsBuilder";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";

interface HistoryEntry {
  label: string;
  items: SimilarItem[];
  unavailable: boolean;
}

export default function SimilarPage() {
  const { t, lang } = useI18n();

  const [seeds, setSeeds] = useState<TitleCardType[]>([]);
  const [busy, setBusy] = useState(false);
  const [items, setItems] = useState<SimilarItem[] | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);
  const [history, setHistory] = useState<HistoryEntry[]>([]);

  const [textForm, setTextForm] = useState("");
  const [textBusy, setTextBusy] = useState(false);
  const [textError, setTextError] = useState(false);

  function pushHistory(label: string, data: SimilarItem[], un: boolean) {
    setHistory((prev) => [{ label, items: data, unavailable: un }, ...prev].slice(0, 8));
  }

  async function find() {
    if (!seeds.length) return;
    // by-seeds приймає лише tmdbId — тайтли без нього (аніме/манга з імпорту) відкидаємо.
    const seedIds = seeds
      .map((s) => s.tmdbId)
      .filter((x): x is number => x != null);
    if (!seedIds.length) {
      setError(true);
      return;
    }
    setBusy(true);
    setError(false);
    try {
      const r = await guardedBySeeds(seedIds, lang);
      setItems(r.data);
      setUnavailable(r.unavailable);
      pushHistory(
        seeds.map((s) => s.title).join(" + "),
        r.data,
        r.unavailable,
      );
    } catch {
      setError(true);
    } finally {
      setBusy(false);
    }
  }

  async function findByText() {
    const text = textForm.trim();
    if (text.length < 3) return;
    setTextBusy(true);
    setTextError(false);
    try {
      const r = await guardedByText(text, lang);
      setItems(r.data);
      setUnavailable(r.unavailable);
      pushHistory(`✍️ ${text.slice(0, 60)}${text.length > 60 ? "…" : ""}`, r.data, r.unavailable);
    } catch {
      setTextError(true);
    } finally {
      setTextBusy(false);
    }
  }

  return (
    <div>
      <h1 className="mb-1 text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("similar.seedsTitle")}
      </h1>
      <p className="mb-5 max-w-2xl text-sm text-mute">{t("similar.seedsHint")}</p>

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

        <div className="min-w-0">
          {unavailable && <ApiBanner />}
          {busy || textBusy ? (
            <LoadingSkeleton count={10} />
          ) : error ? (
            <EmptyState icon="⚠️" title={t("common.error")} />
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
            <EmptyState icon="🎲" title={t("similar.seedsHint")} />
          )}
        </div>
      </div>
    </div>
  );
}
