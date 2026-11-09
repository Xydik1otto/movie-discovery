# MovieDiscovery: повна матриця пошуку, ідентифікації й стрічок

Дата: 09.11.2026. Код: **0fd1b56** (реалізація CR-03 babd123). Початкова робоча копія чиста. Production web/api залишаються на попередніх Docker images; актуальний код додатково перевірено в ізольованому project **codex-search-audit-20261109**.

## Висновок

**Каталог працює частково, повну працездатність пошуку й усіх стрічок підтвердити не можна.** Є базовий пошук назв, пагінація, картки, metadata-схожість. Виявлені блокувальні розбіжності контрактів збірників, несправні anime/manga format та rating filters, розбіжна TV-таксономія, неповні назви/метадані, повтори в Кінотеці й повільні/падаючі запити.

Аудит перевіряє роботу продукту, а не виправляє його. Код продукту, .env, production DB та інфраструктура не змінювалися; нові файли — лише докази й відтворювані audit scripts. Розпізнавання тайтлів тут перевірено як назви/локалізації/джерела/ID/типи/поля картки. Окремого розпізнавання за завантаженим кадром чи відеофрагментом в оглянутих UI/API немає.

## Що перевірено

| Напрям | Обсяг та результат |
|---|---|
| Живий public API :3001 | **85 запитів**: 75×200, 2×400 очікувана валідація, 3×401 очікуваний auth gate, **1×500**, **4 таймаути 24 с** |
| Актуальний API на disposable PostgreSQL | **47 перевірок**: 35 збігів з очікуванням, 12 відхилень; частина відхилень — контракт/якість UX, не всі є HTTP-збоями |
| Backend suite | **481/481 PASS**, 0 skipped; існуюче покриття не ловить наведені end-to-end розбіжності |
| Ідентифікація карток | **12/12** вибраних search/feed records з movie/series/anime/manga/cartoon/dorama відкривають detail з тим самим internal ID, title і type |
| Реальний браузер | Живі /anime, /manga, головна/Телешоу; актуальні /discover і /similar→Збірники на тестовому акаунті |
| Додаткові probes production-клієнта | getCollectionPresets/getCollections виконано з фактичного api.ts проти реального fixture API; схеми не сумісні |
| Метадані | Read-only агрегати **всіх 2 863 652** рядків titles; count embeddings; TV genre taxonomy |
| Пагінація | Anime page1/2/3 по24, без перетинів сусідніх сторінок; page=-1 нормалізується; надвелика page падає |
| Семантичний текст | 3 fixture запити дали200 через metadata fallback; Ollama недоступна, в production embeddings=0 — справжня векторна семантика не підтверджена |

Код зібрано Docker api/web (API publish PASS, frontend production build з валідного cache layer). Повний backend suite пройшов із відомими EF Relational version/nullable/xUnit warnings. Нових frontend code changes у цьому аудиті немає.

47-пунктова матриця враховує, зокрема, бажане відхилення оберненого діапазону років (повернуто200 з порожнім результатом); це позначено як прогалина валідації, а не як злам сервера. Наведено лише фінальний результат після успішного повного сіда: початкову помилку кількості SQL VALUES у моєму audit fixture виправлено, її результати не використані як дефекти продукту.

## Покриття розділів

