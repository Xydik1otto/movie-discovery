"use client";

// UX-01 (заміна useProgressiveSearch, рішення власника 10.11): серверна пагінація.
// Одна сторінка = один запит (page + pageSize=20); наступна сторінка ЗАМІНЮЄ
// поточну — без append, автодозавантажень і прихованого накопичення каталогу.
//
// • total рахується на page=1 і кешується per resetKey; сторінки >1 летять з
//   omitTotal=1 — бекенд пропускає дорогий COUNT і повертає total=-1 (невідомо),
//   хук підставляє кешоване значення. Порожній кеш → перший запит завжди з count.
// • Stale-guard: лічильник запитів + AbortController — застаріла повільна
//   відповідь не перезаписує свіжу (guard), а новий запит скасовує попередній
//   (abort; викликач може прокинути signal у fetch — api-шар наразі таймаутить
//   сам, тому скасування гарантує щонайменше відкид результату).
// • Depth-cap синхронний із бекендом (SearchService.MaxPages): глибші сторінки
//   не пропонуються, кліпнута відповідь бекенда (page < запитаної) приймається.
// • Зміна resetKey (фільтри/сортування/запит/мова) → скидає page=1, гасить
//   in-flight запит і НЕ дає йому вилетіти зі старою сторінкою (pendingReset).

import { useCallback, useEffect, useRef, useState } from "react";
import { isUnavailable } from "./fallback";
import { warmPosterImages } from "./posterWarmup";
import type { Paged } from "./types";

/** Розмір сторінки каталогу (UX-01: 20) — єдине джерело для всіх споживачів. */
export const PAGE_SIZE = 20;

/** Жорстка стеля глибини пагінації — синхронна з SearchService.MaxPages. */
export const MAX_PAGES = 5000;

/** TTL пам'ятного кешу префетчених сторінок (hover «Наступна») — анти-стейл. */
const PREFETCH_TTL_MS = 60_000;
/** Стеля пам'ятного кешу префетчу (записів по всіх resetKey разом). */
const PREFETCH_CACHE_MAX = 24;

export interface FetchPageArgs {
  page: number;
  /** true, коли в кешу є total і запитувана сторінка > 1 — COUNT можна пропустити. */
  omitTotal: boolean;
  signal: AbortSignal;
}

export interface PagedList<T> {
  items: T[];
  page: number;
  pageSize: number;
  /** null = ще не відомий ( перший запрос триває ); -1 від бекенда підмінюється кешем. */
  total: number | null;
  /** total обрізаний лімітом підрахунку бекенда («2000+») — показувати як ~N. */
  totalCapped: boolean;
  totalPages: number | null;
  busy: boolean;
  /** Скелетон: ще нічого не показано для поточного resetKey. */
  firstLoad: boolean;
  error: boolean;
  unavailable: boolean;
  canPrev: boolean;
  canNext: boolean;
  goTo: (p: number) => void;
  /** Повторити поточну сторінку після помилки. */
  retry: () => void;
  /** UX-медіа: точковий мердж items (постери батч-бекфілу) — той самий setItems,
   *  що й у пагінації; без рефетчу і без запису в SWR-кеш localStorage. */
  mergeItems: (updater: (prev: T[]) => T[]) => void;
  /** UX-медіа: тихо префетчити сторінку (hover «Наступна») — пам'ятний кеш +
   *  прогрівання постерів; перехід на цю сторінку тоді віддає результат з кешу. */
  prefetchPage: (page: number) => void;
}

interface CachedTotal {
  total: number;
  capped: boolean;
}

interface CachedPage<T> {
  entry: Paged<T>;
  at: number;
}

function pruneTotalCache(cache: Map<string, CachedTotal>) {
  while (cache.size > 48) {
    const oldest = cache.keys().next().value;
    if (oldest === undefined) break;
    cache.delete(oldest);
  }
}

function prunePageCache<T>(cache: Map<string, Map<number, CachedPage<T>>>) {
  let total = 0;
  for (const m of cache.values()) total += m.size;
  while (total > PREFETCH_CACHE_MAX) {
    const oldestKey = cache.keys().next().value;
    if (oldestKey === undefined) break;
    const m = cache.get(oldestKey);
    if (!m) {
      cache.delete(oldestKey);
      continue;
    }
    const oldestPage = m.keys().next().value;
    if (oldestPage === undefined) {
      cache.delete(oldestKey);
      continue;
    }
    m.delete(oldestPage);
    if (m.size === 0) cache.delete(oldestKey);
    total--;
  }
}

