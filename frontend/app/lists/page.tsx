"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { useI18n } from "@/i18n/I18nContext";
import { useAuth } from "@/lib/auth";
import {
  deleteListItem,
  guardedLists,
  guardedListStats,
  isUnavailable,
  migrateGuestLists,
  rateListItem,
} from "@/lib/fallback";
import { rateListEntry } from "@/lib/listActions";
import {
  clearGuestSnapshot,
  guestSnapshot,
  hasMigrationMarker,
  loadGuestLists,
  removeGuestItem,
  setMigrationMarker,
  storageAvailable,
  type GuestEntry,
} from "@/lib/guestLists";
import type { ListKind, ListItem, ListStats } from "@/lib/types";
import TitleCard from "@/components/TitleCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";

const TABS: ListKind[] = ["watchlist", "liked", "watched", "read", "disliked"];
const RATEABLE: ListKind[] = ["watched", "read"];

// Гостьовий запис → форма ListItem для рендера картки (без серверних запитів).
function guestToItem(e: GuestEntry): ListItem {
  return {
    id: e.titleId,
    tmdbId: e.tmdbId ?? null,
    type: e.meta?.type ?? "movie",
    title: e.meta?.title ?? "",
    originalTitle: null,
    year: e.meta?.year ?? null,
    posterUrl: e.meta?.posterUrl ?? null,
    rating: e.rating ?? null,
    createdAt: e.addedAt,
  };
}

