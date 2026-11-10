# Незалежне приймання змін Hermes — проміжний результат

2026-11-10. Статус: НЕ ПРИЙНЯТО, Hermes ще виконує інтеграцію. Не видавати цей документ за завершений аудит нової розгорнутої версії.

## Що перевірено незалежно

Знята окрема копія338 source-файлів з поточного робочого дерева в `.agent-work/codex-review-20261110`, hashes кожного початкового файла в source-manifest.json. Код продукту Codex не змінював. Паралельний read-only review виконали дешевші агенти review_backend і review_frontend; вони не створювали серверів/ресурсів.

- Оригінальний backend snapshot: compile FAIL, шість CS1061 в WatchUrlClassifier.cs67,70–74 (bool.Search(...).Success).
- Frontend next build: exit0, compile/typecheck/lint/prerender PASS. Через node_modules junction у копії є EPERM warning при standalone trace-copy, тому готовність standalone packaging цим не підтверджена.
- Bot suite:158/158 PASS.
- Щоб побачити додаткові помилки, ТІЛЬКИ В ДІАГНОСТИЧНІЙ КОПІЇ виправлено шість bool.Success, додано missing using Persistence у WikipediaPosterTests і Controllers у TrailerSyncTests, InternalsVisibleTo для Infrastructure/API та default CancellationToken у SyncOneAsync (самі тести викликають internal API без токена). Це не продуктова реалізація і не підстава вважати його компільованим.
- Після цих compile-only діагностичних правок:707 backend tests,705 PASS,2 FAIL: TrailerSyncTests.RunAsync_Skips_Titles_With_Rows_And_Writes_SyncState та MatchesAnyName_Wrong_Year_Rejects. TRX: `.agent-work/codex-review-20261110/results/backend-diagnostic.trx`.

## IV-01 P1 — імпорт порушує ізоляцію профілів

`backend/src/MovieDiscovery.Api/Controllers/ImportController.cs`: CreateJob анонімного користувача довіряє req.ProfileId (перевіряє тільки існування). Без cookie можна замовити імпорт до іншого профілю. GetJob теж не перевіряє власника.

Незалежний WAF-тест `Codex_Anonymous_Must_Not_Target_Another_Users_Profile` створює профіль9876 у InMemory test DB, анонімно POST /api/import/jobs `{source:"shikimori",username:"some_nick",profileId:9876}`. Очікує401/403; фактично202. Жодних реальних списків/production DB не змінювали, worker у тесті вимкнений. TRX: `results/import-isolation.trx` в діагностичній копії. Проба додана лише в копію ImportProfileTests.cs.

Виправлення: сервер визначає доступний профіль, не довіряє довільному client profileId. Авторизований імпорт завжди власний; гість має обмежений явно визначений legacy-шлях або вимогу входу. Читання джоб теж за власником. Додати негативні тести гість→чужий, A→B та статус чужої джоби.

## IV-02 P1 — backend і тести не компілюються

Початковий WatchUrlClassifier має extension Search():bool і водночас читає .Success у шести викликах. Після усунення цього блокера в копії виявилися додаткові test compile errors: missing using, недоступні internal SearchService.MaxPages/SyncOneAsync/ToInfos, відсутній ct у викликах тестів. Потрібен звичайний dotnet test БЕЗ локальних reviewer-патчів у фінальному дереві.

## IV-03 P1 — admin trailer sync повертає фальшиве queued

TrailersController.SyncTrailers перевіряє ключ/limit і віддає202 queued:true, але не викликає TitleTrailerSyncQueue.TryEnqueue. Потрібно реальне enqueue, queue-full/configuration/error handling та тест виклику/виконання, а не тільки коду відповіді.

## IV-04 P1/P2 — мовні варіанти губляться на upsert

