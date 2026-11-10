"use client";

// FR-18: «Кінотека» — свайп-стек. Драг мишею і тачем (pointer events),
// кнопки ❤️/👎/⏭/➕, нескінченний стек (добираємо колоду, коли лишається <3).
// Anti-fatigue паузу скасовано власником: дизлайк просто враховується у
// профілі на бекенді; бейдж mode — деликатний підпис.

import { useCallback, useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { useI18n } from "@/i18n/I18nContext";
import { useAuth } from "@/lib/auth";
import {
  ApiError,
  guardedDiscover,
  postDiscoverAction,
} from "@/lib/fallback";
import type { DiscoverAction, DiscoverMode, TitleCard } from "@/lib/types";
import SwipeCard from "@/components/SwipeCard";
import ApiBanner from "@/components/ApiBanner";
import EmptyState from "@/components/EmptyState";
import LoadingSkeleton from "@/components/LoadingSkeleton";

const BATCH = 10;
const REFILL_AT = 3;
const SWIPE_THRESHOLD = 110;
// UX-01 (віконний трім): пройдений префікс колоди випадає, щойно накопичиться
// TRIM_AFTER пройдених — масив cards (і пам'ять) не росте з довжиною сесії;
// у рендері й так живуть лише активна картка + 2 тильки.
const TRIM_AFTER = 6;

export default function DiscoverPage() {
  const { t, lang } = useI18n();
  const { user, ready, apiUp } = useAuth();
  const router = useRouter();

  const [cards, setCards] = useState<TitleCard[]>([]);
  const [index, setIndex] = useState(0);
  const [mode, setMode] = useState<DiscoverMode | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState(false);
  const [actionError, setActionError] = useState(false);

  // Drag-стейт (одна активна картка = cards[index]).
  const [drag, setDrag] = useState<{ x: number; y: number; active: boolean }>({
    x: 0,
    y: 0,
    active: false,
  });
  // Анімація злету: напрям + ид картки, що летить.
  const [exit, setExit] = useState<{ id: number; dir: "like" | "dislike" | "up" } | null>(
    null,
  );
  const dragStart = useRef<{ x: number; y: number } | null>(null);
  const fetchingMore = useRef(false);
  // Дзеркало index для колбеку setCards (клозура fetchBatch відстає від стану).
  const indexRef = useRef(0);

  const seenIds = useRef<Set<number>>(new Set());

  const fetchBatch = useCallback(
    async (first: boolean) => {
      if (first) setLoading(true);
      else setLoadingMore(true);
      try {
        const r = await guardedDiscover(lang, [...seenIds.current], BATCH);
        const fresh = (r.data.items ?? []).filter((c) => !seenIds.current.has(c.id));
        for (const c of fresh) seenIds.current.add(c.id);
        setCards((prev) => (first ? fresh : [...prev, ...fresh]));
        setMode(r.data.mode ?? null);
        setUnavailable(r.unavailable);
        setError(false);
        if (first) {
          setIndex(0);
          indexRef.current = 0;
        }
      } catch (e) {
        // Cookie протух → на вхід, далі повернемось на /discover.
        if (e instanceof ApiError && e.status === 401) {
          router.replace("/login?needLogin=1&next=/discover");
          return;
        }
        if (first) setError(true);
      } finally {
        if (first) setLoading(false);
        else setLoadingMore(false);
      }
    },
    [lang, router],
  );

  // Анонім на живому API → редирект на /login з повідомленням. Якщо API
  // недоступний (apiUp=false) — не redirect'имо: демо-колода + банер.
  useEffect(() => {
    if (ready && apiUp && !user) {
      router.replace("/login?needLogin=1&next=/discover");
    }
  }, [ready, apiUp, user, router]);

  useEffect(() => {
    // Чекаємо auth-стейт: на живому API аноніма одразу веде на /login.
    if (!ready) return;
    if (apiUp && !user) return;
    void fetchBatch(true);
  }, [ready, apiUp, user, fetchBatch]);

  // Нескінченний стек: добираємо, коли до кінця колоди лишилось <REFILL_AT.
  useEffect(() => {
    if (loading || loadingMore || error) return;
    const remaining = cards.length - index;
    if (remaining < REFILL_AT && !fetchingMore.current) {
      fetchingMore.current = true;
      void fetchBatch(false).finally(() => {
        fetchingMore.current = false;
      });
    }
  }, [cards.length, index, loading, loadingMore, error, fetchBatch]);

  // Віконний трім (UX-01): після кількох злетів зрізаємо пройдений префікс колоди.
  // Зсув узгоджений і для cards (slice(index)), і для index (→0): cards[0] = та сама
  // «поточна» картка, ключі (id) не змінюються — DOM не перемонтується.
  useEffect(() => {
    if (index < TRIM_AFTER) return;
    setCards((prev) => prev.slice(index));
    setIndex(0);
    indexRef.current = 0;
  }, [index]);

  const current = cards[index];
  const behind = cards.slice(index + 1, index + 3); // 2 картки під верхньою

  const advance = useCallback(() => {
    setIndex((i) => {
      indexRef.current = i + 1;
      return i + 1;
    });
    setExit(null);
    setDrag({ x: 0, y: 0, active: false });
  }, []);

  const act = useCallback(
    (action: DiscoverAction, dir: "like" | "dislike" | "up" | null) => {
      if (!current) return;
      setActionError(false);
      if (dir) setExit({ id: current.id, dir });
      // Демо-режим — лише локальний перехід, POST не смикаємо.
      if (!unavailable) {
        postDiscoverAction(current.id, action).catch(() => {
          // Збій одиночної дії не ламає стек — покажемо ноту.
          setActionError(true);
        });
      }
      window.setTimeout(advance, dir ? 260 : 0);
    },
    [current, unavailable, advance],
  );

  function onPointerDown(e: React.PointerEvent) {
    if (exit) return;
    dragStart.current = { x: e.clientX, y: e.clientY };
    setDrag({ x: 0, y: 0, active: true });
    (e.target as HTMLElement).setPointerCapture?.(e.pointerId);
  }

  function onPointerMove(e: React.PointerEvent) {
    if (!dragStart.current) return;
    setDrag({
      x: e.clientX - dragStart.current.x,
      y: e.clientY - dragStart.current.y,
      active: true,
    });
  }

  function onPointerUp() {
    if (!dragStart.current) return;
    const { x, y } = drag;
    dragStart.current = null;
    if (x >= SWIPE_THRESHOLD) act("like", "like");
    else if (x <= -SWIPE_THRESHOLD) act("dislike", "dislike");
    else if (y <= -SWIPE_THRESHOLD * 1.2) act("watchlist", "up");
    else setDrag({ x: 0, y: 0, active: false }); // пружинка назад
  }

  if (!ready) {
    return <LoadingSkeleton count={1} />;
  }

  // Анонім на живому API — редирект уже запущено.
  if (apiUp && !user) {
    return <LoadingSkeleton count={1} />;
  }

  const topTransform = exit
    ? exit.dir === "like"
      ? "translate(150%, -40px) rotate(24deg)"
      : exit.dir === "dislike"
        ? "translate(-150%, -40px) rotate(-24deg)"
        : "translate(0, -130%) rotate(6deg)"
    : drag.active
      ? `translate(${drag.x}px, ${drag.y}px) rotate(${drag.x / 22}deg)`
      : "translate(0px, 0px) rotate(0deg)";

  const overlay: "like" | "dislike" | null = drag.active
    ? drag.x > 40
      ? "like"
      : drag.x < -40
        ? "dislike"
        : null
    : null;

  const modeLabel = mode
    ? t(`discover.mode.${mode}`)
    : null;

  return (
    <div>
      <div className="mb-4 flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <h1 className="text-2xl font-extrabold tracking-tight sm:text-3xl">
            {t("discover.title")}
          </h1>
          <p className="mt-1 text-sm text-mute">{t("discover.subtitle")}</p>
        </div>
        {/* Деликатний бейдж режиму видачі */}
        {modeLabel && (
          <span className="rounded-full border border-line bg-panel px-2.5 py-1 text-[11px] text-mute">
            {t("discover.modeLabel")}: {modeLabel}
          </span>
        )}
      </div>

      {unavailable && <ApiBanner />}
      {actionError && (
        <p className="mb-3 text-sm text-amber-300">{t("common.error")}</p>
      )}

      {loading ? (
        <div className="mx-auto max-w-sm">
          <LoadingSkeleton count={1} />
        </div>
      ) : error ? (
        <EmptyState icon="⚠️" title={t("common.error")} />
      ) : !current ? (
        <div>
          <EmptyState icon="🎬" title={t("discover.empty")} />
          <div className="mt-4 text-center">
            <button
              type="button"
              onClick={() => {
                seenIds.current.clear();
                void fetchBatch(true);
              }}
              className="rounded-xl bg-accent px-5 py-2.5 text-sm font-bold text-white transition-colors hover:bg-accent-2"
            >
              {t("discover.again")}
            </button>
          </div>
        </div>
      ) : (
        <div className="mx-auto max-w-sm select-none">
          {/* Стек: під верхньою карткою — 2 тильки */}
          <div
            className="relative h-[460px] touch-none"
            onPointerDown={onPointerDown}
            onPointerMove={onPointerMove}
            onPointerUp={onPointerUp}
            onPointerCancel={onPointerUp}
          >
            {behind
              .map((c, i) => ({ c, i }))
              .reverse()
              .map(({ c, i }) => (
                <div
                  key={c.id}
                  style={{
                    transform: `translateY(${(i + 1) * 10}px) scale(${1 - (i + 1) * 0.04})`,
                    opacity: 1 - (i + 1) * 0.25,
                  }}
                  className="absolute inset-x-0 bottom-0 top-0 mx-auto max-w-sm"
                >
                  <SwipeCard item={c} transform="" transition onOpen={() => {}} />
                </div>
              ))}

            {/* Картка, що злітає: анімація триває до advance, потім exit скидається */}

            <SwipeCard
              key={current.id}
              item={current}
              transform={topTransform}
              transition={!drag.active}
              overlay={overlay}
              onOpen={() => router.push(`/title/${current.id}`)}
            />
          </div>

          {/* Кнопки дій */}
          <div className="mt-5 flex items-center justify-center gap-3">
            <button
              type="button"
              onClick={() => act("dislike", "dislike")}
              aria-label={t("discover.dislike")}
              title={t("discover.dislike")}
              className="flex h-14 w-14 items-center justify-center rounded-full border border-line bg-panel text-2xl transition-all hover:border-red-500/60 hover:bg-red-500/10 active:scale-90"
            >
              👎
            </button>
            <button
              type="button"
              onClick={() => act("skip", null)}
              aria-label={t("discover.skip")}
              title={t("discover.skip")}
              className="flex h-11 w-11 items-center justify-center rounded-full border border-line bg-panel text-base text-mute transition-all hover:text-ink active:scale-90"
            >
              ⏭
            </button>
            <button
              type="button"
              onClick={() => act("like", "like")}
              aria-label={t("discover.like")}
              title={t("discover.like")}
              className="flex h-16 w-16 items-center justify-center rounded-full bg-accent text-3xl text-white shadow-lg shadow-accent/25 transition-all hover:bg-accent-2 active:scale-90"
            >
              ❤️
            </button>
            <button
              type="button"
              onClick={() => act("watchlist", "up")}
              aria-label={t("discover.watchlist")}
              title={t("discover.watchlist")}
              className="flex h-11 w-11 items-center justify-center rounded-full border border-line bg-panel text-base text-mute transition-all hover:border-sky-500/60 hover:text-sky-300 active:scale-90"
            >
              ➕
            </button>
          </div>

          {loadingMore && (
            <p className="mt-3 text-center text-xs text-mute">{t("common.loading")}</p>
          )}
        </div>
      )}
    </div>
  );
}
