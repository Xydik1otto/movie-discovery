using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watch;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

/// <summary>Фіксстури Megogo — МАЛЕНЬКІ, збудовані за патернами РЕАЛЬНИХ зразків (розвідка 09.11):
/// колекції free_films_megogo / free_series_megogo — картки "card videoItem" з data-obj-id,
/// data-title, data-vod-rule="advod", якорями /ua/view/{id}-{slug}.html, роком/жанром у
/// video-year/video-country і плашкою free-label; сторінка тайтла — JSON-LD VideoObject.</summary>
public static class MegogoFixtures
{
    public const string CatalogFilmsHtml = """
        <html lang="uk"><head><link rel="canonical" href="https://megogo.ua/ua/collection/free_films_megogo"><meta name="description" content="Безкоштовні фільми дивитися онлайн на MEGOGO"></head>
        <body><div class="catalog-row">
        <div class="card videoItem
                orientation-portrait size-normal"
            data-obj-id="25927966"
            data-title="Серце маєтку"
            data-vod-rule="advod"
            data-video="{&quot;id&quot;: 25927966, &quot;showVideoPopover&quot;: true }"
            data-item-router="video"
            data-tracker-object-type="VOD"
            data-tracker-object-title="Серце маєтку"
            data-access="1"
            data-discount="0"
            data-vod-rule="advod"
        >
            <div class="thumbnail"><div class="thumb">
                <a href="/ua/view/25927966-sertse-maietku.html" title="Серце маєтку">
                    <img src="https://s6.vcdn.biz/static/f/10287225501/image.jpg/pt/r193x272x4" alt="Серце маєтку" loading="lazy">
                </a>
            </div></div>
            <a class="overlay" href="/ua/view/25927966-sertse-maietku.html"></a>
            <div class="card-content video-content">
                <a href="/ua/view/25927966-sertse-maietku.html">
                    <h3 class="video-title card-content-title ">Серце маєтку</h3>
                </a>
                <div class="video-info card-content-info">
                    <span class="video-year">2021</span>,
                    <span class="video-country">Комедії</span>
                </div>
                <div class="free-label">Безкоштовно</div>
            </div>
        </div>
        <div class="card videoItem
                orientation-portrait size-normal"
            data-obj-id="1606801"
            data-title="Голодні ігри"
            data-vod-rule="advod"
            data-video="{&quot;id&quot;: 1606801 }"
            data-tracker-object-type="VOD"
            data-access="1"
            data-vod-rule="advod"
        >
            <div class="thumbnail"><div class="thumb">
                <a href="/ua/view/1606801-golodni-igri.html" title="Голодні ігри">
                    <img src="https://s6.vcdn.biz/static/f/1/image.jpg/pt/r193x272x4" alt="Голодні ігри" loading="lazy">
                </a>
            </div></div>
            <div class="card-content video-content">
                <a href="/ua/view/1606801-golodni-igri.html">
                    <h3 class="video-title card-content-title ">Голодні ігри</h3>
                </a>
                <div class="video-info card-content-info">
                    <span class="video-year">2012</span>,
                    <span class="video-country">Трилери</span>
                </div>
            </div>
        </div>
        <div class="card videoItem
                orientation-portrait size-normal"
            data-obj-id="12345678"
            data-title="Незнана планета"
            data-vod-rule="advod"
            data-tracker-object-type="VOD"
            data-access="1"
            data-vod-rule="advod"
        >
            <div class="thumbnail"><div class="thumb">
                <a href="/ua/view/12345678-neznana-planeta.html" title="Незнана планета">
                    <img src="https://s6.vcdn.biz/static/f/2/image.jpg/pt/r193x272x4" alt="Незнана планета" loading="lazy">
                </a>
            </div></div>
            <div class="card-content video-content">
                <a href="/ua/view/12345678-neznana-planeta.html">
                    <h3 class="video-title card-content-title ">Незнана планета</h3>
                </a>
                <div class="video-info card-content-info">
                    <span class="video-year">2015</span>,
                    <span class="video-country">Фантастика</span>
                </div>
                <div class="free-label">Безкоштовно</div>
            </div>
        </div>
        <div class="card videoItem
                orientation-portrait size-normal"
            data-obj-id="12345679"
            data-title="Безрокова загадка"
            data-vod-rule="advod"
            data-tracker-object-type="VOD"
            data-access="1"
            data-vod-rule="advod"
        >
            <div class="thumbnail"><div class="thumb">
                <a href="/ua/view/12345679-bezrokova-zagadka.html" title="Безрокова загадка">
                    <img src="https://s6.vcdn.biz/static/f/3/image.jpg/pt/r193x272x4" alt="Безрокова загадка" loading="lazy">
                </a>
            </div></div>
            <div class="card-content video-content">
                <a href="/ua/view/12345679-bezrokova-zagadka.html">
                    <h3 class="video-title card-content-title ">Безрокова загадка</h3>
                </a>
                <div class="video-info card-content-info">
                    <span class="video-country">Драми</span>
                </div>
            </div>
        </div>
        </div></body></html>
        """;

