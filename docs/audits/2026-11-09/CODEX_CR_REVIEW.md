# Незалежне приймання CR-01–06 після Hermes

Дата: 09.11.2026. Перевірена ревізія **f336082**, реалізація **de4b0cc**; база порівняння 666fb16. Робоча копія на початку перевірки була чистою. Код продукту цим рев'ю не змінювався.

## Висновок

Hermes завершив доопрацювання, не завис. Це підтверджено лівим desktop-чатом, двома новими локальними комітами та CR_FIX_REPORT.md. **CR-01/02/04/05 і конкретні доповнення CR-06 підтверджені; CR-03 залишається частковим.** Підстав прийняти всі шість зауважень без залишків немає: незалежні probes виявили два шляхи втрати гостьових змін у міграції.

Основний moviediscovery-* стек працює на попередніх образах; рев'ю його не оновлювало і не запускало міграції на user DB. Нова версія перевірялася на власному Compose project **codex-cr-review-20261109**, порти 5434/5300/3201, disposable PostgreSQL.

## Незалежно виконані перевірки

| Перевірка | Результат |
|---|---|
| Backend dotnet test --no-restore | **481/481 PASS**, 0 skipped; лишилися EF Relational 8.0.11/8.0.33 MSB3277, nullable та xUnit2031 warnings |
| Bot dotnet test --no-restore | **158/158 PASS**, 0 skipped |
| Frontend npm run lint + npx tsc --noEmit | PASS; 2 попередні unused catch-variable warnings у SW |
| Docker api/web builds | PASS; API publish успішний, frontend production build з валідного Docker layer cache |
| Cold PostgreSQL seed із ON_ERROR_STOP | PASS; перевірено Compose ownership до TRUNCATE; seed тільки у власній fixture DB |
| python e2e/md_audit_fixes.py http://localhost:5300 | **46 PASS, 0 FAIL**; [повний лог](codex-cr-review/api-runner.log) |
| Module probes з фактичного TS/TSX/SW source | CR-02/04/SW — PASS; 2 CR-03 defects відтворено; [JSON](codex-cr-review/module-probes.json) |
| Браузер :3201/similar?seed=1 | Collision Target A без TMDB ID → Space Heist C / Void Blade E з українськими reasons |
| Браузер guest flow | Space Heist C → «Хочу подивитись» → «Додано» → /lists містить Space Heist C |
| docker compose config, без друку секретів | api і bot отримують непорожній однаковий BOT_API_KEY |
| git diff --check | PASS |

API runner реально перевірив ownership, онбординг, internal-ID/legacy namespaces, per-entry migration, Telegram user isolation та Compose key forwarding. Перше виведення хвоста логу через Python wrapper впало лише на кодуванні stdout cp1251 символу «→»; сам runner уже завершився, а повний UTF-8 лог був збережений. Його результат 46/46 прочитаний без повторного запуску.

Заявлений Hermes browser runner **14/14** і security probe **3/3** у цьому рев'ю цілком повторно не запускалися. Незалежні browser smoke та module/API probes вище не видаються за повне повторення цих двох suite.

## Статус кожного CR

- **CR-01 — прийнято в межах коду/конфігурації.** Compose тепер передає ключ обом services; bot GetListsAsync/AddToListAsync без ключа локально повертають bot_key_not_configured без HTTP. Нові unit tests пройшли. Не тестував production polling і не перезапускав робочий @for_movie_tinder_bot; його username підтверджував Hermes через getMe, а не цей reviewer.
- **CR-02 — прийнято.** Реальна similarBySeedsEx під примусовим GET405 виконує тільки один GET і кидає similar_by_seeds_get_unsupported; неоднозначний POST більше не надсилається. Нормальна internal-ID схожість підтверджена API й браузером.
- **CR-03 — частково.** Новий backend відділяє moved/duplicate від unknown_title/invalid_kind/invalid_rating, API regression це підтверджує. Новий frontend зберігає rejected при новому per-entry API. Однак legacy fallback і зміна того самого запису іншою вкладкою залишилися небезпечними — нижче.
- **CR-04 — прийнято для add/rate.** Probe current listActions+guestLists із setItem quota failure тепер отримує GuestStorageError замість guest/успіх. Нові try/catch у rate та попередній title catch показують помилку.
- **CR-05 — прийнято.** Повний cold seed і 46/46 runner відтворилися на щойно створеній PostgreSQL; pinned IDs і name lookup усувають попередні sequence-передумови.
- **CR-06 — конкретні доповнення прийнято.** Country/runtime/status присутні у draft, builder, callback handler, API query та tests; 158 bot tests green. Виконаний фактичний SW activate handler із cache keys mvdc-v1/mvdc-v0/other-app-v1 видалив тільки mvdc-v0. Це не є сертифікацією всіх можливих web/bot filters або всього PWA/TLS/Telegram стеку.

