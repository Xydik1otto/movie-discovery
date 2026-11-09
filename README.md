# MovieDiscovery (робоча назва)

Сервіс підбору фільмів, серіалів і телешоу: пошук за параметрами, гібридна схожість
(метадані + семантика описів), добірка «схоже на: [список еталонів]», вільний текстовий запит.
Веб + повнофункціональний Telegram-бот. Українською з перемикачем на англійську.

## Стек

- **Backend:** ASP.NET Core 8 (C#), EF Core, PostgreSQL 16 + pgvector
- **Frontend:** Next.js 15 + TypeScript + Tailwind (+ **PWA**: встановлюється на Android/iPhone як «ретранслятор» — manifest, service worker, офлайн-сторінка)
- **Бот:** Telegram.Bot (C#), long polling
- **Дані:** TMDB (ядро) + OMDb + Wikidata + власні скрапери (Е3)
- **Семантика:** локальні embeddings через Ollama (bge-m3, uk/en) — безкоштовно

## Швидкий старт (Docker)

```bash
cp .env.example .env
# заповнити TMDB_API_KEY (безкоштовно: themoviedb.org/settings/api)
docker compose up -d --build
```

- Веб: http://localhost:3001
- API: http://localhost:5200 (Swagger у Development)
- PostgreSQL: localhost:5433

## Швидкий старт (розробка без Docker)

```bash
# 1. PostgreSQL+pgvector (docker compose up -d db) або локальний PG на :5433
# 2. Backend
cd backend && dotnet run --project src/MovieDiscovery.Api   # :5200
# 3. Frontend (next dev без -p слухає :3000; на :3001 — npm run dev -- -p 3001
#    або next start після npm run build; standalone-запуск — frontend/Dockerfile)
cd frontend && npm install && npm run dev                    # :3000 (або -p 3001)
# 4. Бот (опційно, потрібен TG_BOT_TOKEN і BOT_API_KEY для авторизації запитів до API)
cd bot && dotnet run
```

## Структура

```
TZ.md          — технічне завдання (етапи Е0-Е4, критерії приймання)
PLAN.md        — АВТОРИТЕТНИЙ API-контракт (схеми, ендпоінти, env)
backend/       — ASP.NET Core 8 (Api / Core / Infrastructure + тести)
frontend/      — Next.js 15 (uk/en, темна тема)
bot/           — Telegram-бот (X-Bot-Key + X-Telegram-User-Id, власний профіль на TG-юзера)
docker-compose.yml, docker-compose.test.yml (ізольований fixture-стек для e2e), .env.example
e2e/           — e4_accounts.py (FR-16/17/18 smoke), md_audit_fixes.py (регресія MD-01..07), seed_fixture.sql
```

## Етапи (див. TZ.md §11)

- **Е0** — каркас: compose, схема БД, TMDB-клієнт з кешем ✅
- **Е1** — MVP веб: каталог, фільтри, картка, «схоже», списки ✅ (перевірено аудитом 09.11.2026)
- **Е2** — семантика (Ollama) + multi-seed + Telegram-бот + uk/en — реалізовано; Ollama-семантика потребує живого Ollama (зараз вимкнено), бот — живого Telegram-токена
- **Е3** — скрапери (UA VOD, Кінопошук) + Jellyfin/Plex/Trakt — частково: sweet.tv/IA/YouTube провайдери live
- **Е4** — профілі, реєстрація, публічний запуск (HTTPS, webhook) — реєстрація/профілі live; публічний запуск не виконано

Статус оновлено за аудитом 09.11.2026 (docs/audits/2026-11-09/) — історичне «E2E 16/16» не є доказом поточної працездатності; актуальні гейти: backend 480, bot 149, lint/tsc/build green, e2e/md_audit_fixes.py 40/40 на fixture-стеку.

## Атрибуція

Дані про фільми — [TMDB](https://www.themoviedb.org), провайдери перегляду — JustWatch.
Цей продукт використовує API TMDB, але не схвалений і не сертифікований TMDB.

Інформація про фільми та серіали також надана [IMDb](https://www.imdb.com) через офіційні
[IMDb datasets](https://datasets.imdbws.com) (умова використання — ця атрибуція).
Безкоштовні фільми — [Internet Archive](https://archive.org) (public domain).
Каталог аніме/манги — [AniList](https://anilist.co), [MyAnimeList](https://myanimelist.net), [Shikimori](https://shikimori.one).
UA-провайдер — [sweet.tv](https://sweet.tv) (публічний каталог, robots.txt дозволяє).