WatchLinkUpsertCore.Resolve порівнює title/provider/kind/region, ігноруючи AudioLangs, хоча змінений unique index містить AudioLangs. Другий ru offer знаходить uk рядок замість створення окремого мовного варіанта. Узгодити нормалізований ключ зберігання, upsert, dedupe і міграцію; тест uk+ru одночасно та ідемпотентний повторний sync.

## IV-05 P2 — concurrent trailer miss

GET trailers при порожньому cache/rows незалежно викликає SyncTitleAsync на кожен конкурентний запит. Немає single-flight/claim, тому можливі дубльована квота й unique violation при вставці title_id+yt_id. Додати guard та одночасний cold-cache тест. Не навантажувати реальну YouTube-квоту для цього тесту.

## IV-06 P1/P2 — фільтр «є трейлер» не бачить нові трейлери

SearchService.cs189–190 перевіряє лише titles.trailer_yt_id. Новий TitleTrailerSync записує title_trailers і не заповнює це legacy поле. Тайтл з перевіреним uk кандидатом зникає з hasTrailer=true.

Незалежний CodexFeature_HasTrailer_Includes_Verified_New_Trailer_Storage створює Title з null legacy ID та TitleTrailer verified_status=ok. Очікує тайтл у SearchAsync HasTrailer:true; фактично порожній результат, FAIL. Тест у діагностичній копії CodexFeatureIntegrationTests.cs; TRX results/feature-integration.trx. Потрібно врахувати лише придатні нові кандидати і зберегти legacy-сумісність; негативні gone/private/nonembeddable cases теж потрібні.

## IV-07 P1/P2 — мовний фільтр не бачить нову metadata озвучок

SearchService.cs244–249 AudioLang перевіряє тільки watch_links.audio_lang. Нові набори audio_langs і providers.audio_langs не враховані; ukr/russ legacy коди не зіставляються з uk/ru набору.

Незалежний CodexFeature_AudioFilter_Includes_New_MultiLanguage_Offer створює доступний WatchLink з AudioLang=null і AudioLangs="uk,ru". SearchAsync AudioLang:"ukr" повертає0, FAIL замість наявного тайтла. Тест/TRX як IV-06. Виправити нормалізацію, пошуковий predicate (токени, а не substring) і перевірити RU, subtitles-only не є audio, unavailable/unknown не є підтверджена озвучка.

## IV-08 P2 — Wikipedia fill-only не заповнює порожній рядок

WikipediaPostersStaging.UpdateSql дозволяє WHERE poster_path IS NULL OR poster_path='', але SET poster_path=COALESCE(t.poster_path,s.poster_url) повертає саме '' для другого випадку. Рядок вважається оновленим, але картинка лишається порожньою. Read-only PostgreSQL SELECT COALESCE('', 'https://example.test/original-poster.jpg') = '' дав true. Потрібно NULLIF(poster_path,'') або еквівалент та тест NULL/''/наявного валідного постера на реальному PG.

## IV-09 P2 — наявні логотипи не стануть оригінальними постерами

Конкретний приклад: GET prod /api/title/581458?lang=uk (Зоряний шлях: Відплата,2002) повертає `https://commons.wikimedia.org/wiki/Special:FilePath/Star_Trek_Nemesis_Logo_2.webp?width=500`. Це вже спостерігалося в браузері. Новий cleanup очищує тільки ytimg/missing_original, а Wikipedia fill-only виключає будь-який непорожній poster_path. Тому конкретний legacy-logo переживає запропонований pipeline, а UI продовжить видавати його за постер.

Потрібна перевірка/provenance типу зображення, безпечна заміна підтверджених wrong media та тест на цей тайтл/логотип; не зносити всі Commons-зображення масово. Джерело нового poster має бути справді постером відповідного фільму, сама відповідність QID недостатня для типу зображення.

## IV-10 P2 — мобільна навігація майже повністю обрізана

