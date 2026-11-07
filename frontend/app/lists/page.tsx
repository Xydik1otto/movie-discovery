"use client";

import { useCallback, useEffect, useState } from "react";
import { useI18n } from "@/i18n/I18nContext";
import { useAuth } from "@/lib/auth";
import {
  deleteListItem,
  guardedGuestItems,
  guardedLists,
  guardedListStats,
  isUnavailable,
  migrateGuestLists,
  rateListItem,
} from "@/lib/fallback";
import type { ListKind, ListItem, ListStats } from "@/lib/types";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";

const TABS: ListKind[] = ["watchlist", "liked", "watched", "read", "disliked"];
const RATEABLE: ListKind[] = ["watched", "read"];

export default function ListsPage() {
  const { t, lang } = useI18n();
  const { user, ready } = useAuth();
  const [kind, setKind] = useState<ListKind>("watchlist");
  const [items, setItems] = useState<ListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);
  const [actionError, setActionError] = useState(false);
  // FR-17: статистика списків (лічильники + середня оцінка + топ жанрів).
  const [stats, setStats] = useState<ListStats | null>(null);
  const [statsUnavailable, setStatsUnavailable] = useState(false);
  // FR-17: одноразова міграція гостьових списків в акаунт.
  const [guestCount, setGuestCount] = useState<number | null>(null);
  const [migrating, setMigrating] = useState(false);
  const [migrated, setMigrated] = useState(false);
  const [migrateError, setMigrateError] = useState(false);

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

  const loadStats = useCallback(() => {
    guardedListStats(lang)
      .then((r) => {
        setStats(r.data);
        setStatsUnavailable(r.unavailable);
      })
      .catch(() => setStatsUnavailable(true));
  }, [lang]);

  useEffect(() => {
    loadStats();
  }, [loadStats]);

  // Банер міграції: лише для залогінених, одноразово (прапорець у localStorage).
  useEffect(() => {
    if (!ready || !user) {
      setGuestCount(null);
      return;
    }
    let migratedBefore = false;
    try {
      migratedBefore = window.localStorage.getItem("guestMigrated") === "1";
    } catch {
      migratedBefore = false;
    }
    if (migratedBefore) {
      setGuestCount(null);
      return;
    }
    let alive = true;
    guardedGuestItems()
      .then((r) => {
        if (alive) setGuestCount(r.data.length);
      })
      .catch(() => {
        if (alive) setGuestCount(null);
      });
    return () => {
      alive = false;
    };
  }, [ready, user]);

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
    const prevItems = items;
    setItems((prev) =>
      prev.map((x) => (x.id === item.id ? { ...x, rating } : x)),
    );
    loadStats();
    if (unavailable) return; // демо-режим
    try {
      // Контракт FR-17: ідемпотентний POST /api/lists/{kind} {titleId, rating}.
      await rateListItem(kind, { tmdbId: item.tmdbId ?? null, id: item.id }, rating);
    } catch (e) {
      if (isUnavailable(e)) return; // API впав під час дії — лишаємо локально
      setActionError(true);
      setItems(prevItems); // відкат
    }
  }

  async function doMigrate() {
    setMigrating(true);
    setMigrateError(false);
    try {
      const r = await guardedGuestItems();
      if (r.data.length > 0) await migrateGuestLists(r.data);
      try {
        window.localStorage.setItem("guestMigrated", "1");
      } catch {
        // без localStorage банер просто зникне до перезавантаження
      }
      setGuestCount(null);
      setMigrated(true);
      loadStats();
    } catch {
      setMigrateError(true);
    } finally {
      setMigrating(false);
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

  const statCell = (label: string, value: string | number) => (
    <div className="rounded-2xl border border-line bg-panel px-4 py-3 text-center">
      <div className="text-xl font-extrabold text-ink">{value}</div>
      <div className="mt-0.5 text-xs text-mute">{label}</div>
    </div>
  );

  return (
    <div>
      <h1 className="mb-4 text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("nav.lists")}
      </h1>

      {/* FR-17: банер-пропозиція перенести гостьові списки (одноразово) */}
      {ready && user && guestCount != null && guestCount > 0 && (
        <div className="mb-4 flex flex-wrap items-center gap-3 rounded-2xl border border-accent/30 bg-accent/10 px-4 py-3">
          <span className="min-w-0 flex-1 text-sm text-ink">
            {t("migrate.banner", { n: guestCount })}
          </span>
          <button
            type="button"
            onClick={() => void doMigrate()}
            disabled={migrating}
            className="rounded-xl bg-accent px-4 py-2 text-sm font-bold text-white transition-colors enabled:hover:bg-accent-2 disabled:opacity-60"
          >
            {migrating ? t("common.loading") : t("migrate.button")}
          </button>
        </div>
      )}
      {migrated && (
        <p role="status" className="mb-4 text-sm text-emerald-300">
          {t("migrate.done")}
        </p>
      )}
      {migrateError && (
        <p className="mb-4 text-sm text-amber-300">{t("migrate.error")}</p>
      )}

      {/* FR-17: статистика */}
      {stats && (
        <section className="mb-4">
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            {statCell(t("lists.stats.watched"), stats.watchedCount ?? 0)}
            {statCell(t("lists.stats.liked"), stats.likedCount ?? 0)}
            {statCell(t("lists.stats.disliked"), stats.dislikedCount ?? 0)}
            {statCell(
              t("lists.stats.avgRating"),
              stats.avgRating != null ? String(stats.avgRating) : "—",
            )}
          </div>
          {!!stats.topGenres?.length && (
            <div className="mt-2 flex flex-wrap items-center gap-1.5 text-xs text-mute">
              <span className="font-semibold">{t("lists.stats.topGenres")}:</span>
              {stats.topGenres.slice(0, 5).map((g, i) => {
                const label = typeof g === "string" ? g : (g.genre ?? "");
                return (
                  <span
                    key={`${label}-${i}`}
                    className="rounded-full border border-line bg-panel px-2.5 py-0.5 text-ink/80"
                  >
                    {label}
                  </span>
                );
              })}
            </div>
          )}
          {statsUnavailable && <div className="mt-2"><ApiBanner /></div>}
        </section>
      )}

      <div role="tablist" className="mb-4 flex gap-2">
        {TABS.map((k) =>
          tabBtn(
            k,
            t(
              k === "disliked"
                ? "lists.stats.disliked"
                : k === "liked"
                  ? "lists.liked"
                  : `lists.${k}`,
            ),
          ),
        )}
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
                  {/* FR-17: оцінка 1–10 зірочками на watched/read */}
                  {RATEABLE.includes(kind) && (
                    <div
                      className="flex min-w-0 flex-1 flex-wrap items-center"
                      role="group"
                      aria-label={t("lists.rating")}
                    >
                      {Array.from({ length: 10 }, (_, i) => i + 1).map((n) => (
                        <button
                          key={n}
                          type="button"
                          onClick={() => void rate(item, n)}
                          aria-label={`${t("lists.rating")}: ${n}`}
                          className={`px-0 text-sm leading-none transition-transform hover:scale-125 ${
                            (item.rating ?? 0) >= n
                              ? "text-amber-300"
                              : "text-line/80 hover:text-amber-300/60"
                          }`}
                        >
                          ★
                        </button>
                      ))}
                    </div>
                  )}
                  {RATEABLE.includes(kind) && item.rating != null && (
                    <span className="rounded-md border border-amber-400/30 bg-amber-400/10 px-1.5 py-0.5 text-[11px] font-bold text-amber-300">
                      ★ {item.rating}
                    </span>
                  )}
                  <button
                    type="button"
                    onClick={() => void remove(item)}
                    aria-label={t("lists.remove")}
                    title={t("lists.remove")}
                    className={`rounded-lg border border-line bg-panel px-2 py-1 text-xs text-mute transition-colors hover:border-red-500/50 hover:text-red-400 ${
                      RATEABLE.includes(kind) ? "" : "ml-auto"
                    }`}
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
