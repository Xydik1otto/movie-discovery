namespace MovieDiscovery.Tests;

/// <summary>
/// Маленькі фікстури sweet.tv, створені за ПАТЕРНАМИ реальних зразків
/// (C:/Users/bigpi/AppData/Local/hermes/cache/scratch/vod-recon/sweet_catalog.html,
/// sweet_movie_card.html): Nuxt SSR + JSON-LD schema.org. Структура блоків — реальна,
/// вміст скорочений до 2–3 елементів.
/// </summary>
public static class SweetTvFixtures
{
    /// <summary>Каталог: ld+json ItemList (3 Movie) + href-патерн /ua-uk/movie/{id}-{slug}.</summary>
    public const string CatalogHtml = """
        <!DOCTYPE html><html lang="uk"><head><title>Дивися фільми онлайн безкоштовно в HD в Україні | SWEET.TV</title></head>
        <body><main class="main-content"><a href="/ua-uk/movie/39067-kinec-ouk-strit" data-v-cbec2f9f>
        <div class="ui-media-card ui-media-card--landscape" data-v-9327e116><picture class="ui-media-card__picture"></picture></div></a>
        <a href="/ua-uk/movie/33125-a-minecraft-movie">card</a>
        <script type="application/ld+json">{"@context": "https://schema.org", "@type": "ItemList", "@id": "https://sweet.tv/ua-uk/movie#itemList", "name": "Дивися фільми онлайн безкоштовно в HD в Україні | SWEET.TV", "url": "https://sweet.tv/ua-uk/movie", "itemListOrder": "https://schema.org/ItemListUnordered", "numberOfItems": 3, "itemListElement": [{"@type": "Movie", "@id": "https://sweet.tv/ua-uk/movie/39067-kinec-ouk-strit#movie", "url": "https://sweet.tv/ua-uk/movie/39067-kinec-ouk-strit", "mainEntityOfPage": "https://sweet.tv/ua-uk/movie/39067-kinec-ouk-strit", "name": "Кінець Оук-Стріт", "description": "Після загадкової космічної події вулицю Оук вириває з передмістя.", "image": "https://img.sweet.tv/cdn-cgi/image/format=auto/immutable/movie_poster/895d.webp", "genre": ["Детективи", "Трилери", "Фантастика"], "countryOfOrigin": {"@type": "Country", "name": "США"}, "contentRating": "13+"}, {"@type": "Movie", "@id": "https://sweet.tv/ua-uk/movie/33125-a-minecraft-movie#movie", "url": "https://sweet.tv/ua-uk/movie/33125-a-minecraft-movie", "mainEntityOfPage": "https://sweet.tv/ua-uk/movie/33125-a-minecraft-movie", "name": "Minecraft: Фільм", "description": "Четверо несподівано втягуються крізь таємничий портал у світ Minecraft.", "image": "https://img.sweet.tv/cdn-cgi/image/format=auto/immutable/movie_poster/9ab.webp", "genre": ["Пригоди", "Комедії"], "countryOfOrigin": {"@type": "Country", "name": "США"}, "contentRating": "6+"}, {"@type": "Movie", "@id": "https://sweet.tv/ua-uk/movie/35769-ti-kosmos#movie", "url": "https://sweet.tv/ua-uk/movie/35769-ti-kosmos", "name": "Ти — космос", "description": "Український космічний далекобійник Андрій перевозить ядерні відходи.", "genre": ["Комедії", "Пригоди", "Фантастика"]}]}</script>
        </main></body></html>
        """;

    /// <summary>Каталог без JSON-LD — лише href-фолбек.</summary>
    public const string CatalogNoLdHtml = """
        <!DOCTYPE html><html><body>
        <a href="/ua-uk/movie/24347-rozvagi-doroslih-divchat">1</a>
        <a href="/ua-uk/movie/24347-rozvagi-doroslih-divchat">dup</a>
        <a href="/ua-uk/movie/23055-autsayderi">2</a>
        </body></html>
        """;

