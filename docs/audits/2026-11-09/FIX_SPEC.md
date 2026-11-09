# ТЗ на виправлення MovieDiscovery

Дата: 09.11.2026. Виконавець: Hermes. Початкова ревізія: 196b62d096077fab8691d3d19f881d19d86ccdd3.

## Мета й межі

Відновити узгоджені сценарії веба/бота та захист приватних даних, після чого надати відтворювані докази. Пріоритет: безпека списків → контракт онбордингу → єдині ID/схожість → гостьові списки → бот → повна регресія. Продуктові вимоги TZ.md і API-контракт PLAN.md зберігаються; розбіжності документації виправляються разом з реалізацією, без тихої відмови від функцій.

Аудит не є виправленням. Частина нижче — source-confirmed bugs, частина — ще неперевірені умови приймання. Не оголошувати останні доведеними дефектами без відтворення.

## Обов'язкові обмеження

- Спочатку перечитати актуальний Git diff та AGENTS.md. Зберегти чужі зміни. Не публікувати, не push/deploy і не чіпати production без окремої вказівки.
- Не зупиняти/не перезапускати moviediscovery-* та інші наявні сервіси. Docker недоступність і зовнішній запуск зафіксовані в аудиті; це не дозвіл керувати ними.
- Для інтеграційних тестів — окремі порти, ізольована порожня БД/Compose project і task-owned ресурси. Не використовувати data/pg як тестову БД. Не запускати імпорти мільйонів записів або справжній бот із production токеном.
- Не друкувати .env, токени, auth cookies, приватні списки. Не додавати секрети в коміти/звіти.
- Не виконувати build і dev/start одночасно з одним distDir. Облік ресурсів і фінальний ownership-aware cleanup обов'язкові.

## MD-01 — P0. Ізоляція профілів і списків

Доказ: backend/src/MovieDiscovery.Api/Controllers/ListsController.cs:20–33, 89–120, 213–269; ProfilesController.cs:13–28. Анонімний ResolveProfileAsync приймає довільний profileId; PATCH/DELETE шукають лише itemId. Авторизований userLists шлях перевірити окремо, не вважати автоматично вразливим.

Виправлення:

- Для акаунта виводити profileId тільки з authenticated user identity. Для guest — localStorage за MD-04; якщо legacy shared guest API потрібен, обмежити його явним designated guest profile і заборонити доступ до account profiles.
- На кожному GET/POST/PATCH/DELETE та імпорті/міграції включати ownership у серверний query. Не довіряти profileId/itemId з клієнта.
- /api/profiles не повинен розкривати назви/ID приватних акаунтів чи дозволяти анонімне довільне створення профілів. Визначити сумісний дозволений guest surface у PLAN.

Приймання: integration fixtures guest, A, B; anonymous/A не читають, не додають, не змінюють і не видаляють entries B навіть за відомими ID. Очікувані 401/403/404 узгоджені та не розкривають приватні дані. Валідні власні операції, рейтинги та міграція проходять. Перевірити IDOR на всіх HTTP verbs, зокрема legacy paths.

## MD-02 — P1. Контракт і правдивий стан онбордингу

Доказ: frontend/app/onboarding/page.tsx:113–136; frontend/lib/types.ts:233–238; backend/src/MovieDiscovery.Api/Controllers/OnboardingController.cs:18–37; PLAN.md FR-18.

Фактичний backend очікує `{picks:string[], mood:string[], era:string[], favoriteTitles:int[], skipped:bool}`. Клієнт надсилає picks/favorites як objects та era як scalar/null. Catch і unavailable гілки допускають success без збереження.

Виправлення: узгодити тип і mapper; genre picks повинні містити genre keys без questionId, favoriteTitles — до 3 internal IDs, era/mood — масиви. Не змішувати відповіді інших питань із жанрами. Лише успішна відповідь про збереження → done; 400/500/502/timeout залишають вибір і видимий retry; 401 → login із next. Skip також зберігається на сервері перед завершенням. Захистити nullable/відсутні/надмірні поля серверною валідацією, зокрема не викликати Length на null.

Приймання: реальний account flow на ізольованій БД; GET me.onboardingDone=true після успіху, дані профілю відповідають вибору. 400/500/offline не показують завершення. Contract test перевіряє JSON shape з клієнта до API, а не тільки локальний TS type.

## MD-03 — P1. Однозначні ID й єдина схожість

Доказ: frontend/app/page.tsx:219–248 (відкидає tmdbId=null); frontend/app/similar/page.tsx:93–110 (internal IDs); frontend/lib/api.ts:198–221 (GET→legacy POST); SimilarController.cs:112–146 (змішаний resolver).

Живе відтворення: головна → вкладка «Схоже на…» → Inception → «Початок (2010)» → «Знайти схоже» → «Помилка». Тайтл id=1218055, tmdbId=null. GET /api/similar/by-seeds?ids=1218055 працює.

