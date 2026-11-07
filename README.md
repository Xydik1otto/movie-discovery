# MovieDiscovery (робоча назва)

Сервіс підбору фільмів, серіалів і телешоу: пошук за параметрами, гібридна схожість
(метадані + семантика описів), добірка «схоже на: [список еталонів]», вільний текстовий запит.
Веб + повнофункціональний Telegram-бот. Українською з перемикачем на англійську.

## Стек

- **Backend:** ASP.NET Core 8 (C#), EF Core, PostgreSQL 16 + pgvector
- **Frontend:** Next.js 15 + TypeScript + Tailwind
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
# 3. Frontend
cd frontend && npm install && npm run dev                    # :3001
# 4. Бот (опційно, потрібен TG_BOT_TOKEN)
cd bot && dotnet run
```

## Структура

```
TZ.md          — технічне завдання (етапи Е0-Е4, критерії приймання)
PLAN.md        — АВТОРИТЕТНИЙ API-контракт (схеми, ендпоінти, env)
backend/       — ASP.NET Core 8 (Api / Core / Infrastructure + тести)
frontend/      — Next.js 15 (uk/en, темна тема)
bot/           — Telegram-бот
docker-compose.yml, .env.example
```

## Етапи (див. TZ.md §11)

- **Е0** — каркас: compose, схема БД, TMDB-клієнт з кешем ← *ми тут*
- **Е1** — MVP веб: каталог, фільтри, картка, «схоже», списки
- **Е2** — семантика (Ollama) + multi-seed + Telegram-бот + uk/en
- **Е3** — скрапери (UA VOD, Кінопошук) + Jellyfin/Plex/Trakt
- **Е4** — профілі, реєстрація, публічний запуск (HTTPS, webhook)

## Атрибуція

Дані про фільми — [TMDB](https://www.themoviedb.org), провайдери перегляду — JustWatch.
Цей продукт використовує API TMDB, але не схвалений і не сертифікований TMDB.
