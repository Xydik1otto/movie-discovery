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
// "ads" — безкоштовний перегляд з рекламою (уніфікований watch-ендпоінт 09.11).
export type ProviderKind = "stream" | "subscription" | "rent" | "buy" | "free" | "ads";
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
  // Discover: id YouTube-трейлера (бекенд додає в TitleMapper; null/відсутнє —
  // кнопка «Трейлер» на свайп-картці не показується).
  trailerYtId?: string | null;
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
  // UX-05: кандидати трейлерів (заповнюється лише у TitleDetail; на картках null).
  trailers?: TrailerInfo[] | null;
}

// UX-05: кандидат трейлера (title_trailers). lang — uk|ru|en|…|null = невідома мова
// (чесний unknown). verifiedStatus: ok|gone|nonembeddable|private|null — бекенд не
// віддає не-ok синк-кандидатів; виняток — швидкий шлях titles.trailer_yt_id з
// перевіреним статусом (фронт показує повідомлення/зовнішнє посилання, не iframe).
export interface TrailerInfo {
  ytId: string;
  lang?: string | null;
  official?: boolean | null;
  source?: string | null;
  verifiedAt?: string | null;
  verifiedStatus?: string | null;
}

// Відповідь GET /api/titles/{id}/trailers (кеш бекенда 24 год; on-demand search за потреби).
export interface TitleTrailersResponse {
  titleId: number;
  trailers: TrailerInfo[];
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
  // Уніфікований watch-ендпоінт (09.11): джерело рядка
  // (sweettv|megogo|plex|youtube|internet-archive|justwatch|…).
  source?: string | null;
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
  // Точний підрахунок обрізаний лімітом бекенда (текстовий пошук, 2000+) —
  // відображати «Знайдено: 2000+», не «2001».
  totalCapped?: boolean;
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
  // UX-01: швидкі перегортання (page>1 з кешованим total) — бекенд пропускає
  // COUNT і повертає total=-1. Опційний; без нього поведінка без змін.
  omitTotal?: boolean;
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
  // FR-17: «прочитане» (манґа) — окремий лічильник, НЕ змішується з watched
  // (вкладки «Переглянуте»/«Прочитане» роздільні; чесні числа з БД на обох).
  readCount?: number | null;
  likedCount?: number | null;
  dislikedCount?: number | null;
  avgRating?: number | null;
  // topGenres може прийти масивом рядків або пар genre/count — толеруємо обидва.
  topGenres?: (string | { genre: string; count?: number | null })[] | null;
}

// Живий API повертає options як {key, nameUk, nameEn} — демо лишається
// у фронтовій формі {id, label, value}; нормалізацію робить getOnboardingQuestions.
export interface OnboardingOption {
  id: string;
  key?: string | null;
  label?: string | null;
  value?: string | null;
  nameUk?: string | null;
  nameEn?: string | null;
  posterUrl?: string | null;
}

export interface OnboardingQuestion {
  id: string;
  type: "posters" | "mood" | "era" | "titles";
  title?: string | null;
  promptUk?: string | null;
  promptEn?: string | null;
  options?: OnboardingOption[] | null;
}

// FR-18: контракт бекенду (OnboardingController.AnswersRequest) — MD-02:
// picks — ТІЛЬКИ жанрові ключі з poster-питань (option.key), mood/era — масиви
// ключів відповідних питань, favoriteTitles — до 3 ВНУТРІШНІХ id тайтлів.
export interface OnboardingAnswers {
  picks: string[];
  mood: string[];
  era: string[];
  favoriteTitles: number[];
  skipped: boolean;
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

// --- FR: франшиза/сага (серія тайтла) — GET /api/titles/{id}/series ---

export interface SeriesItem {
  id: number;
  title: string;
  year?: number | null;
  posterUrl?: string | null;
  type: string;
  watched: boolean;
  watchlist: boolean;
  isCurrent: boolean;
}

export interface SeriesResponse {
  seriesKey?: string | null;
  seriesName?: string | null;
  items: SeriesItem[];
}
