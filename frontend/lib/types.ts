// Форми даних за контрактом PLAN.md (camelCase, як у API).

export type TitleType = "movie" | "series" | "show" | "anime" | "manga";
export type ProviderKind = "stream" | "subscription" | "rent" | "buy" | "free";
export type ListKind = "watchlist" | "watched" | "read";

export interface TitleCard {
  id: number;
  // Для аніме/манги з зовнішніх джерел tmdb_id може бути NULL.
  tmdbId?: number | null;
  type: TitleType;
  title: string;
  originalTitle?: string | null;
  year?: number | null;
  genres?: string[];
  countries?: string[];
  ratingTmdb?: number | null;
  ratingImdb?: number | null;
  posterUrl?: string | null;
  runtimeMin?: number | null;
  seasons?: number | null;
  overview?: string | null;
  // Аніме/манга-специфіка (PLAN §Аніме і манга; null для звичайних тайтлів).
  format?: string | null;
  episodes?: number | null;
  chapters?: number | null;
  volumes?: number | null;
  studios?: string[] | null;
  authors?: string[] | null;
  scoreMal?: number | null;
  scoreAnilist?: number | null;
  scoreShikimori?: number | null;
  // FR-14: компактні провайдери перегляду (лише is_available=true;
  // provider: sweettv|archive|tmdb-streaming).
  watchProviders?: WatchProvider[];
}

// Провайдер перегляду (FR-14, watch_links). Компактна форма — у TitleCard
// (provider/kind/url); повна — GET /api/titles/{id}/watch (region, isAvailable).
export interface WatchProvider {
  provider: string;
  kind: ProviderKind;
  url?: string | null;
  region?: string | null;
  isAvailable?: boolean | null;
}

export interface TitleDetail extends TitleCard {
  actors?: string[];
  director?: string | null;
  keywords?: string[];
  overviewFull?: string | null;
  backdropUrl?: string | null;
  trailerYtId?: string | null;
  providers?: WatchProvider[];
  status?: string | null;
  originalLanguage?: string | null;
}

export interface SimilarItem extends TitleCard {
  score?: number | null;
  reasons?: string[];
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface ListItem extends TitleCard {
  rating?: number | null;
  createdAt?: string | null;
  itemId?: number | null;
}

export interface ListResponse {
  items: ListItem[];
}

export interface HealthResponse {
  status: string;
  db: boolean;
  ollama: boolean;
  titlesCount: number;
}

export interface SearchParams {
  q?: string;
  type?: string | null;
  format?: string | null;
  genres?: string[];
  yearFrom?: number | null;
  yearTo?: number | null;
  country?: string | null;
  minRating?: number | null;
  maxRating?: number | null;
  minRuntime?: number | null;
  maxRuntime?: number | null;
  status?: string | null;
  // розширені
  actor?: string | null;
  director?: string | null;
  keyword?: string | null;
  language?: string | null;
  minVotes?: number | null;
  seasonsFrom?: number | null;
  seasonsTo?: number | null;
  excludeGenres?: string[];
  hasTrailer?: boolean | null;
  // FR-14: лише тайтли з доступними watch_links (?watchable=true).
  watchable?: boolean | null;
  ratingSource?: string | null;
  sort?: "relevance" | "rating" | "imdb" | "votes" | "year" | "popularity";
  page?: number;
  pageSize?: number;
}

// --- Імпорт списків (PLAN §Аніме і манга: «Імпорт списків») ---

export type ImportSourceId = "mal" | "anilist" | "shikimori";
export type ImportJobStatus = "queued" | "running" | "done" | "failed";

export interface ImportSource {
  id: string;
  name: string;
  hint: string;
}

// Форма import_jobs.stats jsonb (PLAN) — усі поля опціональні.
export interface ImportJobStats {
  animeTotal?: number | null;
  mangaTotal?: number | null;
  matched?: number | null;
  created?: number | null;
  animeWatched?: number | null;
  mangaRead?: number | null;
  watchlistAdded?: number | null;
  errors?: string[] | null;
}

export interface ImportJob {
  id: number;
  source: string;
  username?: string | null;
  status: ImportJobStatus;
  stats?: ImportJobStats | null;
  error?: string | null;
}
