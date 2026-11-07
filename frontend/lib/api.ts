// Клієнт до API за контрактом PLAN.md.
// Базовий URL — NEXT_PUBLIC_API_URL, дефолт http://localhost:5200.

import type {
  HealthResponse,
  ListKind,
  ListItem,
  ListResponse,
  Paged,
  ProviderRef,
  SearchParams,
  SimilarItem,
  TitleCard,
  TitleDetail,
} from "./types";
import type { Lang } from "../i18n/I18nContext";

export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5200";
export const DEFAULT_PROFILE_ID = 1;

export class ApiUnavailableError extends Error {
  constructor() {
    super("API unavailable");
    this.name = "ApiUnavailableError";
  }
}

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
    this.name = "ApiError";
  }
}

// Швидкий фейл: після однієї мережевої відмови 10 с не дергаємо API,
// щоб UI не «трипав» на кожному ререндері без бекенду.
let downUntil = 0;

export function isApiMarkedDown(): boolean {
  return Date.now() < downUntil;
}

function markDown() {
  downUntil = Date.now() + 10_000;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  if (isApiMarkedDown()) throw new ApiUnavailableError();
  let res: Response;
  try {
    res = await fetch(`${API_URL}${path}`, {
      ...init,
      headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) },
      cache: "no-store",
      signal: AbortSignal.timeout(8000),
    });
  } catch {
    markDown();
    throw new ApiUnavailableError();
  }
  if (res.status === 503) {
    // "Semantic index unavailable" — семантичні фічі недоступні, але API живе.
    throw new ApiError(503, await res.text().catch(() => "Service unavailable"));
  }
  if (!res.ok) {
    throw new ApiError(res.status, `${res.status} ${res.statusText}`);
  }
  return (await res.json()) as T;
}

function qs(params: Record<string, string | number | undefined | null>): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== "") sp.set(k, String(v));
  }
  const s = sp.toString();
  return s ? `?${s}` : "";
}

export async function getHealth(): Promise<HealthResponse | null> {
  try {
    return await request<HealthResponse>("/api/health");
  } catch {
    return null;
  }
}

export async function searchTitles(
  p: SearchParams,
  lang: Lang,
): Promise<Paged<TitleCard>> {
  const path =
    `/api/search${qs({
      q: p.q,
      type: p.type,
      genres: p.genres?.length ? p.genres.join(",") : undefined,
      yearFrom: p.yearFrom ?? undefined,
      yearTo: p.yearTo ?? undefined,
      country: p.country,
      minRating: p.minRating ?? undefined,
      maxRating: p.maxRating ?? undefined,
      minRuntime: p.minRuntime ?? undefined,
      maxRuntime: p.maxRuntime ?? undefined,
      status: p.status,
      actor: p.actor,
      director: p.director,
      keyword: p.keyword,
      language: p.language,
      minVotes: p.minVotes ?? undefined,
      seasonsFrom: p.seasonsFrom ?? undefined,
      seasonsTo: p.seasonsTo ?? undefined,
      excludeGenres: p.excludeGenres?.length ? p.excludeGenres.join(",") : undefined,
      hasTrailer: p.hasTrailer == null ? undefined : p.hasTrailer ? "true" : "false",
      hasProviders: p.hasProviders == null ? undefined : p.hasProviders ? "true" : "false",
      ratingSource: p.ratingSource,
      sort: p.sort,
      page: p.page ?? 1,
      pageSize: p.pageSize ?? 24,
    })}&lang=${lang}`;
  return request<Paged<TitleCard>>(path);
}

export async function getTitle(id: number | string, lang: Lang): Promise<TitleDetail> {
  return request<TitleDetail>(`/api/title/${id}${qs({ lang })}`);
}

export async function getTitleSimilar(
  id: number | string,
  lang: Lang,
  limit = 20,
): Promise<{ items: SimilarItem[] }> {
  return request<{ items: SimilarItem[] }>(`/api/title/${id}/similar${qs({ limit, lang })}`);
}

export async function similarBySeeds(
  tmdbIds: number[],
  lang: Lang,
  limit = 20,
): Promise<{ items: SimilarItem[] }> {
  return request<{ items: SimilarItem[] }>("/api/similar/by-seeds", {
    method: "POST",
    body: JSON.stringify({ tmdbIds, limit, lang }),
  });
}

export async function similarByText(
  text: string,
  lang: Lang,
  limit = 20,
): Promise<{ items: SimilarItem[] }> {
  return request<{ items: SimilarItem[] }>("/api/similar/by-text", {
    method: "POST",
    body: JSON.stringify({ text, limit, lang }),
  });
}

export async function getProviders(
  id: number | string,
  region = "UA",
): Promise<{ providers: ProviderRef[] }> {
  return request<{ providers: ProviderRef[] }>(`/api/title/${id}/providers${qs({ region })}`);
}

export async function getLists(
  kind: ListKind,
  profileId = DEFAULT_PROFILE_ID,
): Promise<ListResponse> {
  return request<ListResponse>(`/api/lists${qs({ profileId, kind })}`);
}

export async function addToList(
  tmdbId: number,
  kind: ListKind,
  rating?: number | null,
  profileId = DEFAULT_PROFILE_ID,
): Promise<ListItem | null> {
  const body: Record<string, unknown> = { profileId, kind, tmdbId };
  if (rating != null) body.rating = rating;
  return request<ListItem | null>("/api/lists", {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function patchListItem(
  itemId: number,
  rating: number,
): Promise<void> {
  await request<void>(`/api/lists/${itemId}`, {
    method: "PATCH",
    body: JSON.stringify({ rating }),
  });
}

export async function deleteListItem(itemId: number): Promise<void> {
  await request<void>(`/api/lists/${itemId}`, { method: "DELETE" });
}