    public const string CatalogSeriesHtml = """
        <html lang="uk"><head><link rel="canonical" href="https://megogo.ua/ua/collection/free_series_megogo"><meta name="description" content="Безкоштовні серіали на MEGOGO"></head>
        <body><div class="catalog-row">
        <div class="card videoItem
                orientation-portrait size-normal"
            data-obj-id="10042705"
            data-title="Пуаро"
            data-vod-rule="advod"
            data-video="{&quot;id&quot;: 10042705, &quot;tv&quot;: false }"
            data-item-router="video"
            data-tracker-object-type="VOD"
            data-tracker-object-title="Пуаро"
            data-access="1"
            data-discount="0"
            data-vod-rule="advod"
        >
            <div class="thumbnail"><div class="thumb">
                <a href="/ua/view/10042705-puaro.html" title="Пуаро">
                    <img src="https://s6.vcdn.biz/static/f/2383883511/image.jpg/pt/r193x272x4" alt="Пуаро" loading="lazy">
                </a>
            </div></div>
            <a class="overlay" href="/ua/view/10042705-puaro.html"></a>
            <div class="card-content video-content">
                <a href="/ua/view/10042705-puaro.html">
                    <h3 class="video-title card-content-title ">Пуаро</h3>
                </a>
                <div class="video-info card-content-info">
                    <span class="video-year">1989 - 2013</span>,
                    <span class="video-country">Детективи</span>
                </div>
                <div class="free-label">Безкоштовно</div>
            </div>
        </div>
        </div></body></html>
        """;

    /// <summary>Головна з посиланням /ua/view/* АЛЕ без "card videoItem" — редірект-фолбек: 0 карток.</summary>
    public const string HomepageLikeHtml = """
        <html lang="uk"><body>
        <a href="/ua/view/26613576-poker-face.html" title="Покерфейс">x</a>
        <a href="/ua/view/28004676-inshi.html" title="Інші">x</a>
        </body></html>
        """;

    /// <summary>Підміна при авто-редіректі 302 → /ua: картки Є, але canonical — головна (не колекція).</summary>
    public const string ContaminatedHtml = """
        <html lang="uk"><head><link rel="canonical" href="https://megogo.ua/ua"></head>
        <body><div class="catalog-row">
        <div class="card videoItem
                orientation-portrait size-normal"
            data-obj-id="99988877"
            data-title="Чужа картка"
            data-vod-rule="advod"
            data-tracker-object-type="VOD"
            data-access="1"
            data-vod-rule="advod"
        >
            <div class="thumbnail"><div class="thumb">
                <a href="/ua/view/99988877-chuzha-kartka.html" title="Чужа картка">
                    <img src="https://s6.vcdn.biz/static/f/9/image.jpg/pt/r193x272x4" alt="Чужа картка" loading="lazy">
                </a>
            </div></div>
            <div class="card-content video-content">
                <a href="/ua/view/99988877-chuzha-kartka.html">
                    <h3 class="video-title card-content-title ">Чужа картка</h3>
                </a>
                <div class="video-info card-content-info">
                    <span class="video-year">2020</span>,
                    <span class="video-country">Комедії</span>
                </div>
                <div class="free-label">Безкоштовно</div>
            </div>
        </div>
        </div></body></html>
        """;

