# Звіт виконання виправного ТЗ MovieDiscovery (MD-01–10)

Виконавець: Hermes (kanban t_cf58bb8d). Дата: 09.11.2026. Базова ревізія: `196b62d096077fab8691d3d19f881d19d86ccdd3`.
Обсяг: docs/audits/2026-11-09/FIX_SPEC.md. Жодних push/deploy/production-міграцій не виконано.

## Гейти верифікації (виконані оркестратором, не субагентами)

| Гейт | Команда | Результат |
|---|---|---|
| Backend tests | `dotnet test backend/tests/MovieDiscovery.Tests` | **480/480 passed** (419 попередніх + 61 новий; 1 тимчасовий probe-тест видалено) |
| Bot tests | `dotnet test bot` | **149/149 passed** (111 попередніх + 38 нових) |
| Frontend lint | `npm run lint` | 0 errors (2 попередні warning у public/sw.js) |
| Frontend tsc | `npx tsc --noEmit` | чисто |
| Frontend build | `npm run build` | 17/17 static pages |
| API-регресія на fixture | `python e2e/md_audit_fixes.py` (fixture-стек docker-compose.test.yml, api :5300) | **40/40 PASS**, exit 0; лог `docs/audits/2026-11-09/e2e-md-fixes.log` |
| Браузерна верифікація | ручний прогін на fixture web :3201 (власний ресурс, видалений після) | MD-02/03/04 живі докази нижче |

## MD-ID → результат → доказ

### MD-01 Ізоляція профілів і списків — **FIXED (P0)**
- `ListsController`: анонімні GET/POST обмежені designated guest profile (`GUEST_PROFILE_ID`, за замовчуванням 1; профіль, приріплений до акаунта, гостьовим не рахується — `GuestProfileService.cs`). Чужий існуючий profileId → 403 `forbidden`; невідомий → 404. PATCH/DELETE гостя — лише записи дозволеного профілю (чужі → 404, без розкриття). Авторизовані шляхи резолвлять профіль лише з identity.
- `ProfilesController`: анонімний GET → 401; auth → лише власний профіль; POST — auth-only (409 при наявному). Анонімне перелічення приватних профілів і довільне створення закриті.
- Бот-режим (MD-05): `X-Bot-Key`+`X-Telegram-User-Id` → власний профіль TG-юзера через `bot_identities` (міграція `20261109114109_BotIdentities` — **файл створено, на жодну БД не застосовано**); client profileId ігнорується.
- Докази: `backend/tests/MovieDiscovery.Tests/ListsOwnershipTests.cs`, `BotIdentityTests.cs` (WebApplicationFactory, InMemory); fixture e2e: анонім читає A → 403, анонім PATCH/DELETE чужий item → 404, B не бачить записів A, валідні власні операції проходять.

### MD-02 Контракт онбордингу — **FIXED (P1)**
- Backend `OnboardingController`: nullable-масиви (немає NRE/500 при відсутніх полях), favoriteTitles ≤3 + >0 + дедуп, picks/mood/era ≤20 непорожніх значень.
- Frontend `app/onboarding/page.tsx`: payload строго `{picks:[genreKeys], mood:[], era:[], favoriteTitles:[int], skipped}` — жанри тільки з poster-питань, без questionId; «Готово» лише після підтвердженого збереження; 401 → login next; 400/500/timeout/unavailable → вибір збережено + видимий retry (i18n uk/en).
- Докази: `OnboardingContractTests.cs` (WAF); fixture e2e: валідний payload → 200 + `me.onboardingDone=true`; `{}` без 500; 4 favorites → 400; браузерний прогін зафіксував реальний payload `{"picks":["action","science fiction",...],"mood":[...],"era":[],"favoriteTitles":[],"skipped":false}` → done-екран.

### MD-03 Однозначні ID й єдина схожість — **FIXED (P1)**
- `SimilarController`: GET `by-seeds?ids=` — **лише internal title id** (резолв строго по `titles.id`; невідомий → 404 `titles_not_found` + `missing[]`, без часткових результатів); POST `{tmdbIds}` — задокументований legacy подвійний резолв (tmdb-first). Колізійний контракт: `A.id == B.tmdb_id` → GET ids=A сіє A, POST tmdbIds=A сіє B.
- Frontend: home `/` більше не відкидає seeds без tmdbId (внутрішні id); TitleCard/титулка/`/similar` без tmdbId-гейтів; `similarBySeedsEx` НЕ маскує 404 `titles_not_found` фолбеком на POST (фолбек тільки route-missing 404/405).
- Докази: `SimilarNamespaceTests.cs` (включно з колізією A.id=B.tmdb_id); fixture e2e 40/40 (у т.ч. GET ids=2 → не повертає twin id=3 як сід; POST tmdbIds=2 → сіє drama-twin); **живий браузерний прогін**: сід «Collision Target A» (tmdbId=null) → «Знайти схоже» → результати з reasons, БЕЗ «Помилка» (репро-кейс аудиту відтворений і виправлений).

