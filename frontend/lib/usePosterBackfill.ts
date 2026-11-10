"use client";

// UX-медіа хвиля: паралельна підгрузка постерів під час перегляду каталогу.
// Частина тайтлів приходить у сітку з posterUrl=null (джерела постерів добираються
// фоновими синками бекенда). Хук тихо колектить такі id, батчить (≤24) і ОДНИМ
// POST /api/posters/batch-resolve {titleIds} резолвить URL-и; результат
// повертається через onResolved(map) — сторінка мерджить його тим самим
// state-апдейтом, що й пагінація (жодних рефетчів каталогу, жодних нових текстів).
//
// Захисти від зайвого трафіку й циклів:
//  • module-level Set вже-запитаних id за сесію: misses НЕ повторюються НІКОЛИ
//    (бекенд чесно відповів «постера нема»), resolved теж не перезапитуються;
//    transport-збій (мережа/5xx/429) вивільняє id — ретрай лише при наступному
//    природному тригері (нова сторінка/фільтри/повернення в online), без циклу;
//  • navigator.onLine === false або document.visibilityState !== 'visible' →
//    skip: id лишаються у черзі, повернення в online/visible дозапускає флаш;
//  • дебаунс 300 мс; максимум 1 запит in-flight (решта чекає в черзі).

import { useEffect, useRef } from "react";
import { batchResolvePosters } from "./api";
import type { ResolvedPosterUrls } from "./types";

/** Мапа резолвлених постерів: внутрішній title id → URL-и (для мерджу в стан). */
export type ResolvedPostersMap = Map<number, ResolvedPosterUrls>;

/** Ліміт батча одного запиту (контракт бекенда). */
const BATCH_LIMIT = 24;
/** Дебаунс колекції: доки сітка малює сторінку — id накопичуються. */
const DEBOUNCE_MS = 300;

// Сесійна пам'ять «вже запитаного» (ключ — внутрішній title id), спільна для
// всіх сторінок/сіток: постер тайтла один і той самий, повторних запитів нема.
const requested = new Set<number>();

export function usePosterBackfill(
  items: ReadonlyArray<{ id: number; posterUrl?: string | null }>,
  onResolved: (map: ResolvedPostersMap) => void,
): void {
  const pendingRef = useRef(new Set<number>());
  const timerRef = useRef<number | null>(null);
  const inflightRef = useRef(false);
  const aliveRef = useRef(true);
  // In-flight запит: cleanup (unmount) скасує його через AbortController.
  const abortRef = useRef<AbortController | null>(null);
  // Останні колбек/масив — через ref, ефект перерегистровується лише на items.
  const onResolvedRef = useRef(onResolved);
  onResolvedRef.current = onResolved;
  const itemsRef = useRef(items);
  itemsRef.current = items;

  useEffect(() => {
    aliveRef.current = true; // StrictMode: повторний маунт того самого інстанса

    const canSend = () => {
      if (typeof navigator !== "undefined" && navigator.onLine === false) return false;
      if (typeof document !== "undefined" && document.visibilityState !== "visible") return false;
      return true;
    };

    function collect(list: ReadonlyArray<{ id: number; posterUrl?: string | null }>) {
      for (const it of list) {
        if (!it || it.id == null || it.posterUrl) continue;
        const id = Number(it.id);
        if (!Number.isInteger(id) || requested.has(id) || pendingRef.current.has(id)) continue;
        pendingRef.current.add(id);
      }
    }

    function schedule() {
      if (timerRef.current != null || !aliveRef.current) return;
      timerRef.current = window.setTimeout(flush, DEBOUNCE_MS);
    }

    function flush() {
      timerRef.current = null;
      if (!aliveRef.current || inflightRef.current || !canSend()) return;
      collect(itemsRef.current); // свіжий масив на випадок змін після рендера
      if (pendingRef.current.size === 0) return;
      const batch: number[] = [];
      for (const id of pendingRef.current) {
        batch.push(id);
        requested.add(id); // і resolved, і misses — повторних запитів нема
        pendingRef.current.delete(id);
        if (batch.length >= BATCH_LIMIT) break;
      }
      inflightRef.current = true;
      const ac = new AbortController();
      abortRef.current = ac;
      batchResolvePosters(batch, ac.signal)
        .then((r) => {
          if (!aliveRef.current) return;
          const map: ResolvedPostersMap = new Map();
          for (const [k, v] of Object.entries(r?.resolved ?? {})) {
            const id = Number(k);
            if (!Number.isInteger(id) || !v) continue;
            map.set(id, v);
          }
          if (map.size > 0) onResolvedRef.current(map);
        })
        .catch(() => {
          // Транспортний збій: id не «згоріли» — можливий чесний ретрай пізніше
          // (лише за наступним природним тригером, без автопідганяння).
          for (const id of batch) requested.delete(id);
        })
        .finally(() => {
          inflightRef.current = false;
          if (abortRef.current === ac) abortRef.current = null;
          if (!aliveRef.current) {
            // Відповідь після unmount мерджити нікуди — дозволяємо повтор пізніше.
            for (const id of batch) requested.delete(id);
            return;
          }
          if (pendingRef.current.size > 0) schedule(); // решта черги — наступний батч
        });
    }

    // Повернення в online/visible: черга могла чекати саме на це.
    const onOnline = () => schedule();
    const onVisible = () => {
      if (document.visibilityState === "visible") schedule();
    };
    window.addEventListener("online", onOnline);
    document.addEventListener("visibilitychange", onVisible);

    collect(items); // перша колекція поточної сторінки
    if (pendingRef.current.size > 0) schedule();

    return () => {
      aliveRef.current = false;
      window.removeEventListener("online", onOnline);
      document.removeEventListener("visibilitychange", onVisible);
      if (timerRef.current != null) {
        window.clearTimeout(timerRef.current);
        timerRef.current = null;
      }
      // Скасувати in-flight батч: відповідь після unmount все одно нікуди не мерджиться.
      abortRef.current?.abort();
      abortRef.current = null;
    };
  }, [items]);
}