    /// <summary>VideoObject JSON-LD зі сторінки /ua/view/1003951-*.html (реальний патерн, ключі спрощені).</summary>
    public const string ViewHtml = """
        <html lang="uk"><head><title>Хроніки Нарнії: Лев, чаклунка та шафа (2005) дивитися онлайн | MEGOGO</title></head>
        <body><script type="application/ld+json">
        {
          "@type" : "VideoObject",
          "@context" : "https://schema.org",
          "name" : "Хроніки Нарнії: Лев, чаклунка та шафа",
          "description" : "<p>повнометражний фільм 2005 року</p>",
          "uploadDate" : "2018-12-20T00:00:00Z",
          "duration" : "PT2H14M36S",
          "embedUrl" : "https://embed.megogo.net/ev3/player?id=1003951",
          "alternativeHeadline" : "The Chronicles of Narnia: The Lion, the Witch and the Wardrobe",
          "genre" : "Пригоди, Сімейні, Фентезі",
          "contentRating" : "12+"
        }
        </script></body></html>
        """;
}

/// <summary>Парсер Megogo за патернами реальних зразків: картки колекцій + VideoObject JSON-LD.</summary>
public class MegogoParserTests
{
    [Fact]
    public void ParseCatalogPage_Reads_Id_Slug_Title_Year_Genre()
    {
        var items = MegogoParser.ParseCatalogPage(MegogoFixtures.CatalogFilmsHtml);

        Assert.Equal(4, items.Count);
        var first = items[0];
        Assert.Equal(25927966, first.Id);
        Assert.Equal("sertse-maietku", first.Slug);
        Assert.Equal("Серце маєтку", first.TitleUk);
        Assert.Equal(2021, first.Year);
        Assert.Equal("Комедії", first.GenreUk);
        Assert.Equal("https://megogo.ua/ua/view/25927966-sertse-maietku.html", first.Url);
    }

