// Клієнт до API за контрактом PLAN.md.
// Базовий URL для fetch із браузера. Дефолт — "" (relative, same-origin):
// Next.js проксіює /api/* на бекенд через rewrites (API_SERVER_URL), тож
// cookie-автентифікація завжди same-origin і працює за будь-яким доменом
// (локально, на сервері, за Cloudflare-тунелем — без CORS-пасток).
// Явний NEXT_PUBLIC_API_URL (http://...) лишається для діагностики/особливих топологій.
export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? "";
import type {
  AuthUser,
  CollectionSummary,
  DiscoverAction,
  DiscoverResponse,
  HealthResponse,
  GuestListItem,
  ImportJob,
  ImportSource,
  ListKind,
  ListItem,
  ListResponse,
  ListStats,
  OnboardingAnswers,
  OnboardingQuestion,
  Paged,
  PresetCollection,
  SearchParams,
  SeriesResponse,
  SimilarItem,
  SimilarSeedsFilters,
  TitleCard,
  TitleDetail,
  WatchProvider,
} from "./types";
import type { Lang } from "../i18n/I18nContext";

export const DEFAULT_PROFILE_ID = 1;

export class ApiUnavailableError extends Error {
  constructor() {
    super("API unavailable");
    this.name = "ApiUnavailableError";
  }
}

export class ApiError extends Error {
  status: number;
  // Розпарсене тіло помилки (наприклад {errors:{...}} реєстрації), якщо є.
  body: unknown;
  constructor(status: number, message: string, body: unknown = null) {
    super(message);
    this.status = status;
    this.body = body;
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
      // FR-16: cookie-автентифікація — cookie бекенду ходять з кожним запитом
      // (cross-origin між :3001 і :5200 — same-site, Lax-cookie проходять).
      credentials: "include",
      headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) },
      cache: "no-store",
      // Пошук/агрегати по 2.8 млн рядків під навантаженням масових синків можуть
      // легально мислити 10-20 с; 8 с давало фальшивий «API недоступний» + демо-дані.
      signal: AbortSignal.timeout(25_000),
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
    // Тіло помилки зберігаємо (реєстрація повертає {errors:{...}} на 400).
    let body: unknown = null;
    try {
      const text = await res.text();
      body = text ? JSON.parse(text) : null;
    } catch {
      body = null;
    }
    throw new ApiError(res.status, `${res.status} ${res.statusText}`, body);
  }
  // 204/порожнє тіло — валідний результат для мутацій.
  const text = await res.text();
  return (text ? JSON.parse(text) : null) as T;
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
      format: p.format,
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
      watchable: p.watchable == null ? undefined : p.watchable ? "true" : "false",
      ratingSource: p.ratingSource,
      // FR-19: категорія/країна/озвучка/якість — усі опційні, порожні не летять.
      category: p.category,
      audioLang: p.audioLang,
      minQuality: p.minQuality ?? undefined,
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

// FR-21: GET-форма by-seeds з опційними фільтрами студії
// (/api/similar/by-seeds?ids=...&yearFrom=&yearTo=&types=&genres=).
// MD-03: ids — внутрішні title ids (єдина однозначна простір імен; tmdbId є
// лише в частини каталогу, тому GET-форма НІКОЛИ не отримує tmdb-ідентифікатори).
export async function similarBySeedsEx(
  internalIds: number[],
  lang: Lang,
  filters: SimilarSeedsFilters = {},
  limit = 20,
): Promise<{ items: SimilarItem[] }> {
  const sp = new URLSearchParams();
  sp.set("ids", internalIds.join(","));
  if (filters.yearFrom != null) sp.set("yearFrom", String(filters.yearFrom));
  if (filters.yearTo != null) sp.set("yearTo", String(filters.yearTo));
  if (filters.type) sp.set("types", filters.type);
  if (filters.genres?.length) sp.set("genres", filters.genres.join(","));
  sp.set("limit", String(limit));
  sp.set("lang", lang);
  try {
    return await request<{ items: SimilarItem[] }>(`/api/similar/by-seeds?${sp.toString()}`);
  } catch (e) {
    // CR-02: ЖОДНОГО legacy-fallback POST {tmdbIds} — він підміняв внутрішні id на tmdb
    // (колізія A.id==B.tmdb_id давала РІЗНІ еталони: direct [3,5] vs fallback [4]) і губив filters.
    // 404 з error=titles_not_found — чесна відповідь «еталони не знайдено»;
    // 404/405 без цього коду = GET-маршруту нема (старіший бекенд) — чесна помилка
    // «версія бекенду не підтримує GET by-seeds», а не мовчазна підміна еталона.
    if (
      e instanceof ApiError &&
      e.status === 404 &&
      (e.body as { error?: unknown } | null)?.error === "titles_not_found"
    ) {
      throw e;
    }
    if (e instanceof ApiError && (e.status === 404 || e.status === 405)) {
      throw new ApiError(
        e.status,
        "GET /api/similar/by-seeds не підтримується цим бекендом — оновіть бекенд.",
        { error: "similar_by_seeds_get_unsupported" },
      );
    }
    throw e;
  }
}