Виправлення:

- Усі web entrypoints використовують internal title IDs, не викидають IMDb/anime/manga seeds. Не ігнорувати частину змішаного списку без повідомлення.
- Розділити namespaces: internal `ids`/`titleIds` та legacy `tmdbIds` резолвляться однозначно. При необхідності TMDB має включати media type (movie/TV). Зберегти/задокументувати legacy сумісність.
- Поточний POST також підтримує internal IDs через змішаний resolver; не виправляти його на підставі помилкового припущення, що він їх уже завжди відхиляє. Усунути неоднозначність реально.
- GET→POST fallback не повинен маскувати справжню 404 titles_not_found, тихо втрачати studio filters чи називати internal IDs TMDB IDs. Визначити підтримувану поведінку старої версії API; якщо підтримки нема — ясна помилка, без неправильних рекомендацій.

Приймання: головна, /similar, title-card button, quick/seeds/studio працюють із IMDb-only, anime, manga, TMDB та змішаними 1–10 seeds; 0/11 seeds дають зрозумілу валідацію. Fixture A.Id=B.TmdbId повертає очікуваний A для internal ID і B для typed legacy ID. GET і POST filters еквівалентні. Невідомий ID → контрольована 404, не fallback на інший тайтл.

## MD-04 — P1. Гостьові дані та надійна міграція

Доказ: frontend/lib/fallback.ts:254–272; frontend/app/lists/page.tsx:80–103, 140–155; ListsController.ResolveProfileAsync. guardedGuestItems після login читає поточні authenticated lists, не гостьове сховище; Promise.allSettled губить помилки; guestMigrated глобальний для браузера й виставляється без перевірки повного перенесення.

Виправлення:

- Реалізувати обумовлені PLAN гостьові lists у versioned localStorage (watchlist/watched/read/liked/disliked, internal titleId, rating). У разі недоступного storage — чесне повідомлення про межі збереження.
- Окремо визначити безпечний шлях для вже наявного legacy default guest profile: не розкривати списки приватних акаунтів, не приписувати shared legacy дані довільному користувачу автоматично. Запропонувати явний перенос доступного snapshot.
- Після login пропонувати саме guest snapshot; міграція лише за дією користувача, idempotent. Не видаляти snapshot і не ставити completion marker до підтвердження всього набору. Completion scoped by account+snapshot/version, не глобальний одноразовий guestMigrated.
- Часткова помилка → точна статистика, retry та збереження джерела. Розв'язати конфлікт rating/status детерміновано та описати в PLAN. Migration POST не приймає profileId чужого користувача.

Приймання: усі 5 kinds і рейтинги зберігаються після reload гостя; login A → перенос → account A містить рівно очікувані записи; повтор не дублює; injected failure не приховує retry; logout/login B не успадковує marker A і не отримує дані A. Доведення видалення даних до аудиту відсутнє — не подавати ризик як підтверджену втрату.

## MD-05 — P1. Бот для всього каталогу та безпечний профіль

Доказ: bot/Bot/BotUpdateHandler.cs:19–20, 309–333; bot/Bot/Keyboards.cs:17–34; bot/Api/ApiClient.cs:35–39, 66–76.

Виправлення: internal ID для callbacks, seed-resolve й list-add; уникнути 0/null IDs; оновити API DTO/клієнт та parser одночасно. Замінити ProfileId=1 на безпечне явне mapping Telegram identity→authorized profile або обмежений legacy guest workflow, сумісний з MD-01. Не надавати ботом доступ до account profile лише за числовим ID. Старі callback payloads обробляти контрольовано, дотримати 64-byte ліміт Telegram.

Приймання: пошук, detail, similar, multi-seed, watchlist/watched/read працюють із tmdbId=null; перехресні Telegram users не читають/міняють чужі дані. Tests з default profile ID≠1, простроченим callback та forged ID. Live Telegram — лише тестовий токен/акаунт, інакше чітко відмічений unverified.

## MD-06 — P2. Паритет фільтрів бота

Реалізувати зрозумілий командний або interactive flow FR-8: тип, жанри, рік, країна, рейтинг, runtime/status; similar-from-card та чинні text/multi-seed режими. Вся предметна логіка лишається у backend; /help і uk/en відповідають реалізації. Приймання: однакові фільтри web/bot формують еквівалентний API query та результати на fixture dataset; cancel/reset/pagination зберігають коректний стан.

## MD-07 — P2. Auth, проксі, помилки й деградація