    [Fact]
    public void ParseCatalogPage_Dedupes_Repeated_Anchors()
    {
        // у кожної картки 2-3 однакові якорі (thumb/overlay/content) — має лишитись один елемент
        var items = MegogoParser.ParseCatalogPage(MegogoFixtures.CatalogFilmsHtml);
        Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void ParseCatalogPage_Series_Year_Range_Takes_Start_Year()
    {
        var items = MegogoParser.ParseCatalogPage(MegogoFixtures.CatalogSeriesHtml);

        var puaro = Assert.Single(items);
        Assert.Equal(10042705, puaro.Id);
        Assert.Equal("puaro", puaro.Slug);
        Assert.Equal("Пуаро", puaro.TitleUk);
        Assert.Equal(1989, puaro.Year); // «1989 - 2013» → рік старту
        Assert.Equal("Детективи", puaro.GenreUk);
    }

    [Fact]
    public void ParseCatalogPage_Card_Without_Year_Kept_With_Null()
    {
        var items = MegogoParser.ParseCatalogPage(MegogoFixtures.CatalogFilmsHtml);
        var noYear = items.Single(i => i.Id == 12345679);
        Assert.Null(noYear.Year);
        Assert.Equal("Безрокова загадка", noYear.TitleUk);
    }

    [Fact]
    public void ParseCatalogPage_Homepage_Like_Html_Yields_Nothing()
    {
        // посилання /ua/view/* є, але карток "card videoItem" нема (редірект на головну) → порожньо
        Assert.Empty(MegogoParser.ParseCatalogPage(MegogoFixtures.HomepageLikeHtml));
    }

    [Fact]
    public void ParseCatalogPage_Garbage_Yields_Nothing()
    {
        Assert.Empty(MegogoParser.ParseCatalogPage("not html at all"));
        Assert.Empty(MegogoParser.ParseCatalogPage(""));
    }

    [Fact]
    public void IsCollectionPage_Gates_By_Canonical()
    {
        // справжня колекція — прохід; головна (canonical без /collection/) і чужий слаг — ні
        Assert.True(MegogoParser.IsCollectionPage(MegogoFixtures.CatalogFilmsHtml, "free_films_megogo"));
        Assert.True(MegogoParser.IsCollectionPage(MegogoFixtures.CatalogSeriesHtml, "free_series_megogo"));
        Assert.False(MegogoParser.IsCollectionPage(MegogoFixtures.HomepageLikeHtml, "free_films_megogo"));
        Assert.False(MegogoParser.IsCollectionPage(MegogoFixtures.CatalogFilmsHtml, "free_series_megogo"));
        Assert.False(MegogoParser.IsCollectionPage("", "free_films_megogo"));
    }

    [Fact]
    public void DetectCardKind_Free_Markers()
    {
        Assert.Equal(MegogoParser.KindFree, MegogoParser.DetectCardKind("data-vod-rule=\"advod\""));
        Assert.Equal(MegogoParser.KindFree, MegogoParser.DetectCardKind("<div class=\"free-label\">Безкоштовно</div>"));
        Assert.Null(MegogoParser.DetectCardKind("data-vod-rule=\"sub\"")); // невідомий маркер
    }

    [Fact]
    public void FirstYear_Parses_Single_And_Range()
    {
        Assert.Equal(2021, MegogoParser.FirstYear("2021"));
        Assert.Equal(1989, MegogoParser.FirstYear(" 1989 - 2013 "));
        Assert.Null(MegogoParser.FirstYear(""));
        Assert.Null(MegogoParser.FirstYear("12+"));
    }

    [Fact]
    public void Unescape_Decodes_Attribute_Entities()
    {
        Assert.Equal("Серце \"маєтку\"", MegogoParser.Unescape("Серце &quot;маєтку&quot;"));
        Assert.Equal("Ти — космос", MegogoParser.Unescape("Ти — космос"));
        Assert.Equal("a&b", MegogoParser.Unescape("a&amp;b"));
    }

    [Fact]
    public void ParseVideoObject_Reads_Name_Original_And_Genres()
    {
        var ld = MegogoParser.ParseVideoObject(MegogoFixtures.ViewHtml);

        Assert.NotNull(ld);
        Assert.Equal("Хроніки Нарнії: Лев, чаклунка та шафа", ld!.NameUk);
        Assert.Equal("The Chronicles of Narnia: The Lion, the Witch and the Wardrobe", ld.TitleEn);
        Assert.Equal(["Пригоди", "Сімейні", "Фентезі"], ld.GenresUk);
    }

    [Fact]
    public void ParseVideoObject_Garbage_Returns_Null()
    {
        Assert.Null(MegogoParser.ParseVideoObject("<html><body>порожня сторінка</body></html>"));
        Assert.Null(MegogoParser.ParseVideoObject("<script type=\"application/ld+json\">{broken</script>"));
    }
}

/// <summary>Матчинг uk-форм: нормалізація пунктуації/апострофів/тире + рік ±1 (FR-14 семантика).</summary>
public class MegogoTitleMatchTests
{
    [Fact]
    public void Normalize_Strips_Punctuation_And_Dashes()
    {
        // тире/дефіси/апострофи різних юнікод-форм мають давати однаковий ключ
        Assert.Equal(TitleNormalizer.Normalize("Ти — космос"), TitleNormalizer.Normalize("Ти - космос"));
        Assert.Equal(TitleNormalizer.Normalize("П'ятихвилини"), TitleNormalizer.Normalize("П'ятихвилини"));
        Assert.Equal("серце маєтку", TitleNormalizer.Normalize("Серце маєтку"));
        Assert.Equal("", TitleNormalizer.Normalize("   "));
    }

    [Fact]
    public void Matcher_Matches_Uk_Name_Within_Year_Window()
    {
        var candidates = new List<Title>
        {
            new() { Id = 1, Type = "movie", TitleUk = "Серце маєтку", TitleEn = "Home Sweet Home Alone", Year = 2021 },
            new() { Id = 2, Type = "series", TitleUk = "Пуаро", TitleEn = "Poirot", Year = 1989 },
        };

        Assert.Equal(1, TitleMatcher.Find(candidates, "Серце маєтку", null, 2021)!.Id);
        Assert.Equal(1, TitleMatcher.Find(candidates, "Серце маєтку", null, 2020)!.Id); // ±1
        Assert.Equal(2, TitleMatcher.Find(candidates, "Пуаро", null, 1989)!.Id);
        Assert.Null(TitleMatcher.Find(candidates, "Серце маєтку", null, 2023)); // рік далеко
        Assert.Null(TitleMatcher.Find(candidates, "Інше кіно", null, 2021));
    }

