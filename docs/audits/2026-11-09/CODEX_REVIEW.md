# Незалежна перевірка результату Hermes

Дата: 09.11.2026. Перевірена ревізія: `666fb16` (реалізація `b148bf8`).

## Висновок

Hermes **завершив роботу, не завис**. У лівому desktop-чаті є фінальна відповідь, два локальні коміти й звіти; робоча копія до цього рев'ю була чистою. Виправлення реальні: незалежно підтверджені тести, API-ізоляція акаунтів і виправлений головний браузерний сценарій схожості. Прийняти все MD-01–10 як завершене **не можна**: нижче залишкові дефекти та неповні вимоги.

Production не оновлювався: moviediscovery-* працюють на старих образах. Перевірка нового коду проведена в окремому fixture environment; user DB/data/pg не використовувалася для записів.

## Незалежно виконані перевірки

| Перевірка | Результат |
|---|---|
| dotnet test backend/tests/MovieDiscovery.Tests/MovieDiscovery.Tests.csproj --no-restore | **480/480 PASS**, 0 skipped; EF Relational 8.0.11/8.0.33 MSB3277 warning і 4 nullable warnings |
| dotnet test bot/tests/Bot.Tests.csproj --no-restore | **149/149 PASS**, 0 skipped |
| npm run lint; npx tsc --noEmit у frontend | PASS; лише 2 попередні SW unused-variable warnings |
| Docker production builds для api/web | PASS; API publish успішний, frontend build layer із валідного Docker cache |
| Новий ізольований Compose project codex-md-review-20261109 | Нові db/api/web, порти 5434/5300/3201, власна disposable БД |
| e2e/seed_fixture.sql → e2e/md_audit_fixes.py | **39 PASS / 1 FAIL**, exit 1; причина — помилкові hardcoded IDs тесту, див. CR-05 |
| Live browser на :3201 | Головна → Схоже на… → Collision Target A (tmdbId=null) → Знайти схоже: **2 рекомендації**, немає «Помилка», жанрові причини українською |
| git diff --check | PASS |

API smoke незалежно підтвердив основний ownership fix: anonymous GET чужого профілю →403, anonymous PATCH/DELETE чужого entry →404, A/B isolation, profiles anonymous→401. Онбординг primitive arrays, null safety, ≤3 favorites, internal-ID GET та bot-key isolation пройшли на PostgreSQL fixture. Повний аудит всіх 75 змінених файлів не заявляється.

## Залишкові знахідки

### CR-01 — P1. Основний Docker Compose не передає BOT_API_KEY

`docker-compose.yml:26` і `:83` не містять нового BOT_API_KEY у environment api/bot. Новий код читає ключ у bot/BotConfig.cs:18 та API Program.MapEnvDefaults. Наявність рядка в .env **не передає його автоматично** в контейнер, якщо його не вказано в Compose environment/env_file.

Доказ: розібраний `docker compose config --format json` показав `BOT_API_KEY_forwarded=false` для обох services. Без ключа бот продовжує роботу і не надсилає X-Bot-Key; API обробляє запити списків як legacy guest. Таким чином звичайний rebuild не активує обіцяне розділення Telegram users і може лишити їх у спільному guest profile.

Потрібно: передати один ключ api і bot через Compose, описати конфігурацію та fail-closed поведінку відсутнього ключа для персональних операцій. Regression має перевіряти deployment config, а не лише ApiClient з вручну заданим ключем. Секрет у рев'ю не читався/не змінювався.

### CR-02 — P1. Compatibility fallback досі підміняє internal ID на TMDB ID

`frontend/lib/api.ts:230–231`: після GET 404/405 викликається `similarBySeeds(internalIds, ...)`, який надсилає їх у `{tmdbIds}`. Legacy POST резолвить tmdb-first. Коментар про коректність змішаного resolver не враховує колізії; filters при цьому також губляться.

Незалежний probe виконав **реальну функцію API-клієнта**, замінивши тільки відповідь GET на 405; POST виконувався на реальному fixture API. При A.id=1 і B.tmdb_id=1, запросі internal seed 1 + science fiction: прямий GET повернув IDs **[3,5]**, fallback POST — **[4]** (драматичний еталон B). Доказ: codex-review/fallback-probe.json.

