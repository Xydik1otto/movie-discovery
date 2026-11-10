"use client";

// Довідник фільтрів (GET /api/filters?category=) з захистами:
// • кеш у пам'яті на сесію ПО КАТЕГОРІЯХ, TTL 10 хв; прострочений запис віддаємо
//   одразу (stale-while-revalidate) і тягнемо свіжий у фоні — панель не блимає;
// • дедуп паралельних запитів тієї самої категорії (спільний Promise) — ThemeTabs
//   і FilterPanel на одній сторінці дають РІВНО один HTTP-запит;
// • невдача теж кешується, але коротко (60 с): ендпоінт недоступний → панель
//   деградує до статики без шторму повторних запитів.
// Хук нічого не кидає: data=null означає «немає даних → рендер статики».

import { useEffect, useState } from "react";
import { getFilters } from "./api";
import type { FiltersPayload } from "./types";

const TTL_MS = 10 * 60 * 1000;
const FAIL_TTL_MS = 60 * 1000;

interface CacheEntry {
  data: FiltersPayload | null;
  at: number;
}

const cache = new Map<string, CacheEntry>();
const inflight = new Map<string, Promise<FiltersPayload | null>>();

function keyOf(category: string | null): string {
  return category ?? "";
}

/** Отримати довідник з кешом/дедупом; ніколи не кидає (помилка → null). */
export async function fetchFiltersCached(
  category: string | null,
): Promise<FiltersPayload | null> {
  const key = keyOf(category);
  const hit = cache.get(key);
  const ttl = hit?.data ? TTL_MS : FAIL_TTL_MS;
  if (hit && Date.now() - hit.at < ttl) return hit.data;
  let p = inflight.get(key);
  if (!p) {
    p = getFilters(category)
      .then((data) => {
        cache.set(key, { data: data ?? null, at: Date.now() });
        return data ?? null;
      })
      .catch(() => {
        cache.set(key, { data: null, at: Date.now() });
        return null;
      })
      .finally(() => {
        inflight.delete(key);
      });
    inflight.set(key, p);
  }
  return p;
}

export interface FiltersDataState {
  data: FiltersPayload | null;
  /** true лише поки даних ще немає і запит триває (скелетон панелі). */
  loading: boolean;
}

function initial(category: string | null): FiltersDataState {
  const hit = cache.get(keyOf(category));
  return hit?.data ? { data: hit.data, loading: false } : { data: null, loading: true };
}

export function useFiltersData(category: string | null): FiltersDataState {
  const [state, setState] = useState<FiltersDataState>(() => initial(category));

  useEffect(() => {
    let alive = true;
    const hit = cache.get(keyOf(category));
    // Свіжий кеш → миттєві дані; прострочений → показуємо stale, оновлюємо у фоні.
    setState(hit?.data ? { data: hit.data, loading: false } : initial(category));
    fetchFiltersCached(category).then((data) => {
      if (!alive) return;
      if (data) setState({ data, loading: false });
      else setState((prev) => (prev.data ? prev : { data: null, loading: false }));
    });
    return () => {
      alive = false;
    };
  }, [category]);

  return state;
}
