# Addendum до HERMES_IMPLEMENTATION_REPORT — незалежна верифікація (другий виконавчий канал)

Дата: 09.11.2026. Цей додаток — результат незалежної верифікаційної лінії (другий чат/сесія Hermes,
модель llm-pi-ai/glm-5.3-flash), яка працювала паралельно з основним виконавцем над тим самим ТЗ
(FIX_SPEC.md MD-01..10) у спільному робочому дереві `G:/Projects/movie-discovery`.

## Роль і внесок цієї лінії

1. **MD-01 (P0), початкова реалізація**: `GuestProfileService` (designated guest profile через
   `GUEST_PROFILE_ID`, профіль, приріплений до акаунта, гостьовим не рахується; без кешу —
   перевірка власника на кожному запиті), guest-гейт у `ListsController` (ResolveProfileAsync /
   PUT / DELETE — чужий запис → 404 без розкриття), `[Authorize]`-rewrite `ProfilesController`,
   DI + `partial class Program` (точка WAF) у `Program.cs`.
2. **Каркас регресії MD-10**: `docker-compose.test.yml` (ізольований стек, порти 5300/3201/5434,
   власний volume — НЕ ./data/pg), первинні `docs/audits/2026-11-09/e2e-hermes/{seed_fixtures.sql,
   api_matrix.py}` (далі доопрацьовані обома лініями).
3. **Незалежна верифікація** (нижче) — запуски, яких основний звіт не містить.

## Гейти, прогнані цією лінією (після злиття обох ліній у робочому дереві)

| Гейт | Команда | Результат |
|---|---|---|
| Backend tests | `dotnet test backend/tests/MovieDiscovery.Tests` | **480/480 PASS** (≈5s) |
| Bot tests | `dotnet test bot/tests` | **149/149 PASS** |
| Frontend tsc | `npx tsc --noEmit` | чисто |
| Fixture-стек | `docker compose -f docker-compose.test.yml -p moviediscovery-test up -d --build` | db/api/web Up, health `{"status":"ok","db":true}` |
| API-матриця (їхня) | `python e2e/md_audit_fixes.py` — **повторний прогін** | **40/40 PASS** (відтворювано) |
| API-матриця (моя, незалежна) | `python docs/audits/2026-11-09/e2e-hermes/api_matrix.py` | **52/52 PASS, exit 0** |
| Браузерний E2E (Playwright) | `node e2e/browser_e2e.js` на web :3201 | **6/6 PASS** |

## Браузерний Playwright-раннер — прогалина з основного звіту ЗАКРИТА

Основний звіт позначав Playwright-runner як «не додано (немає встановлених браузерів)».
Ця лінія встановила Playwright 1.65 + chromium в ІЗОЛЬОВАНІЙ директорії (scratch, не в repo —
щоб не конфліктувати за `frontend/package.json`) і додала раннер у репозиторій:

- `e2e/browser_e2e.js` — 6 чеків: home loads; MD-03 `/similar?seed=4` (внутрішній id,
  tmdb NULL) рендерить результати; MD-03 швидкий режим на головній (пошук → підказка →
  «Знайти схоже» → результати — репро-кейс аудиту «ввів фільм — і все» виправлено й
  підтверджено живим браузером); MD-02 register→onboarding→skip завершується лише після
  серверного збереження; MD-08 мобільний 390px без горизонтального overflow; відсутність
  uncaught JS-помилок.
- `e2e/browser_e2e.README.md` — встановлення/запуск/пастки.
- Скріншоти-докази: `docs/audits/2026-11-09/e2e-hermes/shots/{home-similar-fixed,
  similar-internal-id, onboarding-done, mobile-390}.png`.

## Виправлення в e2e-артефактах (обґрунтування)

`api_matrix.py` у початковому вигляді (мій) мав три дефекти, знайдені під час живого прогону
і виправлені:

1. **Ім'я auth-cookie**: тест-стек використовує `md_auth` (з чанкінгом `.C1/.C2`), а не
   `.AspNetCore.Cookies` — перевірки сесії ніколи не спрацьовували б; виправлено на
   name-префікс + реальний probe `/api/auth/me`.
2. **Rate-limit 3/год/IP** на register (429 з ПОРОЖНІМ тілом) і вікно на login — повторні
   прогони тепер логіняться в акаунти попередніх прогонів (fallback-ланцюги з відомими
   паролями), неіснуючі email не пробуються (щоб не витрачати brute-force-ліміт).
3. **POST `/api/similar/by-seeds`**: `types` має бути `List<string>`; рядок `"movie"` → 400.
   Крім того, «GET/POST еквівалентні» — хибний критерій на колізійній парі: за контрактом
   GET ids=2 сіє A (sci-fi), POST tmdbIds=2 сіє B (drama) — набори СВІДОМО різні; критерій
   переписано (обидва 200, непорожні, кожен без власного сіда).

`seed_fixtures.sql` первинно посилався на неіснуючу таблицю `list_entries` (реальна —
`lists`) і на іншу схему id — переписаний як історичний покажчик на канонічний
`e2e/seed_fixture.sql`.

## Зіклін-спостереження між лініями (факти, без оцінок)

- Основний звіт підписаний «Виконавець: Hermes (kanban t_cf58bb8d)»; ця лінія працювала в
  окремому чаті й OpenRouter/канбан не запускала. Обидві лінії редагували спільні файли
  (ListsController, тести, api_matrix) — конфліктів не виникло; семантика 403/404
  узгоджена обома.
- Тестові акаунти попередніх прогонів (`own-a@t.ua`, `own-b@t.ua`, `onb@t.ua`,
  `mdfix_*`) існують у fixture-БД і використовуються fallback-логінами.
- Перший прогін моєї матриці ДО фіксів анонімно створив запис у guest-профілі (fixture-БД)
  — це fixture-дані, не production; зникають разом з volume при cleanup.

## Підсумок стану

- P0/P1 (MD-01..05) — закриті й підтверджені ТРЕМА незалежними шляхами: юніт/інтеграційні
  тести (WAF), HTTP-матриця на fixture-стеку (2 різні скрипти), живий браузер (Playwright).
- MD-06/07 — реалізовані/частково; залишкові unverified пункти — ті самі, що в основному
  звіті (live Telegram, TLS/CSRF staging, NFR-2 заповненість embeddings).
- Код зафіксований комітом b148bf8 (+ follow-up цієї лінії: виправлення e2e-артефактів,
  браузерний раннер, скріншоти, цей додаток). Push/deploy не виконувались (за ТЗ).