    [Fact]
    public void Matcher_Null_Candidate_Year_Does_Not_Block()
    {
        var candidates = new List<Title>
        {
            new() { Id = 3, Type = "movie", TitleUk = "Голодні ігри", TitleEn = "The Hunger Games", Year = null },
        };
        // рік кандидата невідомий — семантика FR-14: назва вирішує, рік не блокує
        Assert.Equal(3, TitleMatcher.Find(candidates, "Голодні ігри", null, 2012)!.Id);
    }

    [Fact]
    public void Matcher_Uk_Apostrophe_Forms_Match()
    {
        var candidates = new List<Title>
        {
            new() { Id = 4, Type = "movie", TitleUk = "П'ятихвилини", Year = 2019 },
        };
        // різні апострофи (’ U+2019 у назві vs ' у картці) дають той самий нормалізований ключ
        var curly = TitleNormalizer.Normalize("П’ятихвилини");
        Assert.Equal(curly, TitleNormalizer.Normalize("П'ятихвилини"));
        Assert.Equal(4, TitleMatcher.Find(candidates, "П’ятихвилини", null, 2019)!.Id);
        Assert.Equal(4, TitleMatcher.Find(candidates, "П'ятихвилини", null, 2019)!.Id);
    }
}

/// <summary>MegogoSyncService (InMemory + HTTP-стаб): матчинг → providers, заміна рядків, ідемпотентність.</summary>
public class MegogoSyncServiceTests
{
    private static MegogoSyncService NewService(MovieDiscoveryDbContext db, StubHttpHandler handler) =>
        new(new MegogoClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://megogo.ua/") },
                new MegogoThrottle(TimeSpan.Zero), // без тротлінгу в тестах
                Options.Create(new MegogoOptions()),
                Logs.Null<MegogoClient>()),
            db,
            Options.Create(new MegogoOptions()),
            Logs.Null<MegogoSyncService>());