Додаткова перевірка перед прийманням, а не перелік уже експлуатованих багів: cookie login/register/logout через runtime proxy та прямий API; A/B isolation; 401/403/423/429; logout при недоступному API не повинен удавати завершену серверну сесію. Перевірити Secure cookies для HTTPS, CSRF/origin захист state-changing cookie запитів та reverse-proxy IP rate limits з довіреними proxy settings. Не довіряти будь-якому X-Forwarded-For від клієнта.

Приймання: тести через same-origin proxy із Cookie/Set-Cookie; TLS staging cookie flags; rejected foreign-origin mutation; незалежні користувачі не ділять помилково одну IP-квоту, spoof headers не обходять limiter. Не вмикати довільне trust-all проксі.

Поточний GET /api/health повертає status=ok навіть коли db=false у коді. Розділити liveness/readiness або документувати machine-readable degraded readiness; Ollama=false має чесно позначати вимкнену семантику. Непрацююче API не повинне показувати demo content як живий каталог або success незбережених операцій.

## MD-08 — P2. Якість UI, PWA і даних

- Перевірити uk/en, клавіатуру/focus, читабельні помилки, 360/390/768/1440 px, відсутність horizontal overflow. Жанри в reasons локалізувати: зараз браузер показує «спільний жанр: adventure/science fiction» в українському UI.
- Production PWA: manifest/icons/maskable/apple, install, offline fallback, online recovery, update SW; API GET/POST не кешуються. Ізолювати власний cache prefix, не видаляти unrelated origin caches. Не кешувати персональні HTML/дані між користувачами. Dev SW lifecycle має бути явним (нині коментар каже production, умови NODE_ENV нема).
- Відсутні опис/постер/трейлер/джерело показуються чесно; «FREE»/watch links перевіряються на конкретний тайтл і реальну доступність, без хибної гарантії легальної повної версії. Не запускати збагачення/скрапери/платні квоти без потреби.
- Ollama зараз false: окремо довести metadata fallback; семантичні тести на ізольованому index, якщо модель доступна, інакше позначити unavailable без сертифікації FR-5.

## MD-09 — P2. Продуктивність та актуальність документації

Inception search мав один замір 7.6 с у паралельному smoke. Виміряти cold/warm, search із фільтрами, detail/similar на реалістичному наборі, зберегти median/p95 і план SQL. Не встановлювати індекси на реальній БД під виглядом аудиту. TZ.md:157 NFR-1 вимагає пошук/схожість по локальному індексу ≤1 с (p95) без зовнішніх API. Застосувати до задокументованого dataset/environment; якщо не досягнуто — звіт і наступний fix, без вигаданих performance гарантій. NFR-2 вимагає ≥50000 популярних тайтлів з описами та векторами; самі 2.86 млн рядків і metadata similarity не доводять її виконання.

Оновити README (поточне «ми тут Е0» застаріле), PLAN, TZ status. Документувати локальний frontend port (README каже 3001, npm dev без -p використовує 3000), standalone startup та зовнішні залежності. Не називати stage виконаним лише на основі історичного E2E 16/16.

## MD-10 — P1. Відтворювана регресія і передача результату

Додати мінімальний browser E2E runner і API integration tests для MD-01–05. Існуючий e2e/e4_accounts.py не замінює browser tests; слабкий пароль не можна зараховувати перевіреним тільки за 429, індексація items[5] має передумови. Усі тести створюють свої fixtures у disposable БД, мають deterministic setup/teardown, завершуються ненульовим exit при FAIL.

Обов'язкова матриця: guest/A/B ownership; register/login/logout/lockout/rate limit; onboarding success/skip/400/500/offline; guest lists persistence/migrate/retry; усі non-TMDB similar paths і ID collision; bot callbacks; UA/EN; mobile; PWA offline; backend unavailable/available recovery. Не додавати тести, які лише дублюють реалізацію і не ловлять ці регресії.

Виконати dotnet test backend і bot, npm lint, tsc, build послідовно, API integration, browser E2E. Зберегти commands, revision, counts, logs, screenshots та межі перевірки. Наявні 419+111 тести повинні лишитися зеленими, нові покривають знайдені дефекти.

## Порядок і Definition of Done

1. MD-01: ізоляція + regression, без публічного розгортання.
2. Узгодити ID/guest contracts (MD-03/04), потім MD-02, web flows і MD-05.
3. MD-06–09 і наскрізна MD-10. Не відкладати виправлення P0 заради розширень.
4. Підсумковий report: кожен MD-ID → commit/files → test/evidence → fixed/partial/unverified. Усі P0/P1 закриті або явно заблоковані з точною причиною; без неправдивого «готово».
5. Оновлена документація й reviewable Git diff; local checkpoint/commit лише своєї роботи за потреби, без push/deploy.
6. Після всіх агентів/тестів — фінальний cleanup лише task-owned процесів/портів/контейнерів/тимчасових БД; зберегти user services і дані. Окремий Environment Cleanup Report з PID/container IDs і залишками.