// Повна форма провайдерів перегляду (FR-14, уніфіковано 09.11):
// {providers: [{provider, kind: free|stream|rent|buy|ads, region, url, isAvailable, source}]}
// — злиття watch_links + providers на бекенді (WatchOptionsMerger).
export async function getWatch(
  id: number | string,
): Promise<{ providers: WatchProvider[] }> {
  return request<{ providers: WatchProvider[] }>(`/api/titles/${id}/watch`);
}

export async function getLists(
  kind: ListKind,
  profileId = DEFAULT_PROFILE_ID,
): Promise<ListResponse> {
  return request<ListResponse>(`/api/lists${qs({ profileId, kind })}`);
}

export async function addToList(
  ref: { tmdbId?: number | null; id: number },
  kind: ListKind,
  rating?: number | null,
): Promise<ListItem | null> {
  // Контракт FR-17: POST /api/lists/{kind} {titleId, rating?} — ідемпотентний
  // upsert за UNIQUE(profile,kind,title). Аніме/манга з імпорту не мають
  // tmdbId — тоді йде лише внутрішній titleId; бекенд толерує зайві поля.
  const body: Record<string, unknown> = { titleId: ref.id };
  if (ref.tmdbId != null) body.tmdbId = ref.tmdbId;
  if (rating != null) body.rating = rating;
  return request<ListItem | null>(`/api/lists/${kind}`, {
    method: "POST",
    body: JSON.stringify(body),
  });
}