### MD-04 Гостьові дані та міграція — **FIXED (P1)**
- `frontend/lib/guestLists.ts`: versioned localStorage `md_guest_lists_v1` (5 kinds, internal titleId, rating, idempotent upsert), чесне повідомлення при недоступному storage; `lib/listActions.ts` — єдиний шлях мутацій (auth → API, guest → localStorage).
- `/lists` для гостя рендерить локальні дані (без серверних викликів), локальна статистика; банер міграції читає ЛИШЕ локальний snapshot; міграція за дією користувача, idempotent (бекенд skip-duplicates); маркер `md_guest_migrated:{userId}:{snapshotHash}` ставиться тільки при moved+skipped == snapshot, snapshot чиститься після підтвердження; часткова помилка → точна статистика + retry; `guardedGuestItems` (серверне читання «гостя» під час логіну) видалено.
- Конфлікт-правило зафіксовано в PLAN: існуючий запис акаунта перемагає.
- Докази: браузерний прогін на fixture — гість додав тайтл → localStorage запис → /lists показав після навігації; login A → банер «Перенести» → moved, маркер `md_guest_migrated:bde0948f…:7a2c079c`, snapshot очищено, тайтл у watchlist акаунта. Fixture e2e: migrate moved=2 → repeat moved=0/skipped=2 → B ізольовано.

### MD-05 Бот для всього каталогу і безпечний профіль — **FIXED (код), LIVE TELEGRAM UNVERIFIED**
- Бот: внутрішні title id у callbacks (`wl:{id}`/`wd:{id}`, rows з Id≤0 пропускаються), `POST /api/lists/{kind} {titleId}`, GET by-seeds з internal ids; `ProfileId=1` видалено; усі запити несуть `X-Bot-Key`+`X-Telegram-User-Id` (per-user профіль, cross-user ізоляція); stale/forged callbacks — контрольована відповідь; 64-byte ліміт покритий тестами; `TitleCard.TmdbId` nullable.
- Докази: bot tests 149/149 (`ApiClientTests`, `BotUpdateHandlerTests`, `KeyboardsTests` — заголовки на кожен запит, відсутність profileId, tmdbId=null flows); backend `BotIdentityTests`. **Живий Telegram round-trip не виконувався (заборонено ТЗ; потрібен тестовий токен) — див. Блокери.**

### MD-06 Паритет фільтрів бота (P2) — **FIXED (реалізовано; fixture-еквівалентність web/bot не ганялась на live)**
- `/find <назва>` інтерактивний білдер: type/genre (toggle)/year/rating, `fi:*` callbacks, go/reset/cancel, пагінація зберігає фільтри; `SearchAsync` розширений type/genres/yearFrom/yearTo/minRating; /help та uk/en відповідають реалізації. Предметна логіка — у backend (API query).

### MD-07 Auth, проксі, помилки, деградація — **PARTIAL (核心 fixed; окремі перевірки unverified)**
- `GET /api/health`: чесний `status: ok|degraded` (200 завжди); новий `GET /api/health/ready` — 200/503. MD-09-фікc: проба БД — дешевий запит з'єднання (COUNT по 2.8 млн давав ~2.6 с median на health — замір docs/audits/2026-11-09/perf-probe.json), titlesCount кеш 60 с; після фікса health на rebuilt fixture: 19–62 мс.
- Frontend: logout при недоступному API чесний (юзер лишається логіненим, повідомлення `auth.logoutUnavailable`), auth-flow деградація без фальшивих success.
- **Unverified**: Secure-cookie flags на HTTPS/staging, rejected foreign-origin mutation (CSRF), reverse-proxy trusted forwarder/IP rate-limit ізоляція — не проганялись (немає staging TLS-середовища в межах ТЗ); cookie login/register/logout через same-origin proxy покриті WAF-тестами (`ListsOwnershipTests`, `IdentityTests`) і fixture e2e (register/login/me/logout cookies).

### MD-08 Якість UI, PWA, дані — **PARTIAL**
- Reasons-жанри локалізовані (uk) — `lib/reasons.ts` (shared_genres:science fiction → «спільний жанр: фантастика»), невідомі ключі — raw-бейдж.
- SW-лifecycle: явний production-guard (`NODE_ENV`) у `ServiceWorkerRegister.tsx`; API GET/POST не кешуються (як було).
- Uk/en словники доповнені новими ключами; російської ніде немає.
- **Unverified**: PWA install/offline recovery, mobile-ширинний прогін 360/390/768/1440, чесність watch-лінків на конкретні тайтли (потребує живого каталогу з watch_links — на fixture не перевірялось); Ollama=false → metadata fallback метрик FR-5 немає (Ollama вимкнена).