    /// <summary>Картка фільма «Ти — космос» (передплата) — реальний патерн ld+json @graph WebPage+Movie.</summary>
    public const string CardSubscriptionHtml = """
        <!DOCTYPE html><html lang="uk"><head>
        <title>Ти — космос (2025) дивитися онлайн українською | SWEET.TV</title>
        <meta name="description" content="Ти - космос ( 2025 ) – U Are the Universe дивитися фільм в Україні на Sweet TV. Вся інформація про фільм: дати виходу, актори, трейлери, відгуки. ⭐ Доступ до фільму у підписці">
        <link rel="canonical" href="https://sweet.tv/ua-uk/movie/35769-ti-kosmos">
        </head><body><div class="movie-content" data-v-34bd404e>
        <button type="button" class="ui-button ui-button--primary header-desktop__actions-button" data-v-22814719>Спробувати безкоштовно</button>
        <i class="movie-content__actions-icon-watch" data-inp-action="watch" data-text="Дивитися безкоштовно" aria-label="Дивитися безкоштовно"></i>
        </div>
        <script type="application/ld+json">{"@context": "https://schema.org", "@type": "FAQPage", "mainEntity": [{"@type": "Question", "name": "Що таке «Ти — космос»?", "acceptedAnswer": {"@type": "Answer", "text": "«Ти — космос» — це український фантастичний фільм 2025 року."}}]}</script>
        <script type="application/ld+json">{"@context": "https://schema.org", "@graph": [{"@type": "WebPage", "@id": "https://sweet.tv/ua-uk/movie/35769-ti-kosmos#webpage", "description": "Ти - космос ( 2025 ) – U Are the Universe дивитися фільм в Україні на Sweet TV. Вся інформація про фільм: дати виходу, актори, трейлери, відгуки. ⭐ Доступ до фільму у підписці", "inLanguage": "uk-UA", "isPartOf": {"@id": "https://sweet.tv/ua-uk#website"}, "mainEntity": {"@id": "https://sweet.tv/ua-uk/movie/35769-ti-kosmos#movie"}, "name": "Ти — космос", "url": "https://sweet.tv/ua-uk/movie/35769-ti-kosmos"}, {"@type": "Movie", "@id": "https://sweet.tv/ua-uk/movie/35769-ti-kosmos#movie", "url": "https://sweet.tv/ua-uk/movie/35769-ti-kosmos", "mainEntityOfPage": {"@id": "https://sweet.tv/ua-uk/movie/35769-ti-kosmos#webpage"}, "name": "Ти — космос", "description": "Ти - космос ( 2025 ) – U Are the Universe дивитися фільм в Україні на Sweet TV.", "image": "https://img.sweet.tv/cdn-cgi/image/format=auto/movie_poster/256b.webp", "dateCreated": "2025", "genre": ["Комедії", "Пригоди", "Фантастика"], "countryOfOrigin": [{"@type": "Country", "name": "Україна"}, {"@type": "Country", "name": "Бельгія"}], "aggregateRating": {"@type": "AggregateRating", "bestRating": "10", "ratingValue": "7.7", "ratingCount": "6437"}}]}</script>
        </body></html>
        """;

