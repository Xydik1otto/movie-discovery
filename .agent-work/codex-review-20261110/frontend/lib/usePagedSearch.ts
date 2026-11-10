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
import type { Paged } from "./types";

/** Розмір сторінки каталогу (UX-01: 20) — єдине джерело для всіх споживачів. */
export const PAGE_SIZE = 20;

/** Жорстка стеля глибини пагінації — синхронна з SearchService.MaxPages. */
export const MAX_PAGES = 5000;

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
}

interface CachedTotal {
  total: number;
  capped: boolean;
}

function pruneTotalCache(cache: Map<string, CachedTotal>) {
  while (cache.size > 48) {
    const oldest = cache.keys().next().value;
    if (oldest === undefined) break;
    cache.delete(oldest);
  }
}

export function usePagedSearch<T>(
  fetchPage: (args: FetchPageArgs) => Promise<Paged<T>>,
  resetKey: string,
  enabled = true,
  initialPage = 1,
): PagedList<T> {
  const [items, setItems] = useState<T[]>([]);
  const [page, setPage] = useState(() => Math.max(1, Math.floor(initialPage) || 1));
  const [total, setTotal] = useState<number | null>(null);
  const [totalCapped, setTotalCapped] = useState(false);
  const [busy, setBusy] = useState(false);
  const [firstLoad, setFirstLoad] = useState(true);
  const [error, setError] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [nonce, setNonce] = useState(0); // retry-лічильник у deps fetch-ефекту

  const reqRef = useRef(0);
  const acRef = useRef<AbortController | null>(null);
  const keyRef = useRef(resetKey);
  // «Щойно змінилися фільтри» — наступний прогін fetch-ефекту пропускаємо,
  // щоб сторінка зі СТАРИМ номером не полетіла з новими фільтрами (баг хвилі-1).
  const pendingResetRef = useRef(false);
  // total-кеш per resetKey: перегортання не перераховують count.
  const totalCacheRef = useRef(new Map<string, CachedTotal>());

  // Зміна фільтрів/сортування/запиту/мови → сторінка 1, порожньо, скелетон;
  // in-flight запит попереднього ключа скасовуємо.
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

    setBusy(true);
    setError(false);
    fetchPage({ page, omitTotal, signal: ac.signal })
      .then((r) => {
        if (reqRef.current !== myReq) return;
        // Бекенд може кліпнути сторінку (depth-cap) — приймаємо його page.
        if (r.page >= 1 && r.page !== page) setPage(r.page);
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
  };
}
