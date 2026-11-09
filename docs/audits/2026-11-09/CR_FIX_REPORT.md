# CR Fix Report — виправлення за CODEX_REVIEW.md (CR-01..06)

Дата: 09.11.2026. Виконавець: Hermes (лівий desktop-чат, llm-pi-ai/glm-5.3-flash).
Вхідна ревізія: `666fb16` (реалізація `b148bf8`). Результат: коміт **`de4b0cc`** (локальний, без push/deploy).

## Загальні гейти (прогнані після всіх змін)

| Гейт | Команда | Результат |
|---|---|---|
| Backend | `dotnet test backend/tests/MovieDiscovery.Tests` | **481/481 PASS** (480 + 1 новий CR-03-тест) |
| Bot | `dotnet test bot/tests` | **158/158 PASS** (149 + 9 нових CR-01/CR-06) |
| Frontend tsc | `npx tsc --noEmit` | чисто |
| Frontend lint | `npm run lint` | 0 errors (2 попередні warnings у sw.js) |
| Frontend build | `npm run build` | OK (17 сторінок) |
| Fixture cold setup | `docker compose -f docker-compose.test.yml -p moviediscovery-test down -v && up -d` + `e2e/seed_fixture.sql` | детерміновано: A=1, B=2/tmdb=1, C=3, D=4, E=5, F=6; guest profile 1 |
| API-регресія (cold) | `python e2e/md_audit_fixes.py` на свіжій БД | **46/46 PASS, 0 failed** (лог `docs/audits/2026-11-09/cr-fix-md-runner.log`) |
| Браузерний E2E | `node e2e/browser_e2e.js` на web :3201 | **14/14 PASS** (вкл. CR-02/03/04/06 регресії) |
| Security probe | `security_probe.js` (ізольовано) | **3/3 PASS** (CSRF cross-origin POST заблокований; PWA offline-фолбек працює) |
| Bot identity | Telegram `getMe` за токеном з .env | `ok=True username=for_movie_tinder_bot` — **@for_movie_tinder_bot підтверджений як цільовий бот** (токен не друкувався, другий polling НЕ запускався, prod-бот не зупинявся) |

## CR-01 — P1. BOT_API_KEY у compose + fail-closed — **FIXED**