## Залишок CR-03 / P1: legacy API знову стирає rejected

**frontend/app/lists/page.tsx, гілка `entries.length === 0 && moved + skipped === snap.length`** досі викликає setMigrationMarker і clearGuestSnapshot. Старий API, як уже доведено попереднім рев'ю, включає unknown/invalid records до skipped. Отже без entries немає доказу, що ці записи перенесено чи що вони дублікати.

Незалежний probe взяв **поточну async doMigrate із TSX через TypeScript AST**, виконав її з реальним guestLists storage і legacy-відповіддю `{moved:0, skipped:1}` для snapshot з titleId=999999999. Результат: **remaining=[]; migrated=true**. Старий шлях втрати даних відтворився; це не просто припущення з коментарів.

Потрібно: при відсутніх per-entry результатах не очищати snapshot і не ставити completion marker. Повертати ясну помилку несумісної версії API або перевіряти кожен запис незалежно. Regression: новий frontend + legacy API, який відхилив запис → snapshot і retry збережені.

## Залишок CR-03 / P1: очищення не є версійно-безпечним для того самого запису

**frontend/lib/guestLists.ts removeGuestEntries** будує drop тільки за `(kind,titleId)`. Новий `dropConfirmed` у lists/page.tsx передає лише ці ключі, без версії/значення саме надісланого snapshot. Нові інші IDs зберігаються, але зміни того самого тайтла — ні.

Probe фактичного guestLists.ts:

1. Запис watched/titleId=1/rating=7 входить у submitted snapshot.
2. Поки запит триває, інша вкладка змінює його рейтинг на 9.
3. Приходить підтвердження старого snapshot; removeGuestEntries([{kind:'watched',titleId:1}]) видаляє поточний rating=9.

Результат **remaining=[]**, хоча сервер підтвердив лише старе значення. Таким самим способом можна стерти повторно створений запис із тим самим ключем. Твердження «version-safe snapshot cleanup» у CR_FIX_REPORT неточне.

Потрібно: передавати підтверджені записи разом із оригінальним submitted значенням/версією; видаляти лише якщо поточний запис збігається з ним. Нові/оновлені значення лишаються для наступної явної міграції. Regression має моделювати не тільки новий інший ID, але й rating/edit/remove+readd того самого ID.

## Додаткові межі висновку

TLS Secure-cookie, повна trusted-proxy/IP-ізоляція, live Telegram round-trip, installed PWA та Ollama/NFR-2 залишаються окремими unverified gates. Три сторонні security probes не є повним security review. Production deploy не входив у цю перевірку.

Доопрацювання доцільно обмежити двома залишками CR-03 та їхніми regression tests, після чого повторити 481+158 та cold API matrix. Інші підтверджені виправлення зберегти. Нове повідомлення Hermes цим рев'ю не надсилалося.

## Environment Cleanup Report

Після всіх перевірок і збереження цього звіту прибирається тільки власний codex-cr-review-20261109. IDs і mounts: [resource-registry.json](codex-cr-review/resource-registry.json). Фактичний фінальний результат: [CLEANUP.md](codex-cr-review/CLEANUP.md). User moviediscovery-* та сторонні scb-* ресурси не належать цьому рев'ю і не зупиняються.