    /// <summary>Картка безкоштовного фільма (маркер «безкоштовн» в описі сторінки).</summary>
    public const string CardFreeHtml = """
        <!DOCTYPE html><html lang="uk"><head>
        <title>Ніч живих мерців (1968) дивитися онлайн українською | SWEET.TV</title>
        <meta name="description" content="Night of the Living Dead ( 1968 ) дивитися фільм в Україні на Sweet TV. ⭐ Дивіться фільм безкоштовно з рекламою">
        <link rel="canonical" href="https://sweet.tv/ua-uk/movie/12001-nich-zhivih-mertsiv">
        </head><body>
        <script type="application/ld+json">{"@context": "https://schema.org", "@graph": [{"@type": "WebPage", "@id": "https://sweet.tv/ua-uk/movie/12001-nich-zhivih-mertsiv#webpage", "description": "Night of the Living Dead ( 1968 ) дивитися фільм в Україні на Sweet TV. ⭐ Дивіться фільм безкоштовно з рекламою", "name": "Ніч живих мерців", "url": "https://sweet.tv/ua-uk/movie/12001-nich-zhivih-mertsiv"}, {"@type": "Movie", "@id": "https://sweet.tv/ua-uk/movie/12001-nich-zhivih-mertsiv#movie", "url": "https://sweet.tv/ua-uk/movie/12001-nich-zhivih-mertsiv", "name": "Ніч живих мерців", "dateCreated": "1968", "genre": ["Жахи"]}]}</script>
        </body></html>
        """;

    /// <summary>Картка без дати створення (рік лишається з <title>).</summary>
    public const string CardNoYearHtml = """
        <!DOCTYPE html><html><head>
        <title>Автсайдери (2019) дивитися онлайн українською | SWEET.TV</title>
        <meta name="description" content="Автсайдери ( 2019 ) – Outsiders дивитися фільм в Україні на Sweet TV. ⭐ Доступ до фільму у підписці">
        <link rel="canonical" href="https://sweet.tv/ua-uk/movie/23055-autsayderi">
        </head><body></body></html>
        """;

    /// <summary>Друга картка каталогу (39067) — передплата.</summary>
    public const string CardOukHtml = """
        <!DOCTYPE html><html><head>
        <title>Кінець Оук-Стріт (2025) дивитися онлайн українською | SWEET.TV</title>
        <meta name="description" content="Кінець Оук-Стріт ( 2025 ) дивитися фільм в Україні на Sweet TV. ⭐ Доступ до фільму у підписці">
        <link rel="canonical" href="https://sweet.tv/ua-uk/movie/39067-kinec-ouk-strit">
        </head><body>
        <script type="application/ld+json">{"@context": "https://schema.org", "@graph": [{"@type": "WebPage", "description": "Кінець Оук-Стріт ( 2025 ) дивитися фільм в Україні на Sweet TV. ⭐ Доступ до фільму у підписці", "name": "Кінець Оук-Стріт", "url": "https://sweet.tv/ua-uk/movie/39067-kinec-ouk-strit"}, {"@type": "Movie", "url": "https://sweet.tv/ua-uk/movie/39067-kinec-ouk-strit", "name": "Кінець Оук-Стріт", "dateCreated": "2025", "genre": ["Детективи", "Трилери", "Фантастика"]}]}</script>
        </body></html>
        """;

    /// <summary>Третя картка каталогу (33125) — передплата.</summary>
    public const string CardMinecraftHtml = """
        <!DOCTYPE html><html><head>
        <title>Minecraft: Фільм (2025) дивитися онлайн українською | SWEET.TV</title>
        <meta name="description" content="Minecraft: Фільм ( 2025 ) – A Minecraft Movie дивитися фільм в Україні на Sweet TV. ⭐ Доступ до фільму у підписці">
        <link rel="canonical" href="https://sweet.tv/ua-uk/movie/33125-a-minecraft-movie">
        </head><body>
        <script type="application/ld+json">{"@context": "https://schema.org", "@graph": [{"@type": "WebPage", "description": "Minecraft: Фільм ( 2025 ) – A Minecraft Movie дивитися фільм в Україні на Sweet TV. ⭐ Доступ до фільму у підписці", "name": "Minecraft: Фільм", "url": "https://sweet.tv/ua-uk/movie/33125-a-minecraft-movie"}, {"@type": "Movie", "url": "https://sweet.tv/ua-uk/movie/33125-a-minecraft-movie", "name": "Minecraft: Фільм", "dateCreated": "2025", "genre": ["Пригоди", "Комедії"]}]}</script>
        </body></html>
        """;
}