### MD-09 Продуктивність і документація — **PARTIAL (заміри зроблені; NFR не сертифіковані)**
- Заміри read-only на живому стеку (2 863 652 titles, docs/audits/2026-11-09/perf-probe.json): search «Inception» median **24.3 мс** (p95 24.7; єдиний замір аудиту 7.6 с — cold/parallel артефакт), filtered search 25.7 мс, detail 5.1 мс, similar 22.9 мс — **в межах NFR-1 (≤1 с p95) на цьому середовищі**; health до фікса 2.6 с → після (структурна зміна) десятки мс.
- NFR-2 (≥50 000 тайтлів з описами і векторами) **не доведено**: 2.86 млн рядків каталогу ≠ заповнені metadata/embeddings; потрібен окремий аудит заповнення.
- Документація: README (етапи Е0–Е4 актуальні, порт dev = :3000/`-p 3001`, standalone, fixture-стек), PLAN.md (MD-03 namespaces, MD-04 guest storage/маркер/конфлікт, MD-01 profiles, MD-05 bot identity, health), TZ.md (розділ «Статус реалізації»), bot/README.md (переписаний). Індекси на реальній БД не встановлювались.

### MD-10 Відтворювана регресія — **FIXED (у межах можливого)**
- Додано: 61 backend-тест (WebApplicationFactory + InMemory: ownership-матриця guest/A/B на всіх verbs, onboarding-контракт, similar-namespace+колізія, bot identity, health), 38 bot-тести, fixture-стек `docker-compose.test.yml` (окремі порти 5300/3201/5434, власний volume, НЕ data/pg), `e2e/md_audit_fixes.py` (40 чеків, детермінований setup, exit≠0 при FAIL, толерантний до повторних прогонів) + `e2e/seed_fixture.sql`, `e2e/perf_probe.py`.
- Браузерний E2E runner: повноцінний Playwright-runner не додано (немає встановлених браузерів у середовищі виконання; браузерна верифікація виконана керованим браузером — докази вище). **Частково**: покриття «mobile/PWA offline» браузерними тестами відсутнє.
- Усі 419+111 попередніх тестів лишаються зеленими.

## Контрактні зміни (зафіксовані в PLAN.md)
1. GET `by-seeds?ids=` = internal-only; POST `tmdbIds` = legacy tmdb-first dual-resolve.
2. `/api/profiles`: 401 анонім; auth → own; POST auth-only (409 при наявному).
3. Гість: localStorage `md_guest_lists_v1`; designated guest profile серверно (`GUEST_PROFILE_ID`).
4. Бот: `X-Bot-Key`+`X-Telegram-User-Id` → власний профіль (`bot_identities`); profileId deprecated.
5. Health: чесний status + `/api/health/ready`.

## Міграції
- `20261109114109_BotIdentities` (таблиця `bot_identities`) — **файл у репозиторії; НЕ застосований до жодної БД**. Застосувати під час наступного планового деплою (`dotnet ef database update` або автоматика контейнера).

## Unverified external integrations та блокери
1. **Live Telegram** (MD-05/06): потрібен тестовий токен бота — в межах ТЗ заборонено.
2. **Ollama семантика** (MD-08): сервіс вимкнений у середовищі; metadata fallback покритий існуючими тестами, семантичні тести на ізольованому індексі не виконувались.
3. **TLS/CSRF/Proxy IP-ізоляція** (MD-07): немає staging HTTPS середовища.
4. **NFR-2** (MD-09): потрібна перевірка заповненості metadata/embeddings каталогу.
5. **Браузерний Playwright-runner** (MD-10): відсутні встановлені браузери/драйвери в середовищі; додати як окреме завдання (потрібен `npx playwright install`).
6. Зовнішній Docker-стек (:3001/:5200/:5433) працює на СТАРОМУ образі — щоб виправлення з'явились живцем, потрібен плановий rebuild власником (не виконувався за забороною).
7. Реєстраційний rate-limit 3/год/IP ускладнює повторні e2e-прогони (відома пастка; `e2e/md_audit_fixes.py` має fallback на login).

## Висновок
P0/P1 (MD-01, MD-02, MD-03, MD-04, MD-05) — закриті з регресійним покриттям і живими доказами на ізольованому fixture. P2: MD-06 реалізований, MD-07/MD-08/MD-09 — частково з явно переліченими unverified пунктами; MD-10 — покриття додано, browser-runner позначений як частковий. Жодних прихованих «success/demo» підмін не додано; недоступність зовнішніх сервісів чесно відображається в UI.