export function usePagedSearch<T>(
  fetchPage: (args: FetchPageArgs) => Promise<Paged<T>>,
  resetKey: string,
  enabled = true,
  initialPage = 1,
  // SWR-кеш дефолтного вигляду (10.11): якщо передано непорожні initialItems —
  // каталог малюється ОДРАЗУ (firstLoad=false), а перший fetch усе одно виконується
  // як рефреш: під час нього дані НЕ ховаються (скелетон лише коли items порожні,
  // сітка з busy лише притлумлюється). Кеш прострочений/порожній → null → скелетон.
  initialItems: T[] | null = null,
  initialTotal: number | null = null,
  initialTotalCapped = false,
): PagedList<T> {
  const hasInitial = initialItems != null && initialItems.length > 0;
  const [items, setItems] = useState<T[]>(() =>
    initialItems != null && initialItems.length > 0 ? initialItems : []);
  const [page, setPage] = useState(() => Math.max(1, Math.floor(initialPage) || 1));
  const [total, setTotal] = useState<number | null>(() =>
    hasInitial && initialTotal != null && initialTotal >= 0 ? initialTotal : null);
  const [totalCapped, setTotalCapped] = useState<boolean>(() => (hasInitial ? initialTotalCapped : false));
  const [busy, setBusy] = useState(false);
  const [firstLoad, setFirstLoad] = useState(() => !hasInitial);
  const [error, setError] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [nonce, setNonce] = useState(0); // retry-лічильник у deps fetch-ефекту

  const reqRef = useRef(0);
  const acRef = useRef<AbortController | null>(null);
  const keyRef = useRef(resetKey);
  // UX-медіа: пам'ятний кеш префетчених сторінок (per resetKey → page → entry)
  // і множина сторінок, що вже префетчатся (≤1 prefetch на resetKey... на сторінку).
  const pageCacheRef = useRef(new Map<string, Map<number, CachedPage<T>>>());
  const prefetchInflightRef = useRef(new Map<string, Set<number>>());
  const fetchRef = useRef(fetchPage);
  const pageRef = useRef(page);
  fetchRef.current = fetchPage;
  pageRef.current = page;
  // «Щойно змінилися фільтри» — наступний прогін fetch-ефекту пропускаємо,
  // щоб сторінка зі СТАРИМ номером не полетіла з новими фільтрами (баг хвилі-1).
  const pendingResetRef = useRef(false);
  // total-кеш per resetKey: перегортання не перераховують count.
  const totalCacheRef = useRef(new Map<string, CachedTotal>());

  // Зміна фільтрів/сортування/запиту/мови → сторінка 1, порожньо, скелетон;
  // in-flight запит попереднього ключа скасовуємо. Кеш префетчу теж чистимо —
  // він живе лише в межах одного resetKey.
  useEffect(() => {
    if (keyRef.current === resetKey) return;
    keyRef.current = resetKey;
    reqRef.current++;
    acRef.current?.abort();
    pendingResetRef.current = true;
    setItems([]);
    setTotal(null);
    setTotalCapped(false);
    setPage(1);
    setFirstLoad(true);
    setError(false);
    pageCacheRef.current.clear();
    prefetchInflightRef.current.clear();
  }, [resetKey]);

  useEffect(() => {
    if (!enabled) return;
    // reset-ефект ще не відпрацював для нового ключа — чекаємо наступний рендер.
    if (keyRef.current !== resetKey) return;
    // Цей прогін — одразу після скидання фільтрів: сторінка зі СТАРИМ номером
    // не має летіти з новими фільтрами (баг хвилі-1). Але коли page вже 1 —
    // fetch відбувається тут же (іншого прогону не буде: setPage(1) дає bail-out).
    if (pendingResetRef.current) {
      pendingResetRef.current = false;
      if (page !== 1) return;
    }
    const myReq = ++reqRef.current;
    acRef.current?.abort();
    const ac = new AbortController();
    acRef.current = ac;

    const cached = totalCacheRef.current.get(resetKey) ?? null;
    // omitTotal лише коли кеш гарячий і це не перша сторінка (перший запит —
    // завжди з count: він живить кеш і навігацію після reload на глибокій сторінці).
    const omitTotal = page > 1 && cached != null;

    // Спільне застосування відповіді (реальний fetch або кеш префетчу).
    const applyPaged = (r: Paged<T>, requestedPage: number) => {
      // Бекенд може кліпнути сторінку (depth-cap) — приймаємо його page.
      if (r.page >= 1 && r.page !== requestedPage) setPage(r.page);
      if (r.total >= 0) {
        const entry = { total: r.total, capped: r.totalCapped === true };
        totalCacheRef.current.set(resetKey, entry);
        pruneTotalCache(totalCacheRef.current);
        setTotal(r.total);
        setTotalCapped(entry.capped);
      } else if (cached) {
        // omitTotal-відповідь: total беремо з кешу цього resetKey.
        setTotal(cached.total);
        setTotalCapped(cached.capped);
      } else {
        setTotal(null);
        setTotalCapped(false);
      }
      setItems(r.items);
      setUnavailable(false);
      setFirstLoad(false);
    };

    // UX-медіа (hover-префетч «Наступна»): сторінка вже в пам'ятному кеші —
    // миттєвий рендер без запиту (TTL захищає від застарілого результату).
    const cachedPage = pageCacheRef.current.get(resetKey)?.get(page);
    if (cachedPage && Date.now() - cachedPage.at < PREFETCH_TTL_MS) {
      setBusy(false);
      setError(false);
      applyPaged(cachedPage.entry, page);
      return () => {
        ac.abort();
      };
    }

    setBusy(true);
    setError(false);
    fetchPage({ page, omitTotal, signal: ac.signal })
      .then((r) => {
        if (reqRef.current !== myReq) return;
        applyPaged(r, page);
      })
      .catch((e) => {
        if (reqRef.current !== myReq) return;
        if (isUnavailable(e)) setUnavailable(true);
        else setError(true);
        setFirstLoad(false);
      })
      .finally(() => {
        if (reqRef.current !== myReq) return;
        setBusy(false);
      });

    // Скасування СВІГО запиту при зміні deps/unmount: новий прогін сам
    // інвалідовує попередній через reqRef, тож тут — лише abort signal'а
    // (читання reqRef.current у cleanup — source of lint-попередження).
    return () => {
      ac.abort();
    };
  }, [page, resetKey, enabled, fetchPage, nonce]);

  const totalPages =
    total != null && total >= 0 ? Math.max(1, Math.ceil(total / PAGE_SIZE)) : null;

  const canPrev = page > 1 && !busy;
  // Наступна сторінка: за точним total — поки не вичерпаний; за capped — теж
  // до межі (усі сторінки в межах досяжні, але підпис ~); без total — heuristic
  // «повна сторінка → ймовірно є наступна». Плюс спільна стеля MAX_PAGES.
  const bound = totalPages != null ? Math.min(totalPages, MAX_PAGES) : MAX_PAGES;
  const canNext =
    !busy &&
    page < bound &&
    (totalPages == null ? items.length >= PAGE_SIZE : true);

  const goTo = useCallback((p: number) => {
    const next = Math.floor(p);
    if (!Number.isFinite(next)) return;
    setPage(Math.min(Math.max(1, next), MAX_PAGES));
  }, []);

  const retry = useCallback(() => setNonce((n) => n + 1), []);

  // UX-медіа: точковий мердж items (постери батч-бекфілу usePosterBackfill) —
  // той самий setItems, що й у пагінації. Кеш префетчу поточної сторінки патчимо
  // теж, щоб повторне перевідкриття сторінки не втрачало резолвлені постери.
  const mergeItems = useCallback((updater: (prev: T[]) => T[]) => {
    setItems(updater);
    const cachedPage = pageCacheRef.current.get(keyRef.current)?.get(pageRef.current);
    if (cachedPage) {
      cachedPage.entry = { ...cachedPage.entry, items: updater(cachedPage.entry.items) };
    }
  }, []);

  // UX-медіа: hover-префетч сторінки — тихий виклик наявного пошукового API,
  // результат у пам'ятному кешу (serve на goTo) + прогрівання постерів перших
  // 20 карток (пул ≤6). Помилки мовчки ігноруються: реальний перехід все одно
  // виконає повноцінний запит. ≤1 префетч на сторінку в межах resetKey.
  const prefetchPage = useCallback((p: number) => {
    const target = Math.floor(p);
    if (!Number.isFinite(target) || target < 1 || target > MAX_PAGES) return;
    if (target === pageRef.current) return;
    const key = keyRef.current;
    const hit = pageCacheRef.current.get(key)?.get(target);
    if (hit && Date.now() - hit.at < PREFETCH_TTL_MS) return;
    const load = fetchRef.current;
    if (!load) return;
    let inflight = prefetchInflightRef.current.get(key);
    if (inflight?.has(target)) return;
    if (!inflight) {
      inflight = new Set<number>();
      prefetchInflightRef.current.set(key, inflight);
    }
    inflight.add(target);
    // Перегортання без COUNT: тотал уже відомий з попередніх сторінок (кеш).
    const cachedTotal = totalCacheRef.current.get(key) ?? null;
    const omitTotal = target > 1 && cachedTotal != null;
    load({ page: target, omitTotal, signal: new AbortController().signal })
      .then((r) => {
        const still = prefetchInflightRef.current.get(key);
        if (!still || !still.has(target)) return; // resetKey змінився — застаріла
        let perKey = pageCacheRef.current.get(key);
        if (!perKey) {
          perKey = new Map();
          pageCacheRef.current.set(key, perKey);
        }
        // Бекенд може кліпнути сторінку (depth-cap) — кешуємо під його page.
        perKey.set(r.page >= 1 ? r.page : target, { entry: r, at: Date.now() });
        prunePageCache(pageCacheRef.current);
        // Паралельне прогрівання зображень по posterUrl перших 20 (дедуп усередині).
        warmPosterImages(r.items);
      })
      .catch(() => {
        // Тихо: префетч — оптимізація, помилку показувати не можна (немає UI-слота).
      })
      .finally(() => {
        const still = prefetchInflightRef.current.get(key);
        if (still) {
          still.delete(target);
          if (still.size === 0) prefetchInflightRef.current.delete(key);
        }
      });
  }, []);

  return {
    items,
    page,
    pageSize: PAGE_SIZE,
    total,
    totalCapped,
    totalPages,
    busy,
    firstLoad,
    error,
    unavailable,
    canPrev,
    canNext,
    goTo,
    retry,
    mergeItems,
    prefetchPage,
  };
}