Потрібно: не виконувати неоднозначний fallback без typed external refs. Або прибрати legacy fallback із ясною unsupported-version помилкою, або передавати справжні external IDs/media type та зберігати/чесно відхиляти filters. Додати тест колізії саме на клієнтському fallback.

### CR-03 — P1. Відхилені migration entries стираються як успішні

`backend/src/MovieDiscovery.Infrastructure/Services/UserListService.cs:172–174` додає до одного `skipped` і дублікати, і невалідні/невідомі тайтли. `frontend/app/lists/page.tsx:223–227` трактує moved+skipped==snapshot.length як повне підтвердження, ставить marker і видаляє **весь** snapshot.

Відтворено на disposable PostgreSQL: snapshot з titleId=999999999 → HTTP200 `{moved:0, skipped:1}`. Це задовольняє умову очищення клієнта, хоча запис не був перенесений і не існує в акаунті. Доказ: codex-review/migration-probe.json. У реальних user lists нічого не видалялося.

Потрібно: відокремити alreadyExisting від rejected із per-entry результатом; видаляти зі snapshot тільки підтверджені записи, залишати rejected для retry/експорту й показувати причину. Також clearGuestSnapshot без порівняння версії може стерти нові guest entries, додані іншою вкладкою під час міграції; це source-level ризик, не окремий live-repro.

### CR-04 — P2. Відмова localStorage маскується успіхом дії

`frontend/lib/listActions.ts:31–32,46–47` ігнорує boolean addGuestItem/rateGuestItem та завжди повертає `guest`. На картці тайтла це дозволяє показати «додано» навіть при quota/privacy failure. Банер storageAvailable на /lists не захищає кожен запис і не виявляє всі quota errors.

Probe виконав поточні guestLists.ts і listActions.ts через TypeScript transpile з localStorage.setItem, який кидає quota error: **directWrite=false; actionResult="guest"; stored=0**. Потрібно передавати відмову нагору і не показувати успіх незбережених дій. Перевірити write failure, а не лише повну відсутність localStorage.

### CR-05 — P2. Регресійний runner не відтворює заявлені 40/40 на чистій БД

Канонічний e2e/seed_fixture.sql на щойно створеній БД дав A.id=1, B.id=2/TmdbId=1, Drama Quiet D.id=4. `e2e/md_audit_fixes.py:162–168` натомість запитує tmdbIds=[2] і вимагає Drama Quiet D.id=5. Реальна відповідь містить [4], тож перевірка завершується FAIL. Отримані **39/40** не є доведеним зламом продуктивної схожості: це дефект тестових передумов.

Потрібно резолвити fixture IDs за стабільними keys/назвами або задавати їх явно SQL seed, а не залежати від історії sequence старого тестового volume. Повторити cold setup і runner на порожній БД.

### CR-06 — P2. MD-06 та частина PWA/security вимог залишились неповними

Bot SearchFilters (`bot/Api/ApiModels.cs:50–60`) має type, genres, yearFrom/yearTo, minRating; відсутні country/runtime/status, прямо вказані в MD-06. Тому «повний паритет фільтрів» не доведено й не реалізовано повністю.

SW activate (`frontend/public/sw.js:25`) досі видаляє всі кеші origin, крім власного поточного, замість лише свого prefix. HTTPS Secure-cookie/CSRF/trusted-forwarder gate, встановлення/offline PWA, повний responsive matrix, live Telegram, Ollama і NFR-2 лишаються unverified або partial за самими звітами Hermes. Це не підстава оголошувати все ТЗ виконаним.

## Рекомендований наступний крок

Повернути Hermes CR-01–05 на доопрацювання з конкретними regression tests; MD-06 і решту partial gates позначити чесно. Не робити production rebuild лише на підставі попередніх зелених тестів. Кодова база вже суттєво покращена, але суцільне «виконано повністю» відхилено.

Це рев'ю **не вносить виправлень у код і не надсилає нове завдання Hermes**. Нові артефакти: цей звіт, codex-review JSON probes/resource registry та cleanup report. User сервіси не зупинялися, deployment/міграції на user DB не виконувалися.

## Environment Cleanup Report

Після завершення всіх перевірок та збереження цього звіту cleanup виконується лише для project `codex-md-review-20261109`. Точні IDs/mounts записані в codex-review/resource-registry.json; фактичний результат — codex-review/CLEANUP.md. Основний moviediscovery-* та сторонні scb-* контейнери захищені й не належать цьому рев'ю.