Browser localhost3201 при390x844: main nav має bounds x165..208.4 (ширина43.4px) між брендом і кнопкою входу/мовами. Посилання «Схоже на...» має ширину93.2px, з видимої частини лишається фрагмент «Схо…». Весь список8 розділів прокручується в надто вузькій смужці. Клік автоматизації за цим link потрапив на «Увійти» й відкрив /login; центральна точка повного link лежить поза clipped nav. Це також обмеження автоматизації clipped links, але візуальний дефект навігації43px безспірний. Потрібне доступне мобільне меню/окремий рядок навігації, зручні tap-targets. Screenshot MOBILE_NAV_390.png.

## Останній повторний build без reviewer-патчів

Після початкових зауважень Hermes вже змінив WatchUrlClassifier, додав реальне enqueue трейлерів, language-key upsert, guard імпорту та пошукові predicates. Це позитивні зміни, але ще не означає приймання: нова ЧИСТА копія поточного backend227 файлів у `.agent-work/codex-review2-20261110` з source-manifest.json знову не компілюється — ImportService.cs20 CS0246 GuestProfileService (немає using Services). Лог backend-fresh.log. Жодних reviewer-патчів у другій копії немає. Продукт продовжує змінюватися, його остаточний commit та інтеграцію ще не отримано.

Наступне оновлення6 source-файлів у другій копії, також без reviewer-патчів (source-manifest-recheck.json): ImportService using вже виправлено, але build далі FAIL Program.cs174–175: RetryAfterMetadata не існує (CS0103), object не має TotalMinutes (CS1061). Лог backend-recheck.log. Потрібен правильний typed rate-limit metadata API (MetadataName.RetryAfter), а потім звичайний clean build/test. Не гонити повторні збірки до стабільного фіналу агентів; поточний рухомий стан ще не release candidate.

## IV-11 P0/P1 — production PostgreSQL нестабільний під час живої перевірки

Окрема перевірка приблизно01:00UTC: оригінальний контейнер БД ae0addb940cc залишився running, але unhealthy (FailingStreak28), pg_isready повідомляє rejecting connections. Логи: server process exited with exit code2, terminating active server processes/reinitializing, recovery mode, короткий ready-to-accept-connections і повторне падіння. OOMKilled=false, RestartCount=0. Є WAL recovery повідомлення, але самі по собі вони не доводять причини/пошкодження; root cause не встановлено.

GET localhost3001/api/health повернув200 з `status=degraded,db=false,titlesCount=0`; тестовий localhost3201/api/health одночасно повернув200 `status=ok,db=true,titlesCount=1429`. Отже production availability не пройшла перевірку. На C/G достатньо місця, Windows free memory близько13GB; це не доводить відсутності інших ресурсних або storage проблем.

Codex не рестартив/не перестворював БД чи user services, не виконував migrations/UPDATE на production. Жодних pg_resetwal/видалень даних/force shutdown не робили. Потрібна окрема діагностика зі збереженням даних і дозволом перед будь-якою зміною наявної інфраструктури.

Оновлення01:02:46UTC: останній Docker health став healthy (самовідновлення без дій Codex), але повторний GET production /api/health перевищив6s timeout; test API водночас200/ok/db=true. Зафіксовано у LIVE_RUNTIME_INCIDENT.json. Це нестабільність під час перевірки, не твердження, що БД досі постійно unhealthy. Сам факт recovery не є доказом пошкодження даних або завершеного виправлення.

## Незавершена інтеграція на момент snapshot

- Нові EF-поля/таблиці/індекси ще не мають міграції в перевіреному diff.
- Title page ще рендерить legacy iframe; TrailerSection не підключена.
- Watch selector/нові мовні поля ще не інтегровані у title UI.
- Detail poster/backdrop ще raw img без onError; truthy але broken backdrop лишає tall hero.

Це acceptance blockers, але Hermes ще працює. Перевірити повторно після його фінальної інтеграції, не видавати за дефекти вже завершеного релізу.