| Стрічка/сценарій | Перевірений стан |
|---|---|
| Фільми | 2 480 710 records; назва/year/rating/genre/query працюють; значні metadata gaps і latency |
| Серіали | 382 037; базова видача працює; episode runtime/seasons/language порожні в живих даних |
| Телешоу | UI type=show дає0; category=tvshow не відповідає збереженим жанрам; запит timed out |
| Аніме | 741; картинки й episodes є; format не фільтрує, rating не використовує AniList/MAL, назви без aliases |
| Манга | 164; volumes/chapters на частині records є; format/rating аналогічно несправні |
| Мультфільми | category=cartoon повернув121739; sample details узгоджені |
| Дорами | category=dorama повернув31808; fixture показав пропуск countries[] та домішування anime/manga |
| Схожість від тайтла / seeds | Повертає200, seeds виключаються; metadata fallback; ONE PIECE без type filter дає топ звичайних фільмів |
| Quick/Seeds/Studio | API multi-seed працює; hard type/year filters є в коді; genre у Studio задуманий як soft boost, не strict exclusion — це не зараховано як дефект |
| Пресети збірників | Backend3×12 у production, але frontend показує «Пресети поки недоступні» |
| Власні збірники | На fixture створена1 collection, raw API її віддає; frontend читає0 |
| Кінотека | Auth redirect гостю працює; на тестовому акаунті після6 skip колода спорожніла; API повертає skipped знову |
| Гостьові списки/імпорт/settings | Навігація існує; попередній CR-03 не переаудитовувався тут повністю; live імпорт зовнішніх списків не запускався |

## Дані: чому багатьом фільтрам нічого знаходити

Джерело: [live-data-coverage.csv](search-catalog-audit/live-data-coverage.csv), read-only SQL. Це повний агрегат titles, не оцінка за першою сторінкою.

| Тип | Кількість | Назва uk | Опис | Актори | Director | Мова | Runtime серії | Сезони | Постер |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| movie | 2480710 | 105 | 0 | 0 | 0 | 0 | 0 | 0 | 57 |
| series | 382037 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 3 |
| anime | 741 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 741 |
| manga | 164 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 164 |

Embeddings: **0**, Ollama health=false. У таблиці вище Director — саме singular поле, яке використовує пошук/detail; імпортний directors[] розглянуто окремо в SC-08.

## Пріоритетні дефекти й критерії виправлення

### SC-01 / P1 — Збірники невидимі через невірні response envelopes

backend CollectionsController повертає `{presets:[...]}` і `{collections:[...]}`. **frontend/lib/api.ts:456–468** читає `r.items ?? []` для обох методів. Фактичний клієнтський probe: serverPresetCount=3/clientPresetCount=0, serverOwnCollectionCount=1/clientOwnCollectionCount=0. Реальний браузер показує порожні стрічки, хоча API має дані.

Додатково CollectionsPanel.PRESET_KEY очікує mood/evening/duration, backend дає preset.mood/preset.evening/preset.duration — після виправлення envelope назви теж слід узгодити.

**Приймання:** реальні DTO → frontend adapters → 3 пресети з локалізованими назвами; створена добірка видима після reload, detail/delete працюють тільки для власника. Contract/browser regression, не тільки окремий GET200.

### SC-02 / P1 — Формат аніме/манги ігнорується

CatalogPage.tsx:67–70 передає format через searchTitles, але SearchController не приймає format, SearchQuery не має такого поля. У production tv/movie/ova/ona/special і навіть impossible_format повертають однакові **741** records; manga/novel/one_shot — однакові **164**. У браузері обраний «Фільм», але зверху ONE PIECE та інші ТВ-серіали, total741.

Fixture current code: anime movie і tv обидва повертають [104,105], novel — [106,107].

**Приймання:** типізований format, SQL-фільтр до count/pagination, валідація несумісних типів/невідомих форматів; anime movie лише105, tv лише104, novel лише107. Обраний чіп відповідає всім results і total.

### SC-03 / P1 — Anime/manga rating sort та rating filters використовують чужі шкали

SearchService.cs:301 сортує лише RatingTmdb/RatingImdb; ApplyRatingFilter також не враховує ScoreMal/ScoreAnilist/ScoreShikimori. У live anime rating order збігається з popularity, хоча видимі AL8.7,8.9,8.5,9.0 — не впорядковані за rating. Fixture: anime score2/pop700 стоїть перед score9.8/pop600; manga3 перед9.7. minRating9 для anime не знаходить score9.8.

**Приймання:** явний рейтинг відповідного типу/обране джерело, nullable fallback узгоджений; anime105 перед104, manga107 перед106; anime minRating9 включає105. Не підмінювати показаний користувачу AL/MAL рейтинг іншою невидимою шкалою.

