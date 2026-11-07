// Форми даних за контрактом PLAN.md (camelCase, як у API).

export type TitleType = "movie" | "series" | "show" | "anime" | "manga";

// FR-19: бекенд-коди віртуальних категорій та озвучок (/api/search?category=,
// watch_links.dub). Невідомі бекенд-коди толеруємо як string у полях даних —
// ці юніони типізують лише довідники опцій у constants.ts.
export type CategoryCode =
  | "movie"
  | "series"
  | "anime"
  | "manga"
  | "tvshow"
  | "cartoon"
  | "dorama";
export type AudioLangCode =
  | "ukr"
  | "russ"
  | "eng"
  | "jpn"
  | "kor"
  | "zho"
  | "original"
  | "multi";
export type DubCode = "ukr" | "russ" | "eng" | "original" | "multi";
export type ProviderKind = "stream" | "subscription" | "rent" | "buy" | "free";
// FR-17: liked/disliked розширюють системні списки.
export type ListKind = "watchlist" | "watched" | "read" | "liked" | "disliked";

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
  // FR-19/20: озвучка і якість конкретного посилання (опційні — бекенд
  // повертає audio_lang/quality/dub, коли вони відомі; інакше null).
  audioLang?: string | null;
  quality?: number | null;
  dub?: string | null;
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
  // FR-19: віртуальна категорія, країна виробника (ISO-2), мова озвучки
  // в наявних провайдерах і мінімальна якість watch_links — усі опційні.
  category?: string | null;
  audioLang?: string | null;
  minQuality?: number | null;
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

// --- FR-16/17/18: акаунти, discover, онбординг, статистика списків ---

export interface AuthUser {
  id: number;
  email?: string | null;
  username: string;
  onboardingDone?: boolean | null;
}

// Нормалізовані помилки полів реєстрації (бекенд повертає {errors: {...}}).
export interface AuthFieldErrors {
  email?: string[];
  username?: string[];
  password?: string[];
  general?: string[];
}

export type DiscoverMode = "exploit" | "safe" | "explore";
export type DiscoverAction = "like" | "dislike" | "skip" | "watchlist" | "watched";

export interface DiscoverResponse {
  items: TitleCard[];
  mode?: DiscoverMode | null;
}

export interface ListStats {
  watchedCount?: number | null;
  likedCount?: number | null;
  dislikedCount?: number | null;
  avgRating?: number | null;
  // topGenres може прийти масивом рядків або пар genre/count — толеруємо обидва.
  topGenres?: (string | { genre: string; count?: number | null })[] | null;
}

export interface OnboardingOption {
  id: string;
  label?: string | null;
  value?: string | null;
  posterUrl?: string | null;
}

export interface OnboardingQuestion {
  id: string;
  type: "posters" | "mood" | "era" | "titles";
  title?: string | null;
  options?: OnboardingOption[] | null;
}

export interface OnboardingAnswers {
  picks?: { questionId: string; values: string[] }[];
  mood?: string[];
  era?: string | null;
  favoriteTitles?: { titleId: number; tmdbId?: number | null; title: string }[];
  skipped?: boolean;
}

// Елемент гостьового списку для POST /api/lists/migrate-guest.
export interface GuestListItem {
  kind: ListKind;
  titleId: number;
  tmdbId?: number | null;
  rating?: number | null;
}

// --- FR-21: «Схоже на...» — студія, збірники, reasons-коди ---

// Опційні фільтри студії для GET /api/similar/by-seeds?ids=...&yearFrom=&yearTo=&types=&genres=.
// genres — soft-boost (не hard-фільтр), type/рік — hard-рамки.
export interface SimilarSeedsFilters {
  yearFrom?: number | null;
  yearTo?: number | null;
  // "" | "movie" | "series" | "anime" (null/"" = усі типи)
  type?: string | null;
  genres?: string[] | null;
}

// Елемент збірки: бекенд повертає або внутрішні titleId (форма POST),
// або повні картки тайтлів (пресети) — толеруємо обидва вигляди.
export type CollectionEntry = TitleCard | number;

export interface CollectionSummary {
  id: number;
  name: string;
  itemCount?: number | null;
  items?: CollectionEntry[] | null;
  createdAt?: string | null;
}

export interface PresetCollection {
  key: string;
  items: CollectionEntry[];
}