// Зміна оцінки на watched/read — той самий ідемпотентний upsert (FR-17).
export async function rateListItem(
  kind: ListKind,
  ref: { tmdbId?: number | null; id: number },
  rating: number,
): Promise<void> {
  const body: Record<string, unknown> = { titleId: ref.id };
  if (ref.tmdbId != null) body.tmdbId = ref.tmdbId;
  body.rating = rating;
  await request<void>(`/api/lists/${kind}`, {
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

// --- Імпорт списків (PLAN: POST /api/import/jobs → 202 {jobId}) ---

export async function getImportSources(): Promise<ImportSource[]> {
  const r = await request<{ items?: ImportSource[] } | ImportSource[]>(
    "/api/import/sources",
  );
  if (Array.isArray(r)) return r;
  return r.items ?? [];
}

export async function startImportJob(
  source: string,
  username: string,
): Promise<{ jobId: number }> {
  return request<{ jobId: number }>("/api/import/jobs", {
    method: "POST",
    body: JSON.stringify({ source, username }),
  });
}

export async function getImportJob(id: number): Promise<ImportJob> {
  return request<ImportJob>(`/api/import/jobs/${id}`);
}

// --- FR-16: акаунти (cookie-автентифікація, credentials: include у request) ---

export async function registerUser(
  email: string,
  username: string,
  password: string,
): Promise<{ userId?: number | string }> {
  const r = await request<{ userId?: number | string } | null>("/api/auth/register", {
    method: "POST",
    body: JSON.stringify({ email, username, password }),
  });
  return r ?? {};
}

export async function loginUser(
  email: string,
  password: string,
  // «Запам'ятати мене»: поле летить завжди. Старший бекенд невідоме поле
  // ігнорує (несумісності немає), новший — робить cookie персистентною.
  // Дефолт = галочка у формі входу (ввімкнена).
  rememberMe = true,
): Promise<{ user?: AuthUser }> {
  const r = await request<{ user?: AuthUser } | null>("/api/auth/login", {
    method: "POST",
    body: JSON.stringify({ email, password, rememberMe }),
  });
  return r ?? {};
}

export async function logoutUser(): Promise<void> {
  await request<void>("/api/auth/logout", { method: "POST" });
}

// 401 → null (анонім), інші помилки прокидаємо нагору (auth.tsx розрізняє
// недоступність API від «не залогінений»).
export async function getMe(): Promise<AuthUser | null> {
  try {
    const r = await request<AuthUser | null>("/api/auth/me");
    return r ?? null;
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) return null;
    throw e;
  }
}

// --- FR-18: discover «Кінотека-свайпи» + онбординг ---

export async function getDiscover(
  count = 10,
  lang: Lang = "uk",
): Promise<DiscoverResponse> {
  const r = await request<Partial<DiscoverResponse> | null>(
    `/api/discover${qs({ count, lang })}`,
  );
  return {
    items: r?.items ?? [],
    mode: r?.mode ?? null,
  };
}

export async function postDiscoverAction(
  titleId: number,
  action: DiscoverAction,
  rating?: number | null,
): Promise<void> {
  const body: Record<string, unknown> = { titleId, action };
  if (rating != null) body.rating = rating;
  await request<void>("/api/discover/action", {
    method: "POST",
    body: JSON.stringify(body),
  });
}

export async function getOnboardingQuestions(lang: Lang = "uk"): Promise<OnboardingQuestion[]> {
  const r = await request<{ questions?: OnboardingQuestion[] } | OnboardingQuestion[]>(
    `/api/onboarding/questions${qs({ lang })}`,
  );
  const raw = Array.isArray(r) ? r : (r.questions ?? []);
  // MD-02: живий API віддає {id, type, promptUk/promptEn, options:[{key, nameUk, nameEn}]},
  // а фронтова форма — {id, label, value}. Нормалізуємо: option.id = option.key
  // (жанровий/десятиліттєвий ключ — саме він летить у picks/mood/era), label — локалізована назва.
  // Демо-питання ({id, label, value}) проходять наскрізь без змін.
  return raw.map((q) => ({
    ...q,
    title: q.title ?? (lang === "uk" ? q.promptUk : q.promptEn) ?? null,
    options: (q.options ?? []).map((o) => ({
      ...o,
      id: o.key ?? o.id,
      label: o.label ?? o.nameUk ?? o.nameEn ?? o.value ?? o.key ?? o.id,
    })),
  }));
}

export async function postOnboardingAnswers(answers: OnboardingAnswers): Promise<void> {
  await request<void>("/api/onboarding/answers", {
    method: "POST",
    body: JSON.stringify(answers),
  });
}

// --- FR-17: статистика списків + міграція гостьових даних ---

export async function getListStats(): Promise<ListStats> {
  const r = await request<ListStats | null>("/api/lists/stats");
  return r ?? {};
}

// MD-04: POST /api/lists/migrate-guest. CR-03: відповідь містить per-entry статуси
// (entries[].status: moved|duplicate|invalid_kind|invalid_rating|unknown_title);
// skipped = ЛИШЕ справжні дублікати, відхилені записи окремо в entries.
export interface MigrateEntryResult {
  titleId: number;
  kind: string;
  status: string;
}

export async function migrateGuestLists(
  items: GuestListItem[],
): Promise<{ moved?: number; skipped?: number; entries?: MigrateEntryResult[] }> {
  const r = await request<{ moved?: number; skipped?: number; entries?: MigrateEntryResult[] } | null>(
    "/api/lists/migrate-guest",
    {
      method: "POST",
      body: JSON.stringify({
        items: items.map((it) => ({ kind: it.kind, titleId: it.titleId, rating: it.rating ?? null })),
      }),
    },
  );
  return r ?? {};
}

// --- FR-21: збірники (collections). Без логіну — 401, панель показує підказку. ---

// Серверні пресети: [{key: 'mood'|'evening'|'duration', items: [...12 карток]}].
export async function getCollectionPresets(lang: Lang): Promise<PresetCollection[]> {
  const r = await request<{ items?: PresetCollection[] } | PresetCollection[]>(
    `/api/collections/presets${qs({ lang })}`,
  );
  return (Array.isArray(r) ? r : (r.items ?? [])) as PresetCollection[];
}

// Свої збірки (auth): [{id, name, itemCount?, items?}].
export async function getCollections(): Promise<CollectionSummary[]> {
  const r = await request<{ items?: CollectionSummary[] } | CollectionSummary[]>(
    "/api/collections",
  );
  return (Array.isArray(r) ? r : (r.items ?? [])) as CollectionSummary[];
}

// POST /api/collections {name, items:[titleId...]} — items = внутрішні id тайтлів.
export async function createCollection(
  name: string,
  titleIds: number[],
): Promise<CollectionSummary | null> {
  const r = await request<CollectionSummary | null>("/api/collections", {
    method: "POST",
    body: JSON.stringify({ name, items: titleIds }),
  });
  return r ?? null;
}

export async function deleteCollection(id: number): Promise<void> {
  await request<void>(`/api/collections/${id}`, { method: "DELETE" });
}

// --- FR: франшиза/сага тайтла (GET /api/titles/{id}/series) ---
// Поза франшизою бекенд віддає 200 {seriesKey: null, items: []} — не помилка.

export async function getTitleSeries(id: number, lang: Lang): Promise<SeriesResponse> {
  return request<SeriesResponse>(`/api/titles/${id}/series${qs({ lang })}`);
}
