// Форми даних за контрактом PLAN.md (camelCase, як у API).

export type TitleType = "movie" | "series" | "show";
export type ProviderKind = "stream" | "rent" | "buy" | "free";
export type ListKind = "watchlist" | "watched";

export interface TitleCard {
  id: number;
  tmdbId: number;
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
}

export interface ProviderRef {
  provider: string;
  kind: ProviderKind;
  url?: string | null;
}

export interface TitleDetail extends TitleCard {
  actors?: string[];
  director?: string | null;
  keywords?: string[];
  overviewFull?: string | null;
  backdropUrl?: string | null;
  trailerYtId?: string | null;
  providers?: ProviderRef[];
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
  genres?: string[];
  yearFrom?: number | null;
  yearTo?: number | null;
  country?: string | null;
  minRating?: number | null;
  maxRating?: number | null;
  minRuntime?: number | null;
  maxRuntime?: number | null;
  status?: string | null;
  sort?: "relevance" | "rating" | "year" | "popularity";
  page?: number;
  pageSize?: number;
}