### SC-04 / P1 — Телешоу класифіковані двома несумісними способами

Головна пропонує type=show, але IMDb mapper імпортує tvSeries як series, tvSpecial як movie; type=show у БД **0**. Віртуальна category=tvshow шукає reality-tv/talk-show/game-show/variety (VirtualCategories.cs:20), а ImdbMapper.cs:54–58 нормалізує їх у **reality/talk/game show**.

Read-only БД: virtual_tv_tokens=0, normalized_imdb_tv_tokens=**86597**, show_type=0. «The Voice» реально має `{game show,music,reality}`. Тобто відсутність видачі — не відсутність відповідних творів, а збій контракту жанрів/категорій. UI TeleShow повертає «Нічого не знайдено»; category query ще й timed out.

**Приймання:** одна канонічна taxonomy й один узгоджений шлях UI/API; реальні talk/reality/game shows знаходяться, movie/series/anime не зникають помилково. Обережна data migration або сумісне читання; не змінювати live БД під виглядом аудиту.

### SC-05 / P1 — «Кінотека» не виключає вже показані/skip під час refill

DiscoverService.GetFeedAsync виключає лише ListEntries, а skip пише тільки InteractionLog. API не приймає seen IDs/cursor; guardedDiscover(lang,exclude) передає exclude лише demo fallback, не реальному getDiscover. Frontend filters seenIds локально, але сервер повторно віддає той самий top batch, тому fresh=[].

Fixture API: skip102→200; наступний feed знову містить102. Браузер: шість «Not now» → «Нараз усе показано», хоча в БД18 тайтлів. Це проблема безперервної видачі/пулу, не доказ нескінченного request storm. Наявність мережевого шторму не підтверджено; application logs не містять request-level рядків.

**Приймання:** cursor/seen exclusion на сервері або session-scoped skip policy, стабільне поповнення за наявних кандидатів, контрольований exhaustion/no-progress; skip не повинен змінювати смак, але має запобігати повтору в поточній колоді. Після restart/session renewal політика повернення skipped визначається явно. Browser test пройти більше одного batch.

### SC-06 / P1 — Metadata/embeddings pipeline не забезпечує заявлені пошукові можливості

Усі overview/actors/director/language/episode_runtime/seasons порожні, embeddings0. q=Leonardo DiCaprio повернув11 **назв** документалок/короткометражок про нього, а не фільмографію; director=Nolan повернув0, actor filter timed out. Anime country=JP/language=ja повертають0, бо дані не заповнені.

**Приймання:** контрольований metadata enrichment на ізольованих samples, вимірювані coverage thresholds по типах, локальні aliases, credits, episode runtimes/status/language та embeddings; чесне UI-позначення недоступних полів/можливостей. Не починати необмежений імпорт мільйонів записів без окремого плану. 2.86 млн рядків не дорівнюють 2.86 млн повних карток.

### SC-07 / P1–P2 — Назви й канонічна ідентифікація між джерелами неповні

type=anime&q=Attack on Titan →0; та сама річ за Shingeki no Kyojin →8 anime records. AniListMapper обирає Romaji перед English і не зберігає aliases як пошукові назви. Наруто українською →0, японський 進撃の巨人→0. Регістр Naruto/NARUTO працює однаково (2 anime), тобто це не case bug.

Один твір існує окремо як IMDb series Attack on Titan id1821781/2013 та anime Shingeki no Kyojin id521/2013; аналогічно Naruto існує як series722851 та anime532. Manga/адаптація/сезон не повинні зливатися лише через однакову назву: потрібні explicit relationships і обережне canonical matching.

**Приймання:** alternate/original/uk/en/romaji/japanese назви індексуються, source IDs і media type визначають канонічний запис; дублі одного твору зв'язуються, сезони й манга відділяються. IMDb ID tt1375666 зараз не є пошуковим ключем (0 results) — це окрема корисна можливість, а не доведене порушення поточного q-контракту. 12/12 існуючих internal ID→detail узгоджені.