export default function ListsPage() {
  const { t, lang } = useI18n();
  const { user, ready } = useAuth();
  const isGuest = ready && !user;
  const [kind, setKind] = useState<ListKind>("watchlist");
  const [items, setItems] = useState<ListItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);
  const [actionError, setActionError] = useState(false);
  // MD-04: чесний стан гостьового сховища (приватний режим браузера).
  const [noStorage, setNoStorage] = useState(false);
  // FR-17: статистика списків (лічильники + середня оцінка + топ жанрів).
  const [stats, setStats] = useState<ListStats | null>(null);
  const [statsUnavailable, setStatsUnavailable] = useState(false);
  // FR-17/MD-04: міграція гостьових списків в акаунт.
  const [guestCount, setGuestCount] = useState<number | null>(null);
  const [migrating, setMigrating] = useState(false);
  const [migrated, setMigrated] = useState(false);
  const [migrateResult, setMigrateResult] = useState<{ moved: number; skipped: number } | null>(
    null,
  );
  const [migrateError, setMigrateError] = useState(false);
  // Перезавантаження серверних списків після міграції.
  const [reload, setReload] = useState(0);

  // Гостьові записи (усі kind) — для вкладок і локальної статистики.
  const [guestEntries, setGuestEntries] = useState<GuestEntry[]>([]);

  useEffect(() => {
    if (!ready) return;
    if (!user) {
      // MD-04: гість читає списки з localStorage — жодного серверного виклику
      // (ні /api/lists, ні /api/lists/stats; старий шлях із profileId=1 знято).
      setNoStorage(!storageAvailable());
      setGuestEntries(loadGuestLists());
      setUnavailable(false);
      setError(false);
      setLoading(false);
      return;
    }
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
  }, [ready, user, kind, lang, reload]);

  const loadStats = useCallback(() => {
    guardedListStats(lang)
      .then((r) => {
        setStats(r.data);
        setStatsUnavailable(r.unavailable);
      })
      .catch(() => setStatsUnavailable(true));
  }, [lang]);

  useEffect(() => {
    if (!ready) return;
    if (!user) return; // гість — статистика рахується локально
    loadStats();
  }, [ready, user, loadStats, reload]);

  // Гість: локальні лічильники (чесно, без серверних агрегатів).
  const guestStats = useMemo(() => {
    const rated = guestEntries.filter((e) => e.rating != null);
    const avg = rated.length
      ? Number((rated.reduce((s, e) => s + (e.rating ?? 0), 0) / rated.length).toFixed(1))
      : null;
    return {
      watchedCount: guestEntries.filter((e) => e.kind === "watched").length,
      likedCount: guestEntries.filter((e) => e.kind === "liked").length,
      dislikedCount: guestEntries.filter((e) => e.kind === "disliked").length,
      avgRating: avg,
    };
  }, [guestEntries]);

  const guestItems = useMemo(
    () => guestEntries.filter((e) => e.kind === kind).map(guestToItem),
    [guestEntries, kind],
  );

  // Банер міграції (MD-04): читаємо ЛИШЕ ЛОКАЛЬНИЙ гостьовий знімок (ніколи не
  // серверні списки). Маркер — `md_guest_migrated:{userId}:{hash}`: інший
  // акаунт чужий маркер не успадковує, нові гостьові дії пропонують міграцію знову.
  useEffect(() => {
    if (!ready || !user || user.id == null) {
      setGuestCount(null);
      return;
    }
    const snap = guestSnapshot();
    if (!snap.length || hasMigrationMarker(user.id, snap)) {
      setGuestCount(null);
      return;
    }
    setGuestCount(snap.length);
  }, [ready, user, reload]);

  function itemIdOf(item: ListItem): number {
    return item.itemId ?? item.id;
  }

  async function remove(item: ListItem) {
    setActionError(false);
    if (isGuest) {
      // Гість: видаляємо у localStorage, оптимістично оновлюємо локальний стан.
      removeGuestItem(kind, item.id);
      setGuestEntries(loadGuestLists());
      return;
    }
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
    if (isGuest) {
      // MD-04: гість — оцінка в localStorage (ідемпотентний upsert).
      await rateListEntry(false, kind, { id: item.id, tmdbId: item.tmdbId ?? null }, rating);
      setGuestEntries(loadGuestLists());
      return;
    }
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

  // MD-04: міграція ЛИШЕ за дією користувача, знімок — з localStorage.
  // Маркер і очищення знімка — тільки коли moved+skipped покрили весь знімок.
  async function doMigrate() {
    if (!user || user.id == null) return;
    const snap = guestSnapshot();
    if (!snap.length) {
      setGuestCount(null);
      return;
    }
    setMigrating(true);
    setMigrateError(false);
    setMigrateResult(null);
    try {
      const r = await migrateGuestLists(snap);
      const moved = r.moved ?? 0;
      const skipped = r.skipped ?? 0;
      if (moved + skipped === snap.length) {
        // Повне підтверджене перенесення: маркер scoped за акаунтом+знімком,
        // знімок чистимо. Повторна міграція не дублює (бекенд ідемпотентний).
        setMigrationMarker(user.id, snap);
        clearGuestSnapshot();
        setGuestEntries([]);
        setGuestCount(null);
        setMigrated(true);
        setReload((x) => x + 1);
        loadStats();
      } else {
        // Частковий результат: знімок і маркер недоторкані, чесна статистика + retry.
        setMigrateResult({ moved, skipped });
        setMigrateError(true);
      }
    } catch {
      // Збій (мережа/401/500): нічого не видаляємо, кнопка «Перенести» лишається.
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

  const shownItems = user ? items : guestItems;
  const shownStats: ListStats | null = user
    ? stats
    : {
        watchedCount: guestStats.watchedCount,
        likedCount: guestStats.likedCount,
        dislikedCount: guestStats.dislikedCount,
        avgRating: guestStats.avgRating,
      };

  return (
    <div>
      <h1 className="mb-4 text-2xl font-extrabold tracking-tight sm:text-3xl">
        {t("nav.lists")}
      </h1>

      {/* MD-04: чесний підпис гостьового режиму */}
      {isGuest && (
        <p className="mb-4 rounded-2xl border border-line bg-panel px-4 py-2.5 text-sm text-mute">
          {t("lists.guestNotice")}
        </p>
      )}
      {isGuest && noStorage && (
        <p
          role="alert"
          className="mb-4 rounded-2xl border border-amber-500/30 bg-amber-500/10 px-4 py-2.5 text-sm text-amber-300"
        >
          {t("lists.storageUnavailable")}
        </p>
      )}

      {/* FR-17/MD-04: банер-пропозиція перенести гостьові списки (за дією користувача) */}
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
        <div className="mb-4">
          <p className="text-sm text-amber-300">{t("migrate.error")}</p>
          {migrateResult && (
            <p className="mt-1 text-sm text-amber-300">
              {t("migrate.partial", {
                moved: migrateResult.moved,
                skipped: migrateResult.skipped,
              })}
            </p>
          )}
        </div>
      )}

      {/* FR-17: статистика (гостю — локальні лічильники, топ жанрів недоступний) */}
      {shownStats && (
        <section className="mb-4">
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            {statCell(t("lists.stats.watched"), shownStats.watchedCount ?? 0)}
            {statCell(t("lists.stats.liked"), shownStats.likedCount ?? 0)}
            {statCell(t("lists.stats.disliked"), shownStats.dislikedCount ?? 0)}
            {statCell(
              t("lists.stats.avgRating"),
              shownStats.avgRating != null ? String(shownStats.avgRating) : "—",
            )}
          </div>
          {user && !!shownStats.topGenres?.length && (
            <div className="mt-2 flex flex-wrap items-center gap-1.5 text-xs text-mute">
              <span className="font-semibold">{t("lists.stats.topGenres")}:</span>
              {shownStats.topGenres.slice(0, 5).map((g, i) => {
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
          {user && statsUnavailable && <div className="mt-2"><ApiBanner /></div>}
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

      {user && unavailable && <ApiBanner />}
      {actionError && (
        <p className="mb-3 text-sm text-amber-300">{t("common.error")}</p>
      )}

      {loading ? (
        <LoadingSkeleton count={6} />
      ) : error ? (
        <EmptyState icon="⚠️" title={t("common.error")} />
      ) : shownItems.length === 0 ? (
        <EmptyState />
      ) : (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6">
          {shownItems.map((item) => (
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
