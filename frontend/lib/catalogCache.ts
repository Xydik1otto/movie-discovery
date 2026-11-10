// SWR-кеш дефолтного вигляду головної (10.11): клік «Головна» малює каталог ОДРАЗУ
// з localStorage, поки свіжий fetch оновлює дані у фоні (без скелетона). Кешуємо
// ЛИШЕ дефолтний вигляд (порожні фільтри + sort=popularity, сторінка 1) — інші
// види/фільтри кеш не чіпають. Усі операції best-effort: SSR, private mode,
// quota-exceeded — мовчки деградують до «кешу нема».
import type { TitleCard } from "./types";

export interface CatalogCacheEntry {
  items: TitleCard[];
  total: number;
  capped: boolean;
  savedAt: number;
}

/** TTL кешу: 10 хв — після цього read повертає null (свіжість віддаємо бекенду). */
const TTL_MS = 10 * 60 * 1000;

/** Ключ per-мова: назви карток мовозалежні, тому uk/en не змішуються. */
const storageKey = (lang: string) => `md_home_cache_v1_${lang}`;

/**
 * Читання кешу дефолтного вигляду; null = нема / прострочений / пошкоджений.
 * Безпечний: try/catch навколо localStorage (SSR / private mode / JSON-сміття).
 */
export function readCatalogCache(lang: string): CatalogCacheEntry | null {
  try {
    if (typeof window === "undefined") return null;
    const raw = window.localStorage.getItem(storageKey(lang));
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== "object" || parsed === null) return null;
    const e = parsed as Partial<CatalogCacheEntry>;
    if (!Array.isArray(e.items) || e.items.length === 0) return null;
    if (typeof e.total !== "number" || !(e.total >= 0)) return null;
    if (typeof e.savedAt !== "number") return null;
    if (Date.now() - e.savedAt > TTL_MS) return null; // прострочений
    return {
      items: e.items as TitleCard[],
      total: e.total,
      capped: e.capped === true,
      savedAt: e.savedAt,
    };
  } catch {
    return null;
  }
}

/**
 * Запис кешу дефолтного вигляду (успішна сторінка 1). Порожня видача не кешується —
 * вона нічого не «показує одразу». Best-effort: quota/private mode ігнорується.
 */
export function writeCatalogCache(
  lang: string,
  items: TitleCard[],
  total: number,
  capped: boolean,
): void {
  try {
    if (typeof window === "undefined") return;
    if (!Array.isArray(items) || items.length === 0) return;
    if (!(total >= 0)) return;
    const entry: CatalogCacheEntry = { items, total, capped, savedAt: Date.now() };
    window.localStorage.setItem(storageKey(lang), JSON.stringify(entry));
  } catch {
    // localStorage недоступний — кеш опційний, продовжуємо без нього.
  }
}
