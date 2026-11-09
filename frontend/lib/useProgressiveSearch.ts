"use client";

// Прогресивне завантаження списків (запит власника 09.11): фільтри застосовуються
// одразу, перша сторінка показується негайно, решта «підгружається з часом» сама
// (таймер-дозавантаження перших AUTO_PAGES сторінок — приємний вигляд без скролу),
// далі — по мірі скролу (IntersectionObserver) і кнопкою «Показати ще» як
// доступний фолбек. Поки йде новий запит — попередні результати лишаються
// видимими (затемнення), без спустошення екрана: stale-while-revalidate.

import { useCallback, useEffect, useRef, useState } from "react";
import { isUnavailable } from "./fallback";
import type { Paged } from "./types";

export interface ProgressiveList<T> {
  items: T[];
  total: number | null;
  totalCapped: boolean; // total обрізаний лімітом підрахунку (текстовий пошук 2000+)
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
  const [totalCapped, setTotalCapped] = useState(false);
  const [busy, setBusy] = useState(false);
  const [firstLoad, setFirstLoad] = useState(true);
  const [error, setError] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [page, setPage] = useState(1);

  const reqRef = useRef(0);
  const keyRef = useRef(resetKey);
  const busyRef = useRef(false);
  // Автодозавантаження: скільки сторінок долити таймером після завершення
  // поточної (потім — лише скрол/кнопка, щоб не тягнути весь каталог у DOM).
  const autoRef = useRef(AUTO_PAGES);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Актуальні total/items для рішень поза рендером (клозура useEffect відстає).
  const totalRef = useRef<number | null>(null);
  const itemsRef = useRef<T[]>([]);
  totalRef.current = total;
  itemsRef.current = items;

  // Unmount: прибрати відкладений таймер дозавантаження.
  useEffect(() => () => clearTimer(), []);

  const clearTimer = () => {
    if (timerRef.current !== null) {
      clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  };

  // Зміна фільтрів → порожній список, сторінка 1; in-flight відповіді попереднього
  // ключа скасовуються лічильником; таймер старого ключа теж скасовується.
  useEffect(() => {
    if (keyRef.current === resetKey) return;
    keyRef.current = resetKey;
    reqRef.current++;
    autoRef.current = AUTO_PAGES;
    clearTimer();
    setItems([]);
    setTotal(null);
    setTotalCapped(false);
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
        // refs оновлюємо ДО re-render: .finally читає їх одразу після .then
        // (мікротаск), коли React ще не встиг перерендерити.
        const merged =
          page === 1
            ? r.items
            : (() => {
                const seen = new Set(itemsRef.current.map((x) => (x as { id: number }).id));
                return [...itemsRef.current, ...r.items.filter((x) => !seen.has((x as { id: number }).id))];
              })();
        itemsRef.current = merged;
        totalRef.current = r.total;
        setItems(merged);
        setTotal(r.total);
        setTotalCapped(r.totalCapped === true);
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
        // «Остальне підгружалось з часом»: після завершення сторінки — плануємо
        // наступну через паузу, поки не вичерпаємо авто-ліміт сторінок. Скасується
        // зміною ключа фільтрів, unmount або повним списком.
        if (autoRef.current > 0 && totalRef.current != null && itemsRef.current.length < totalRef.current) {
          autoRef.current -= 1;
          clearTimer();
          timerRef.current = setTimeout(() => {
            timerRef.current = null;
            if (keyRef.current === resetKey) setPage((p) => p + 1);
          }, AUTO_DELAY_MS);
        }
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

  return { items, total, totalCapped, busy, firstLoad, error, unavailable, sentinelRef, canMore, showMore };
}

// Автодозавантаження «решти сторінок з часом»: 8 сторінок (≈192 картки) доливаються
// самі по ~1.1с; далі — за скролом/кнопкою. Ліміт — щоб не тягнути весь каталог у DOM.
const AUTO_PAGES = 8;
const AUTO_DELAY_MS = 1100;
