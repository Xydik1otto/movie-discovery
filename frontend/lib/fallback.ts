// Обгортки над api.ts з деградацією: якщо API недоступний
// (мережева помилка) — повертаємо демо-дані + прапорець unavailable.

import {
  ApiError,
  addToList,
  deleteListItem,
  getLists,
  getTitle,
  getTitleSimilar,
  patchListItem,
  searchTitles,
  similarBySeeds,
  similarByText,
} from "./api";
import { demoCards, demoDetail, demoLists, demoSearch, demoSimilar } from "./demo";
import type { Lang } from "../i18n/I18nContext";
import type {
  ListKind,
  ListItem,
  Paged,
  SearchParams,
  SimilarItem,
  TitleCard,
  TitleDetail,
} from "./types";

export interface Gate<T> {
  data: T;
  unavailable: boolean;
}

export async function guardedSearch(
  p: SearchParams,
  lang: Lang,
): Promise<Gate<Paged<TitleCard>>> {
  try {
    return { data: await searchTitles(p, lang), unavailable: false };
  } catch (e) {
    if (isUnavailable(e)) {
      const d = demoSearch(
        {
          q: p.q,
          type: p.type,
          genres: p.genres,
          yearFrom: p.yearFrom,
          yearTo: p.yearTo,
          country: p.country,
          minRating: p.minRating,
          maxRating: p.maxRating,
          minRuntime: p.minRuntime,
          maxRuntime: p.maxRuntime,
        },
        lang,
        p.page ?? 1,
        p.pageSize ?? 24,
      );
      return { data: d, unavailable: true };
    }
    throw e;
  }
}

export async function guardedTitle(
  id: number,
  lang: Lang,
): Promise<Gate<TitleDetail | null>> {
  try {
    return { data: await getTitle(id, lang), unavailable: false };
  } catch (e) {
    if (isUnavailable(e)) {
      return { data: demoDetail(id, lang), unavailable: true };
    }
    throw e;
  }
}

export async function guardedTitleSimilar(
  id: number,
  lang: Lang,
  limit = 20,
): Promise<Gate<SimilarItem[]>> {
  try {
    const r = await getTitleSimilar(id, lang, limit);
    return { data: r.items ?? [], unavailable: false };
  } catch (e) {
    if (isUnavailable(e)) {
      const d = demoDetail(id, lang);
      const seedTmdb = d ? [d.tmdbId] : [];
      return { data: demoSimilar(seedTmdb, lang, limit), unavailable: true };
    }
    throw e;
  }
}

export async function guardedBySeeds(
  tmdbIds: number[],
  lang: Lang,
  limit = 20,
): Promise<Gate<SimilarItem[]>> {
  try {
    const r = await similarBySeeds(tmdbIds, lang, limit);
    return { data: r.items ?? [], unavailable: false };
  } catch (e) {
    if (isUnavailable(e)) {
      return { data: demoSimilar(tmdbIds, lang, limit), unavailable: true };
    }
    throw e;
  }
}

export async function guardedByText(
  text: string,
  lang: Lang,
  limit = 20,
): Promise<Gate<SimilarItem[]>> {
  try {
    const r = await similarByText(text, lang, limit);
    return { data: r.items ?? [], unavailable: false };
  } catch (e) {
    if (isUnavailable(e)) {
      // Демо-fallback: шукаємо тайтли, чий текст опису перетинається із запитом.
      const words = text
        .toLowerCase()
        .split(/[\s,.!?;:]+/u)
        .filter((w) => w.length >= 4);
      const cards = demoCards(lang);
      const scored = cards
        .map((c) => {
          const hay = `${c.overview ?? ""} ${(c.genres ?? []).join(" ")}`.toLowerCase();
          const hits = words.filter((w) => hay.includes(w)).length;
          return { c, hits };
        })
        .sort((a, b) => b.hits - a.hits)
        .filter((x) => x.hits > 0)
        .slice(0, limit);
      const items: SimilarItem[] = scored.map(({ c }, i) => ({
        ...c,
        score: Math.max(0.4, 0.9 - i * 0.05),
        reasons: [lang === "uk" ? "семантичний збіг опису (демо)" : "semantic overview match (demo)"],
      }));
      return { data: items, unavailable: true };
    }
    throw e;
  }
}

export async function guardedLists(
  kind: ListKind,
  lang: Lang,
): Promise<Gate<ListItem[]>> {
  try {
    const r = await getLists(kind);
    return { data: r.items ?? [], unavailable: false };
  } catch (e) {
    if (isUnavailable(e)) {
      return { data: demoLists(kind, lang), unavailable: true };
    }
    throw e;
  }
}

// Мутації не мають демо-підмінки — помилки показуємо користувачу.
export { addToList, patchListItem, deleteListItem };

export function isUnavailable(e: unknown): boolean {
  return e instanceof Error && e.name === "ApiUnavailableError";
}

export { ApiError };
