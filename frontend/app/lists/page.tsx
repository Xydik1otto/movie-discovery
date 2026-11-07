"use client";

import { useEffect, useState } from "react";
import { useI18n } from "@/i18n/I18nContext";
import { deleteListItem, guardedLists, patchListItem } from "@/lib/fallback";
import type { ListKind, ListItem } from "@/lib/types";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";

export default function ListsPage() {
  const { t, lang } = useI18n();
  const [kind, setKind] = useState<ListKind>("watchlist");
  const [items, setItems] = useState<ListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);
  const [actionError, setActionError] = useState(false);

  useEffect(() => {
    let alive = true;
    setLoading(true);
    setError(false);
    setActionError(false);
    guardedLists(kind, lang)
      .then((r) => {
        if (!alive) return;
        setItems(r.data);
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
  }, [kind, lang]);

  function itemIdOf(item: ListItem): number {
    return item.itemId ?? item.id;
  }

  async function remove(item: ListItem) {
    setActionError(false);
    setItems((prev) => prev.filter((x) => x.id !== item.id));
    if (unavailable) return; // демо-режим — лише локально
    try {
      await deleteListItem(itemIdOf(item));
    } catch {
      setActionError(true);
      setItems((prev) => [...prev, item]); // відкат
    }
  }

  async function rate(item: ListItem, rating: number) {
    setActionError(false);
    setItems((prev) =>
      prev.map((x) => (x.id === item.id ? { ...x, rating } : x)),
    );
    if (unavailable) return; // демо-режим
    try {
      await patchListItem(itemIdOf(item), rating);
    } catch {
      setActionError(true);
    }
  }

  const tabBtn = (k: ListKind, label: string) => (
    <button
      key={k}
      type="button"
      role="tab"
      aria-selected={kind === k}
      onClick={() => setKind(k)}
      className={`rounded-xl px-4 py-2 text-sm font-semibold transition-colors ${
        kind === k ? "bg-accent text-white" : "bg-panel text-mute hover:text-ink"
      }`}
    >
      {label}
    </button>
  );

  return (
    <div>
      <h1 className="mb-4 text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("nav.lists")}
      </h1>

      <div role="tablist" className="mb-4 flex gap-2">
        {tabBtn("watchlist", t("lists.watchlist"))}
        {tabBtn("watched", t("lists.watched"))}
      </div>

      {unavailable && <ApiBanner />}
      {actionError && (
        <p className="mb-3 text-sm text-amber-300">{t("common.error")}</p>
      )}

      {loading ? (
        <LoadingSkeleton count={6} />
      ) : error ? (
        <EmptyState icon="⚠️" title={t("common.error")} />
      ) : items.length === 0 ? (
        <EmptyState />
      ) : (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
          {items.map((item) => (
            <TitleCard
              key={`${kind}-${item.id}`}
              item={item}
              eager={kind === "watchlist"}
              footer={
                <div className="mt-2 flex items-center gap-1.5">
                  {kind === "watched" && (
                    <select
                      value={item.rating ?? ""}
                      onChange={(e) =>
                        e.target.value && rate(item, Number(e.target.value))
                      }
                      aria-label={t("lists.rating")}
                      className="rounded-lg border border-line bg-panel px-1.5 py-1 text-xs text-ink focus:border-accent focus:outline-none"
                    >
                      <option value="">{t("lists.rating")}</option>
                      {Array.from({ length: 10 }, (_, i) => (
                        <option key={i + 1} value={i + 1}>
                          {i +1}
                        </option>
                      ))}
                    </select>
                  )}
                  {kind === "watched" && item.rating != null && (
                    <span className="rounded-md border border-amber-400/30 bg-amber-400/10 px-1.5 py-0.5 text-[11px] font-bold text-amber-300">
                      ★ {item.rating}
                    </span>
                  )}
                  <button
                    type="button"
                    onClick={() => remove(item)}
                    aria-label={t("lists.remove")}
                    title={t("lists.remove")}
                    className="ml-auto rounded-lg border border-line bg-panel px-2 py-1 text-xs text-mute transition-colors hover:border-red-500/50 hover:text-red-400"
                  >
                    🗑
                  </button>
                </div>
              }
            />
          ))}
        </div>
      )}
    </div>
  );
}
