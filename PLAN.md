# PLAN.md — ЄДИНИЙ API-КОНТРАКТ (авторитетний документ)

> Цей документ — джерело правди для ВСІХ агентів. Конфлікт між уявою агента і цим файлом → виграє цей файл.
> Базові конвенції: REST, JSON (camelCase), UTC-часи ISO 8601. Локалізація даних передається параметром `?lang=uk|en` (uk за замовчуванням; fallback на en, якщо uk-текст відсутній). Російської мови НЕМАЄ ніде — свідоме рішення власника.

## Порти та середовище

| Сервіс | Порт (хост) | Примітка |
|---|---|---|
| api (ASP.NET Core 8) | **5200** | ASPNETCORE_URLS=http://+:5200 |
| web (Next.js) | **3001** (хост) → 3000 (контейнер) | NEXT_PUBLIC_API_URL |
| db (PostgreSQL 16 + pgvector) | **5433** (хост) → 5432 | образ `pgvector/pgvector:pg16` |
| bot (Telegram, long polling) | без порту | TG_BOT_TOKEN, API_URL=http://api:5200 |

Ollama (embeddings): `OLLAMA_URL`, за замовчуванням `http://host.docker.internal:11434`; модель `OLLAMA_EMBED_MODEL=bge-m3` (1024 dim).Якщо Ollama недоступна/моделі нема — api продовжує працювати (метадані-схожість), семантичні фічі повертають осмислену помилку 503 з текстом "Semantic index unavailable", не падають.

## Змінні оточення (.env, бачать всі сервіси)

```
POSTGRES_USER=movie
POSTGRES_PASSWORD=<secret>
POSTGRES_DB=moviediscovery
PG_PORT=5433
ConnectionStrings__Default=Host=db;Port=5432;Database=moviediscovery;Username=movie;Password=<secret>   # для api (docker); поза докером Host=localhost;Port=5433
TMDB_API_KEY=<free key,themoviedb.org>
OMDB_API_KEY=            # опційно
OLLAMA_URL=http://host.docker.internal:11434
OLLAMA_EMBED_MODEL=bge-m3
ADMIN_KEY=<secret>       # header X-Admin-Key для /api/admin/*
API_PORT=5200
WEB_PORT=3001
TG_BOT_TOKEN=            # заповнює власник
API_URL=http://api:5200  # для бота всередині compose
```

## Схема БД (PostgreSQL; names snake_case у БД, C# PascalCase)

**titles** — `id serial PK`, `tmdb_id int UNIQUE NOT NULL`, `type text NOT NULL` (`movie|series|show`), `title_uk text`, `title_en text NOT NULL`, `overview_uk text`, `overview_en text`, `year int`, `countries text[]`, `genres text[]`, `keywords text[]`, `actors text[]`, `director text`, `runtime_min int NULL`, `episode_runtime_min int NULL`, `seasons int NULL`, `status text NULL` (`released|returning|ended|canceled`), `original_language text`, `rating_tmdb double precision NULL`, `rating_imdb double precision NULL`, `votes_imdb int NULL`, `popularity double precision DEFAULT 0`, `poster_path text`, `backdrop_path text`, `trailer_yt_id text NULL`, `created_at timestamptz DEFAULT now()`, `updated_at timestamptz DEFAULT now()`. Індекси: gin(genres), gin(keywords), gin(actors), btree(year), btree(popularity desc), unique(tmdb_id,type).

**providers** — `id serial PK`, `title_id int FK→titles ON DELETE CASCADE`, `provider text NOT NULL`, `kind text NOT NULL` (`stream|rent|buy|free`), `url text`, `source text NOT NULL`, `region text NOT NULL DEFAULT 'UA'`, `updated_at timestamptz`. UNIQUE(title_id, provider, kind, source, region).

**embeddings** — `title_id int PK FK→titles CASCADE`, `model text NOT NULL`, `dim int NOT NULL DEFAULT 1024`, `vec vector(1024) NOT NULL`, `updated_at timestamptz`.

**profiles** — `id serial PK`, `name text NOT NULL`, `is_default bool DEFAULT false`, `created_at timestamptz`. При першому старті сіється єдиний профіль "Головний" (is_default=true).

**lists** — `id serial PK`, `profile_id int FK→profiles`, `kind text NOT NULL` (`watchlist|watched`), `title_id int FK→titles CASCADE`, `rating int NULL` (1–10), `created_at timestamptz DEFAULT now()`, `watched_at timestamptz NULL`. UNIQUE(profile_id, kind, title_id).

**queries** — `id serial PK`, `profile_id int NULL FK`, `name text NOT NULL`, `seeds jsonb NOT NULL` (масив int tmdb_id), `filters jsonb NULL`, `created_at timestamptz`.

**source_cache** — `id serial PK`, `source text NOT NULL`, `cache_key text NOT NULL`, `payload jsonb NOT NULL`, `fetched_at timestamptz NOT NULL`, `ttl_seconds int NOT NULL DEFAULT 0`. UNIQUE(source, cache_key). Правило: ttl_seconds=0 → кеш безстроковий (метадані тайтла); будь-який зовнішний виклик спершу дивиться сюди.

**sync_state** — `id serial PK`, `source text NOT NULL`, `kind text NOT NULL`, `last_run_at timestamptz`, `last_pages int`, `last_count int`. UNIQUE(source, kind).

## Ендпоінти api (base `http://localhost:5200`)