## Додатковий функціональний review

- Onboarding search: без sequence/abort guard старий запит може перезаписати новий.
- Import polling: після4 network failures перестає poll, але job залишається queued/running і форма busy; нема Resume/Retry.
- router.replace для кожної сторінки/фільтра не створює browser history: Back не повертає попередню сторінку результатів.
- PosterImage після двох помилок не дає повторити той самий src без unmount/зміни URL.

## Поточний старий стенд3201 — browser baseline, НЕ приймання нових змін

- На головній24 картки/«Показати ще»: стара версія, нова пагінація ще не розгорнута.
- Гостьові watchlist/watched/rating8 працюють; watched пережив reload, зміна рейтингу зі списку дала середнє8.
- На title1 similar badges показали110% і105%: відсотковий формат виходить за100. Потребує перевірки семантики score/нормалізації.
- Реєстрація окремого synthetic test account показала загальне «Не вдалося. Спробуй ще раз». Окрема API-проба з валідним username підтвердила HTTP429 з порожнім body; SELECT test DB підтвердив0 створених акаунтів з тестовим email. Це вичерпаний limiter3/h, а не успішна реєстрація. UI не пояснює rate limit/час повтору. Повний новий account flow поки заблокований цим станом тестового стенду.
- Guest import form доступна; зовнішній import не запускався до перевірки ізоляції.
- Settings: English+PL збереглися після reload. Після тесту повернуто початкові Українська+UA. Viewport390x844 після перевірки скинуто до default.

## Ресурси

Codex не запустив жодного сервера/контейнера. Дві спроби власного frontend3401 відхилені automatic policy review (`blocked by policy`), не повторювати альтернативними механізмами. npm build / dotnet test завершені. Ізольована source-копія і logs/TRX збережені для review. Browser tab2 тимчасовий. Hermes task stacks і user services не зупинялися. Завершальне очищення відкласти до кінця агентів/інтеграції/всього приймання.

### Handoff і межі поточного приймання

IV-01–09 передані у найлівіший чат Hermes; у UI підтверджено прийняття й steer виконавцям. Видимі реальні виправлення, але остаточних гейтів/коміту/нового web-стенду ще не отримано. IV-10, останній build з RetryAfterMetadata і IV-11 додано в цей документ; їхня доставка в GUI не підтверджена: click/capture почали timeout, повторна селекція того ж Hermes-вікна і повторний capture теж timeout. Не писати «все передано» для цих останніх пунктів.

Повний браузерний прогін НОВОЇ інтегрованої версії ще не виконаний. Аккаунтний цикл заблоковано429 на старому тест-стенді; новий source/backend не має стабільного clean gate, production DB нестабільна. Успіх окремих unit tests не є доказом готовності всього сайту. Потрібен завершений Hermes commit + міграції + updated test web/API, потім повторні negative probes, account/onboarding/import і desktop/mobile E2E.

### Environment Cleanup Report (поточна фаза review)

- Stopped processes/services:0 — всі task-owned npm/dotnet команди завершилися самі.
- Removed temporary resources:0 — source snapshots, logs, TRX та screenshot збережені як докази, не видаляти до повторного приймання.
- Own orphan verification: Win32_Process не показав node/dotnet/testhost з review-path у командному рядку; listener3401 відсутній.
- Existing user services: не зупиняли/не перезапускали; оригінальні container IDs збережені. Production DB unhealthy — операційність user infrastructure НЕ підтверджена.
- Remaining: Hermes5 agents/test stacks лишаються працювати, не належать цьому Codex-проходу; фінальне загальне очищення заборонене, поки вони працюють. Належність спільних build/compiler helpers не встановлювали — не чіпали.
- Cleanup errors: жодних cleanup-команд не запускали; resource termination не потрібна. GUI capture timeouts і policy rejection власного web-server — окремі обмеження перевірки, не привід чіпати чужі сервіси.