### SC-08 / P1–P2 — IMDb crew пише directors[], пошук читає Director

ImdbCrewSync оновлює directors/actors, але SearchService загальний q і director filter читають singular director. Title detail також віддає singular поле. Навіть завершення поточного crew sync саме по собі не підключить directors[] до цих шляхів.

Fixture113 має directors=['Array Director'], director=NULL: q=Array Director→0, director=Array Director→0, detail.director=NULL. [Probe](search-catalog-audit/credits-and-rating-probes.json).

**Приймання:** один узгоджений credit model або явний bridge; q/filter/detail/similarity бачать всі підтверджені режисерські credits; translation tests на PostgreSQL, не лише in-memory.

### SC-09 / P2 — Overflow пагінації та неповна валідація

SearchService.cs:63 обчислює int `(page-1)*pageSize`. page=2147483647 дає **HTTP500** і на live, і на current fixture. Обернений yearFrom/yearTo не відхиляється: fixture200 empty, live24s timeout. Невідомі type/genres правильно дають400, negative page нормалізується до1.

**Приймання:** safe arithmetic + bounds/max page чи cursor; некоректний range дає швидку зрозумілу400 (або документовану нормалізацію), жодного500. Stable tie-break по id для сортування/пагінації; overlap на перших3 live anime сторінках не виявлено.

### SC-10 / P2 — Озвучка й якість можуть походити з різних watch links

SearchService.cs:197–209 застосовує два незалежні EXISTS. Fixture111 має ukr480 і eng1080, але audioLang=ukr&minQuality=1080 його повертає. Такого посилання ukr1080 у нього немає.

**Приймання:** умови конкретної версії/посилання в одному EXISTS (за узгодженою продуктовою семантикою), врахувати region; fixture111 виключається, fixture101 з ukr1080 включається.

### SC-11 / P2 — Dorama пропускає countries[] і включає інші media types

Country search правильно OR-ить scalar Country і Countries[], але category=dorama перевіряє тільки scalar Country. Немає type guard. Fixture: очікувані KR series109/110; реально повернені anime105, manga107, series109; series110 з countries=['KR']/Country=NULL пропущений.

**Приймання:** та сама country-normalization, визначений перелік movie/series media types, drama genre; ні anime/manga домішки, ні пропущених KR records через різне сховище країни.

### SC-12 / P2 — Картки втрачають країну й runtime серії

TitleMapper.ToCard бере лише Countries[] і RuntimeMin. Scalar Country з IMDb akas не відображається; EpisodeRuntimeMin не доходить як series duration. Fixture109 Country=KR → detail.countries=[]; fixture102 episodeRuntime25 → detail.runtimeMin=NULL.

Додатково IMDb bulk зберігає runtime у RuntimeMin навіть для series, а ApplyRuntimeFilter(type=series) читає EpisodeRuntimeMin. У live episode runtime відсутній у всіх series, тому range20–30 повернув0. Потрібна узгоджена семантика зберігання, а не лише виправлення UI label.

**Приймання:** metadata з будь-якого підтриманого джерела з'являється в картці/фільтрі з правильним meaning; KR видно,25хв видно й фільтрується. Не трактувати тривалість усього твору як runtime епізоду без валідації джерела.

### SC-13 / P2 — Relevance і cross-source recommendations мають структурні обмеження

relevance в SearchService.Sort — популярність із year tie-break, не title relevance. Exact Naruto стоїть після Shippuden; укр/ромадзі aliases не беруть участі. Similarity candidate pool — глобальні top2000 за popularity; anime popularity нормалізовано /10000, IMDb — log10(votes+1)*10. Пул може витісняти anime до скорингу.

ONE PIECE id528 similar(top5) у live: Star Wars I, The Mummy, Being John Malkovich, Austin Powers, Dogma — жодного anime. Cross-type рекомендації дозволені контрактом, тому це **спостереження якості**, не автоматичний ID bug. Без описів/embeddings semantic quality не оцінена.