Форма `TitleCard` (використовується всюди):
```json
{
  "id": 123, "tmdbId": 456, "type": "movie",
  "title": "Назва (локалізована)", "originalTitle": "Original",
  "year": 2019, "genres": ["драма"], "countries": ["US"],
  "ratingTmdb": 7.8, "ratingImdb": 8.1,
  "posterUrl": "https://image.tmdb.org/...", "runtimeMin": 142, "seasons": null,
  "overview": "Перші 2-3 речення опису (локалізовані)"
}
```

- `GET /api/health` → `{"status":"ok","db":true,"ollama":false,"titlesCount":0}`
- `GET /api/search?q=&type=&genres=a,b&yearFrom=&yearTo=&country=&lang=&minRating=&maxRating=&minRuntime=&maxRuntime=&status=&sort=relevance|rating|year|popularity&page=1&pageSize=24`
  → `{"items":[TitleCard],"total":1234,"page":1,"pageSize":24}`. Пошук/фільтри — ЛИШЕ по локальній БД (postgres full-text + фільтри); жодних зовнішніх викликів у hot path. `q` шукає в назвах (uk+en), акторах, режисері.
- `GET /api/title/{id}?lang=` → TitleCard + `actors`, `director`, `keywords`, `overviewFull`, `backdropUrl`, `trailerYtId`, `providers:[{provider,kind,url}]`, `status`, `originalLanguage`.
- `GET /api/title/{id}/similar?limit=20&lang=` → `{"items":[TitleCard + "score":0.83, "reasons":["спільні жанри: драма, трилер","спільний актор X","близький рік"]]}` (reasons — 1-3 рядки, ЛОКАЛІЗОВАНІ).
- `POST /api/similar/by-seeds` body `{"tmdbIds":[123,456],"limit":20,"lang":"uk"}` → та сама форма. tmdbIds 1–10; незнайдені id → 404 з переліком.
- `POST /api/similar/by-text` body `{"text":"...","limit":20,"lang":"uk"}` → та сама форма. Якщо Ollama недоступна → 503 (не падати). Реалізація Е2: embed тексту + hybrid; на Е0/Е1 дозволений fallback: keyword extraction з тексту → пошук по keywords/genres.
- `GET /api/title/{id}/providers?region=UA` → `{"providers":[{...}]}`.
- `GET /api/profiles` → `[{id,name,isDefault}]`; `POST /api/profiles {name}` (Е2+, але ендпоінт існує з Е0).
- `GET /api/lists?profileId=1&kind=watchlist` → `{"items":[{itemId, ...TitleCard, kind, rating, createdAt, watchedAt}]}` (`itemId` — id РЯДКА списку; саме в нього целяться PATCH/DELETE; `id` всередині — id тайтла); `POST /api/lists {profileId,kind,tmdbId,rating?}` → 201; `PATCH /api/lists/{itemId} {rating?}`; `DELETE /api/lists/{itemId}`. Реакція на неіснуючий tmdbId → 404, дублікат → 409.
- `GET /api/i18n/{lang}` → плоский словник `{"key":"текст",...}` (uk, en). КОПІЯ у backend як дефолт; фронт тримає свою (збіжні ключі!) — contract: ключі описані у PLAN §i18n.
- `POST /api/admin/sync/popular` header `X-Admin-Key`, body `{"pages":5,"type":"movie|series|all"}` → 202 `{queued:true}`; фонова джоба тягне TMDB popular/discover батчами, upsert у titles, ставить чергу на embeddings.
- `GET /api/admin/sync/status` → `{lastRunAt, titlesCount, embeddingsCount, queueSize}`.
- Swagger: увімкнений лише в Development (`/swagger`).

## Движок схожості (Core, чистий C#, без I/O — тестопридатний)

Ваги (конфіг `SimilarityOptions`): semantic 0.35, keywords 0.20, genres 0.15, credits 0.15, era 0.05, numeric(rating+runtime) 0.05, tmdbSimilarBoost 0.05; бонус same-type ×1.1. Semantic term = 0, якщо векторів нема → ваги нормалізуються на решту. Jaccard для жанрів; overlap-частка для keywords/actors (режисер ×2); era: exp-спад за |Δyear| з напівжиттям 7 років; numeric: гаус. Multi-seed: середнє векторів seeds; жанри/keywords/actors — об'єднання з вагою (означена ознака зустрічається у k seeds → вага k/n); причини — топ-3 внески. Косинус — через `System.Numerics.Tensors` (SIMD) або `TensorPrimitives.CosineSimilarity`.

## i18n ключі (спільний мінімум для frontend-словника і /api/i18n)

`nav.home, nav.similar, nav.lists, nav.settings, search.placeholder, search.button, filters.type, filters.genre, filters.year, filters.country, filters.rating, filters.runtime, filters.apply, filters.reset, title.similar, title.why, title.watchlist, title.watched, title.watchedAdd, title.providers, title.trailer, similar.seedsTitle, similar.seedsHint, similar.byText, similar.byTextPlaceholder, lists.watchlist, lists.watched, lists.empty, common.loading, common.error, api.unavailable, lang.switch`

## Правила зон (ХТО ЩО СОЗДАЄ — не порушувати)

- **backend-агент**: усе в `backend/**` (включно з `backend/Dockerfile`), рішення solution-структури його.
- **frontend-агент**: усе в `frontend/**` (включно з `frontend/Dockerfile`).
- **bot-агент**: усе в `bot/**` (включно з `bot/Dockerfile`).
- **Батько (оркестратор)**: `docker-compose.yml`, `.env.example`, `.gitignore`, `README.md`, `PLAN.md`, `TZ.md`, git-коміти. Агенти НЕ виконують жодних git-команд.
