"use client";

// Прогресивне завантаження списків (запит власника 09.11): фільтри застосовуються
// одразу, перша сторінка показується негайно, решта догружається по мірі скролу
// (IntersectionObserver + «Показати ще» як доступний фолбек). Поки йде новий
// запит — попередні результати лишаються видимими (затемнення), без спустошення
// екрана: stale-while-revalidate.

import { useCallback, useEffect, useRef, useState } from "react";
import { isUnavailable } from "./fallback";
import type { Paged } from "./types";

export interface ProgressiveList<T> {
  items: T[];
  total: number | null;
  busy: boolean; // будь-яке завантаження в процесі
  firstLoad: boolean; // ще нічого не показано (скелетон)
  error: boolean;
  unavailable: boolean;
  sentinelRef: (el: HTMLDivElement | null) => void;
  canMore: boolean;
  showMore: () => void;
}

export function useProgressiveSearch<T>(
  fetchPage: (page: number) => Promise<Paged<T>>,
  resetKey: string,
  enabled = true,
): ProgressiveList<T> {
  const [items, setItems] = useState<T[]>([]);
  const [total, setTotal] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [firstLoad, setFirstLoad] = useState(true);
  const [error, setError] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [page, setPage] = useState(1);

  const reqRef = useRef(0);
  const keyRef = useRef(resetKey);
  const busyRef = useRef(false);

  // Зміна фільтрів → порожній список, сторінка 1; in-flight відповіді попереднього
  // ключа скасовуються лічильником.
  useEffect(() => {
    if (keyRef.current === resetKey) return;
    keyRef.current = resetKey;
    reqRef.current++;
    setItems([]);
    setTotal(null);
    setPage(1);
    setFirstLoad(true);
    setError(false);
  }, [resetKey]);

  useEffect(() => {
    if (!enabled) return;
    // reset-ефект ще не відпрацював для нового ключа — чекаємо наступний рендер,
    // щоб не витрачати запит на стару сторінку попередніх фільтрів.
    if (keyRef.current !== resetKey) return;
    const myReq = ++reqRef.current;
    setBusy(true);
    busyRef.current = true;
    setError(false);
    fetchPage(page)
      .then((r) => {
        if (reqRef.current !== myReq) return;
        setItems((prev) => {
          if (page === 1) return r.items;
          const seen = new Set(prev.map((x) => (x as { id: number }).id));
          return [...prev, ...r.items.filter((x) => !seen.has((x as { id: number }).id))];
        });
        setTotal(r.total);
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
        busyRef.current = false;
      });
    // fetchPage — стабільний useCallback викликача (залежить від фільтрів/мови)
  }, [page, resetKey, enabled, fetchPage]);

  const canMore = total != null && items.length < total;
  const showMore = useCallback(() => {
    if (busyRef.current || !canMore) return;
    setPage((p) => p + 1);
  }, [canMore]);

  // Sentinel: ref-callback, перевішується при зміні showMore; елемент лишається в DOM.
  const sentinelRef = useCallback(
    (el: HTMLDivElement | null) => {
      if (!el) return;
      const io = new IntersectionObserver(
        (entries) => {
          if (entries.some((e) => e.isIntersecting)) showMore();
        },
        { rootMargin: "700px 0px" },
      );
      io.observe(el);
    },
    [showMore],
  );

  return { items, total, busy, firstLoad, error, unavailable, sentinelRef, canMore, showMore };
}