**Приймання:** golden set для exact/alias queries, franchise/season disambiguation; квоти/нормалізація candidates за типами/джерелами та recall metrics до scoring. Studio hard type filter залишається доступним.

### SC-14 / P1–P2 — Latency звичайних фільтрів порушує практичну працездатність

На live, concurrency максимум2: cartoon8.6с, dorama8.5с, genre10.6с, country10с, rating13.9с, runtime11.9с, seasons11.3с, trailer13.4с. tvshow/actor/reversed years/literal '%' — timeout24с. Це одиночні виміри, **не p95 benchmark**; водночас вони пояснюють UI очікування/помилки і не підтверджують NFR≤1с.

**Приймання:** EXPLAIN/ANALYZE в безпечному staging, відповідні indexes/canonical predicates, валідація до DB scan, count/page strategy, request cancellation. Профіль cold/warm під задокументованим навантаженням. Не встановлювати live індекси в межах read-only аудиту.

## Позитивні результати, які слід зберегти

- Internal ID search→detail для вибірки12 працює; movie/series/anime/manga не підміняються при відкритті цих records.
- Пошук uk-назви «Початок» знаходить потрібний тайтл, english case-insensitive Naruto працює.
- Основні year/IMDb rating/genre/exclusion/actor/director-singular/keyword/country/language/votes/seasons/trailer/provider filters працюють на **заповненому fixture**; data gaps відділено від code bugs.
- Пагінація anime page1–3 не мала overlap; невідомі type/genre дають400; auth gates discover/collections/me дають401.
- Backend presets повертає3 коректні непорожні списки; ошибка у frontend adapter, не в самому генераторі presets.
- Metadata by-text fallback і multi-seed endpoints відповідають; це не доказ готової vector search.

## Рекомендований порядок наступного виправлення

1. SC-01 envelopes збірників та SC-02/03 anime/manga filters/rating — contract E2E.
2. SC-04 TV taxonomy + SC-05 session seen/cursor/refill.
3. SC-08/12 credit/country/runtime model узгодити **до** масового metadata pass SC-06/07.
4. SC-09/10/11 validation і комбіновані фільтри; SC-14 performance gates на representative dataset.
5. Golden search/identity/recommendation set, aliases/relationships, staging з актуальними образами; production rollout окремо після приймання.

Не запускати новий необмежений імпорт, deployment чи polling @for_movie_tinder_bot лише на підставі цього звіту. Live Telegram messages/playback усіх зовнішніх watch links, всі2.86млн карток вручну, всі можливі комбінації фільтрів і векторний пошук не перевірені; наведений аудит систематично покриває ключові сценарії та докази, а не заявляє математичну повноту.

## Артефакти й відтворення

Каталог [search-catalog-audit](search-catalog-audit/): live_matrix.py + live-matrix.json (85 public cases), fixture.sql + fixture_matrix.py + fixture-matrix.json (47), identity-and-semantic.json, collections-contract-probe.json, credits-and-rating-probes.json, live-data-coverage.csv, tv-taxonomy.csv, embeddings-count.txt, summary.json, screenshots anime-format-bug.png / discover-exhausted.png / collections-empty.png.

Fixture SQL та стандартний e2e/seed_fixture.sql виконуються **тільки** на disposable PostgreSQL після перевірки project ownership. Стандартний seed містить TRUNCATE і не призначений для user DB. Раннер завершується nonzero при відхиленнях; очікування та пропозиції валідації прочитати перед застосуванням як release gate.

## Environment Cleanup Report

Після завершення всіх перевірок і збереження звіту прибирається тільки project **codex-search-audit-20261109**. Точні IDs/mounts — [resource-registry.json](search-catalog-audit/resource-registry.json), фактичний результат останньої фази — [CLEANUP.md](search-catalog-audit/CLEANUP.md). Production moviediscovery-* та сторонні scb-* залишаються недоторканими.