    private static HttpResponseMessage? Respond(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("free_films_megogo?page=2") || url.Contains("free_series_megogo?page=2"))
            return null; // 404 → кінець видачі
        if (url.Contains("free_films_megogo")) return StubHttp.Html(MegogoFixtures.CatalogFilmsHtml);
        if (url.Contains("free_series_megogo")) return StubHttp.Html(MegogoFixtures.CatalogSeriesHtml);
        return null;
    }

    private static async Task SeedCatalogAsync(MovieDiscoveryDbContext db)
    {
        db.Titles.Add(new Title { Type = "movie", TitleUk = "Серце маєтку", TitleEn = "Home Sweet Home Alone", Year = 2021 });
        db.Titles.Add(new Title { Type = "movie", TitleUk = "Голодні ігри", TitleEn = "The Hunger Games", Year = 2013 }); // картка 2012 → ±1
        db.Titles.Add(new Title { Type = "series", TitleUk = "Пуаро", TitleEn = "Poirot", Year = 1989 });
        // рядок іншого джерела — не чіпається мегаґо-синком
        db.Providers.Add(new Provider { TitleId = 1, ProviderName = "Netflix", Kind = "stream", Source = "justwatch", Region = "UA" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sync_Writes_Provider_Rows_Only_For_Confident_Matches()
    {
        await using var db = TestDb.Create("megogo-sync");
        await SeedCatalogAsync(db);
        var handler = new StubHttpHandler(Respond);
        var service = NewService(db, handler);

        var result = await service.SyncAsync(maxPages: 2);

        Assert.Equal(5, result.Cards);        // 4 фільми + 1 серіал
        Assert.Equal(3, result.Matched);      // Серце маєтку, Голодні ігри (±1), Пуаро
        Assert.Equal(2, result.Unmatched);    // Незнана планета (нема кандидата), Безрокова загадка (без року)
        Assert.Equal(3, result.Providers);
        Assert.Equal(3, await db.Titles.CountAsync()); // тайтли НЕ створюються

        var rows = await db.Providers.Where(p => p.Source == "megogo").ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, p =>
        {
            Assert.Equal("Megogo", p.ProviderName);
            Assert.Equal("free", p.Kind);
            Assert.Equal("UA", p.Region);
        });
        Assert.Contains(rows, p => p.Url == "https://megogo.ua/ua/view/25927966-sertse-maietku.html");
        Assert.Contains(rows, p => p.Url == "https://megogo.ua/ua/view/10042705-puaro.html");

        // інше джерело не постраждало
        Assert.Equal(1, await db.Providers.CountAsync(p => p.Source == "justwatch"));
    }

    [Fact]
    public async Task Second_Sync_Replaces_Rows_Idempotently()
    {
        await using var db = TestDb.Create("megogo-idempotent");
        await SeedCatalogAsync(db);
        var service = NewService(db, new StubHttpHandler(Respond));

        var first = await service.SyncAsync(maxPages: 2);
        var afterFirst = await db.Providers.CountAsync(p => p.Source == "megogo");
        Assert.Equal(first.Providers, afterFirst);

        var second = await service.SyncAsync(maxPages: 2);
        Assert.Equal(first.Providers, second.Providers);
        Assert.Equal(afterFirst, await db.Providers.CountAsync(p => p.Source == "megogo")); // без дублів
        Assert.Equal(3, await db.Titles.CountAsync());
    }

    [Fact]
    public async Task Sync_Removes_Stale_Megogo_Rows_Keeps_Other_Sources()
    {
        await using var db = TestDb.Create("megogo-replace");
        await SeedCatalogAsync(db);
        var stale = await db.Titles.FirstAsync(t => t.TitleUk == "Пуаро");
        db.Providers.Add(new Provider
        {
            TitleId = stale.Id, ProviderName = "Megogo", Kind = "free", Source = "megogo", Region = "UA",
            Url = "https://megogo.ua/ua/view/77777777-stare.html",
        });
        await db.SaveChangesAsync();

        var service = NewService(db, new StubHttpHandler(Respond));
        await service.SyncAsync(maxPages: 2);

        var megogoRows = await db.Providers.Where(p => p.Source == "megogo").ToListAsync();
        Assert.Equal(3, megogoRows.Count);
        Assert.DoesNotContain(megogoRows, p => p.Url!.Contains("77777777")); // застарілий знято
        Assert.Equal(1, await db.Providers.CountAsync(p => p.Source == "justwatch")); // чужі джерела цілі
    }

    [Fact]
    public async Task Sync_Stops_When_Pages_Exhausted()
    {
        await using var db = TestDb.Create("megogo-pages");
        await SeedCatalogAsync(db);
        var handler = new StubHttpHandler(Respond);
        var service = NewService(db, handler);

        await service.SyncAsync(maxPages: 5);

        // сторінки 1 і 2 обидвох колекцій, сторінки 3+ НЕ запитувались (стоп після порожньої)
        Assert.Contains(handler.Requests, u => u.EndsWith("/ua/collection/free_films_megogo"));
        Assert.Contains(handler.Requests, u => u.Contains("free_films_megogo?page=2"));
        Assert.Contains(handler.Requests, u => u.EndsWith("/ua/collection/free_series_megogo"));
        Assert.Contains(handler.Requests, u => u.Contains("free_series_megogo?page=2"));
        Assert.DoesNotContain(handler.Requests, u => u.Contains("page=3"));
    }

    [Fact]
    public async Task Sync_Gates_Out_Wrong_Canonical_Pages()
    {
        await using var db = TestDb.Create("megogo-gate");
        await SeedCatalogAsync(db);
        // сторінка 2 фільмів — редірект на головну, що приніс би "чужі" картки: гейт мусить зупинити обхід
        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("free_films_megogo?page=2")) return StubHttp.Html(MegogoFixtures.ContaminatedHtml);
            return Respond(req);
        });
        var service = NewService(db, handler);

        var result = await service.SyncAsync(maxPages: 3);

        Assert.Equal(5, result.Cards); // лише p1 обидвох колекцій; "Чужа картка" НЕ потрапила
        Assert.Equal(3, result.Providers);
        Assert.False(await db.Providers.AnyAsync(p => p.Url!.Contains("chuzha-kartka")));
    }

    [Fact]
    public async Task Sync_Updates_SyncState()
    {
        await using var db = TestDb.Create("megogo-state");
        await SeedCatalogAsync(db);
        var service = NewService(db, new StubHttpHandler(Respond));

        await service.SyncAsync(maxPages: 2);

        var state = await db.SyncStates.SingleAsync(s => s.Source == "megogo");
        Assert.Equal("free_catalog", state.Kind);
        Assert.Equal(3, state.LastCount);
        Assert.NotEqual(default, state.LastRunAt);
    }
}