- `docker-compose.yml`: `BOT_API_KEY: ${BOT_API_KEY:-}` додано в environment **api** і **bot** (один спільний ключ). `docker compose config` верифікував: `api forwarded=True len=48`, `bot forwarded=True len=48` (значення не друкувалось).
- `.env`: згенеровано BOT_API_KEY (48 hex, через scratch-скрипт; значення в чат не виводилось). Порожній ключ у .env більше не дає «тихого» legacy-фолбеку після ребілда.
- **Fail-closed у боті**: `bot/Api/ApiClient.cs` — без налаштованого ключа персональні операції (`GetListsAsync`, `AddToListAsync`) повертають `Failure(null, "bot_key_not_configured")` **локально, без HTTP-виклику** (інакше API обробив би їх як анонімні і записав у спільний guest profile). Пошук/схожість лишаються анонімно-працездатними. `BotUpdateHandler` мапить помилку на чесне повідомлення юзеру (uk/en: «Персональні списки недоступні: бот не налаштований… ваші дані не записано»); `Program.cs` і `bot/README.md` оновлені.
- **Regression на deployment config** (як вимагав рев'ю): `e2e/md_audit_fixes.py` парсить docker-compose.yml і перевіряє пересилання ключа в ОБИДВА сервіси (2 чеки: «CR-01 compose passes BOT_API_KEY to api/bot» — PASS).

## CR-02 — P1. Fallback підміняв internal ID на TMDB — **FIXED (fallback видалено)**

- `frontend/lib/api.ts similarBySeedsEx`: legacy POST `{tmdbIds}` fallback **видалено**. Тепер: 404 `titles_not_found` → чесна помилка (як було); 404/405 без цього коду → `ApiError` з кодом `similar_by_seeds_get_unsupported` («Бекенд не підтримує GET by-seeds — оновіть бекенд») — жодної підміни еталона, жодної втрати filters.
- Вибір «видалити» замість typed external refs: у каталозі 2.86 млн записів tmdbId є лише в частини — надійного типізованого мапінгу internal→tmdb для всіх seed'ів не існує; хибний результат гірший за чесну помилку.
- `similarBySeeds` (POST) лишено в api.ts — ним користується лише demo-fallback (`lib/fallback.ts`), це не живий шлях.
- **Regression (браузер)**: `e2e/browser_e2e.js` перехоплює GET by-seeds → 405 і перевіряє, що (а) POST by-seeds НЕ виконується жодного разу, (б) UI показує чесну помилку («Не вдалося знайти схоже»/«Помилка»), а не фейкові результати. Скріншот `cr-fix/cr02-no-fallback.png`. PASS ×2.

## CR-03 — P1. Rejected entries стирались як успішні — **FIXED**

- Backend `UserListService.MigrateGuestAsync`: нова відповідь `{moved, skipped, entries:[{titleId, kind, status}]}`, де `skipped` = **лише** справжні дублікати (alreadyExisting), а невалідні записи — окремо: `invalid_kind` / `invalid_rating` / `unknown_title`. Контракт задокументовано в коді.
- `ListsController.MigrateGuest` повертає per-entry статуси.
- Frontend `app/lists/page.tsx doMigrate`: знімок чиститься **тільки по підтверджених** записах (`moved|duplicate`) через новий `removeGuestEntries()` (guestLists.ts) — видалення по парі `(kind,titleId)` ЗНАЧЕННЯМ, версійно-безпечне: entries, додані іншою вкладкою під час міграції, лишаються (source-ризик з рев'ю закритий). Rejected лишаються у знімку для retry; банер показує кількість і причину (i18n uk/en `migrate.rejected`).
- Тести: backend `Migrate_Guest_Is_Idempotent` (переписаний під новий контракт: missing title → `unknown_title`, НЕ skipped) + новий `Migrate_Guest_Rejects_Invalid_Kind_And_Rating_Separately`; runner `md_audit_fixes.py` — 4 нові CR-03 чеки (rejected unknown_title; skipped=2 лише duplicates; B ізольовано); браузерний E2E — гостьовий знімок з неіснуючим тайтлом 999999999: **rejected PRESERVED у localStorage**, підтверджені видалені, банер «Не вдалося перенести…» видно. Скріншот `cr-fix/cr03-migration-rejected.png`.

## CR-04 — P2. False success при відмові localStorage — **FIXED**

- `frontend/lib/listActions.ts`: `addGuestItem`/`rateGuestItem` повертають false при невдалому записі — тепер це `throw new GuestStorageError()`; `lists/page.tsx rate()` гостьовий шлях загорнутий у try/catch (банер), title-сторінка вже ловить catch. «Додано» більше НЕ показується, якщо запис не зберігся.
- Тести: браузерний E2E — localStorage.setItem примусово кидає QuotaExceededError на `md_guest_lists_v1`: клік «Хочу подивитись» → у localStorage ПУСТО + банер «Помилка» + кнопка НЕ переключилась на «✓ Додано». Скріншот `cr-fix/cr04-storage-error.png`.

## CR-05 — P2. Runner 39/40 на чистій БД — **FIXED (cold setup PASS)**

- `e2e/seed_fixture.sql`: id **закріплені явно** (1..6) через `OVERRIDING SYSTEM VALUE` + `setval`; TRUNCATE робить сід ідемпотентним; колізія MD-03 зафіксована `UPDATE titles SET tmdb_id=1 WHERE id=2`; guest profile 1 відтворюється сідом (знайдена й закрита пастка: `profiles.created_at` NOT NULL без дефолту — без неї INSERT мовчки падав і guest-шлях ставав недоступним).
- `e2e/md_audit_fixes.py`: **усі fixture IDs резолвляться за title_en через /api/search** (helper `tid(name)`, голосний fail якщо не знайдено) — жодної залежності від історії sequence. Всі харджкоди (2/3/5/6) замінені на A_ID..F_ID.
- **Cold verification**: down -v → up (свіжий volume) → seed → runner на порожній БД → **46/46 PASS, 0 failed**. Дефект тестових передумов з рев'ю відтворити неможливо — резолв не залежить від даних.
- Пастки зафіксовані в коментарях сіда і в скілі (auth-cookie `md_auth`, rate-limits, POST `types` як масив).

## CR-06 — P2. Бот-фільтри + SW prefix — **FIXED (в межах ТЗ)**

- **Бот country/runtime/status**: `SearchFilters` + `Country/MinRuntime/MaxRuntime/Status`; `BuildSearchUrl` надсилає `country/minRuntime/maxRuntime/status` (бекенд /api/search ці параметри ВЖЕ підтримує — паритет з web FR-19 без змін бекенду). `FilterDraft` + поля й toggle-семантика (повторний клік скидає); `Keyboards.FilterBuilder` — рядки країн (UA/US/GB/KR/JP), тривалість (<60 / 60–120 / 120+ хв), статус (airing/finished — реальні значення БД); summary у тексті будівника; i18n uk/en.
- Тести: новий `bot/tests/CR06FilterParityTests.cs` (мапінг ToApiFilters, наявність і маркування нових кнопок) + `ApiClientTests` (URL містить/не містить нових параметрів). **MD-06 паритет фільтрів тепер доведений тестами** (живий Telegram round-trip — блокер, див. нижче).
- **SW activate**: `frontend/public/sw.js` видаляє в activate **лише** кеші префікса `mvdc-*` (окрім поточного) — чужі кеші origin недоторкані.
- Тест (браузер, реальний lifecycle): створювались `other-app-v1` + застарілий `mvdc-v0`, оновлення SW симулювалось новим scriptURL (`?update=` — той самий scope, новий байт-код; знайдена пастка: unregister+register з ТОЖ САМИМ URL не тригерить install/activate у Chromium) → після activated: `mvdc-v0` видалений, `other-app-v1` живий. PASS.

## Решта partial-gates — ізольовані проби й чесні блокери

| Гейт | Стан | Доказ/блокер |
|---|---|---|
| CSRF (cross-origin mutation) | **перевірено ізольовано, PASS** | JSON POST з чужого origin (:3999) до :3201 не долає API (CORS preflight блокує; додатковий шар — SameSite=Lax). Проба `security_probe.js` 3/3 PASS |
| PWA offline | **перевірено ізольовано, PASS** | SW активний → `context.setOffline(true)` → навігація віддає закешовану /offline сторінку |
| Secure-cookie / TLS | **unverified** | потрібен staging з HTTPS (fixture — HTTP; прапорець Secure не можна перевірити без TLS-термінації). Блокер: TLS-середовище |
| Trusted-forwarder / IP rate-limit ізоляція | **unverified** | потребує reverse-proxy конфігурації перед API — поза fixture; чесно зафіксовано |
| Live Telegram round-trip | **unverified** | заборонено ТЗ (не запускати другий polling; prod-бот працює). Код бота покритий 158 тестами + getMe підтверджує конфігурацію (@for_movie_tinder_bot) |
| Ollama семантика | **unverified** | сервіс вимкнений у середовищі; metadata fallback покритий юніт-тестами |
| NFR-2 (≥50k тайтлів з metadata+embeddings) | **НЕ виконано — зафіксовано числами** | читання-only prod БД: titles_total=2 863 652, з overview (>50 символів)=**0**, embeddings=**0**. Потрібен окремий metadata/embeddings-pass (не в межах CR) |

## Зміни/коміти

- **`de4b0cc`** — усі CR-01..06 виправлення + регресії (23 файли: backend×3, bot×9, frontend×8, e2e×3) + артефакти (CODEX_REVIEW.md і codex-review/ чужі докази збережено й закоммічено; cr-fix/ скріншоти).
- Push/deploy НЕ виконувались (за ТЗ). Prod moviediscovery-* на старих образах — не чіпались.

## Resource registry + cleanup (лише власні ресурси)

| Ресурс | ID | Дія |
|---|---|---|
| Fixture-стек moviediscovery-test (db :5434, api :5300, web :3201) | compose project `moviediscovery-test` | `down -v --remove-orphans` ПІСЛЯ всіх прогонів (volume fixture-БД знищено разом з тест-акаунтами) |
| Playwright + chromium | `C:/Users/bigpi/AppData/Local/hermes/cache/scratch/pw-e2e` (поза репо) | залишено (scratch автоочистка 24 год; потрібен для повторних прогонів раннера) |
| Scratch-скрипти | `scratch/add_bot_key.py`, `md_audit_fixes_new.py`, `browser_e2e_new.js`, `probe_auth.py`, `sw_probe.js` | scratch (автоочистка) |
| Лог прогону | `docs/audits/2026-11-09/cr-fix-md-runner.log` | збережено як доказ |
| Prod moviediscovery-*, чужі scb-*, .env (окрім додавання BOT_API_KEY), data/pg | — | НЕ чіпались |

## Висновок

Усі шість CR-знахідок виправлені з регресіями на трьох рівнях (юніт/інтеграція, HTTP-матриця на fixture, реальний браузер). Cold setup відтворюваний (46/46 на свіжій БД). Зауваження рев'ю «не приймати все як завершене» відпрацьоване: P1-дефекти закриті, partial-гейти або перевірені ізольовано, або чесно зафіксовані з точними блокерами. Production rebuild + застосування міграції bot_identities — за рішенням власника.
