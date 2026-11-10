using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watch;

namespace MovieDiscovery.Tests;

/// <summary>
/// Парсер/матчер/синк Plex за патернами РЕАЛЬНИХ зразків (розвідка 09.11, зразки в scratch,
/// у тестах — маленькі фікстури за тими самими патернами; реальних HTTP-викликів немає).
/// </summary>
public class PlexParserTests
{
    // Патерн year-сторінки /movie-database/1999: <li><span…><a href="/movie/{slug}"><span…>Назва</span></a></span></li>
    // слаг буває і з роком, і без; назви — entities і не-латиниця (реальні приклади зі зразка).
    internal const string YearPageHtml = """
        <h1>1999 — Movie Database</h1><ul>
        <li><span class="dnbk0g1w dnbk0g1y dnbk0g2e"><a aria-disabled="false" class="_1r6wp5u0 w01fra1" href="/movie/american-pie-1999"><span class="dnbk0g1w dnbk0g1x">American Pie</span></a></span></li>
        <li><span class="dnbk0g1w dnbk0g1y dnbk0g2e"><a aria-disabled="false" class="_1r6wp5u0 w01fra1" href="/movie/10-things-i-hate-about-you"><span class="dnbk0g1w dnbk0g1x">10 Things I Hate About You</span></a></span></li>
        <li><span class="dnbk0g1w dnbk0g1y dnbk0g2e"><a aria-disabled="false" class="_1r6wp5u0 w01fra1" href="/movie/the-matrix"><span class="dnbk0g1w dnbk0g1x">The Matrix</span></a></span></li>
        <li><span class="dnbk0g1w dnbk0g1y dnbk0g2e"><a aria-disabled="false" class="_1r6wp5u0 w01fra1" href="/movie/eyes-wide-shut"><span class="dnbk0g1w dnbk0g1x">Eyes Wide Shut</span></a></span></li>
        <li><span class="dnbk0g1w dnbk0g1y dnbk0g2e"><a aria-disabled="false" class="_1r6wp5u0 w01fra1" href="/movie/mr-hollands-opus"><span class="dnbk0g1w dnbk0g1x">Mr. Holland&#39;s Opus</span></a></span></li>
        <li><span class="dnbk0g1w dnbk0g1y dnbk0g2e"><a aria-disabled="false" class="_1r6wp5u0 w01fra1" href="/movie/ben-and-me"><span class="dnbk0g1w dnbk0g1x">Tom &amp; Jerry</span></a></span></li>
        <li><span class="dnbk0g1w dnbk0g1y dnbk0g2e"><a aria-disabled="false" class="_1r6wp5u0 w01fra1" href="/movie/tu-er-ye"><span class="dnbk0g1w dnbk0g1x">兔儿爷</span></a></span></li>
        <li><span class="dnbk0g1w dnbk0g1y dnbk0g2e"><a aria-disabled="false" class="_1r6wp5u0 w01fra1" href="/movie/the-matrix"><span class="dnbk0g1w dnbk0g1x">The Matrix</span></a></span></li>
        </ul>
        """;

    // Патерн хаба /discover: aria-label="Назва (Рік)" + href="/movie/{slug}"; серіали йдуть по /show/ — ігноруються.
    private const string DiscoverCardsHtml = """
        <li class="xhahwja"><div class="po8e0t1"><figure class="_13usayk0"><a data-id="tv.plex.provider.discover-5d776d53fb0d55001f59d4d4" aria-disabled="false" aria-label="24 Hours to Live (2017)" class="_1r6wp5u0 po8e0t0" href="/movie/24-hours-to-live-2017"></a></figure></div></li>
        <li class="xhahwja"><div class="po8e0t1"><figure class="_13usayk0"><a data-id="tv.plex.provider.discover-abc123" aria-disabled="false" aria-label="Bad Santa (2003)" class="_1r6wp5u0 po8e0t0" href="/movie/bad-santa-2003"></a></figure></div></li>
        <li class="xhahwja"><div class="po8e0t1"><figure class="_13usayk0"><a data-id="tv.plex.provider.discover-noyear" aria-disabled="false" aria-label="Forgotten Island" class="_1r6wp5u0 po8e0t0" href="/movie/forgotten-island"></a></figure></div></li>
        <li class="xhahwja"><div class="po8e0t1"><figure class="_13usayk0"><a data-id="tv.plex.provider.discover-show" aria-disabled="false" aria-label="Breaking Bad (2008)" class="_1r6wp5u0 po8e0t0" href="/show/breaking-bad"></a></figure></div></li>
        """;

    // Реальний формат RSC-політу (з plex_discover.html): push-чанки з \"-екрапованим JSON усередині.
    // Форма 1 (hub-елемент): previewData ДО link в одному об'єкті. Форма 2 (trending): accessibilityLabel
    // (назва) безпосередньо перед actions[] з goToMovie-лінком; previewData сусіда — далеко ззаду.
    // Назва з екранованими лапками — перевірка декодування.
    private const string FlightHtml = """
        <script>(self.__next_f=self.__next_f||[]).push([0])</script><script>self.__next_f.push([1,"0:{\"previewData\":{\"title\":\"24 Hours to Live\"},\"label\":\"More\",\"metrics\":{\"click\":{\"event\":\"client:click\"}},\"link\":{\"url\":\"/movie/24-hours-to-live-2017\",\"external\":false},\"metrics\":{\"impression\":{\"itemId\":\"5d776d53fb0d55001f59d4d4\",\"itemType\":\"movie\"}}}\n"])</script><script>self.__next_f.push([1,"1:{\"previewData\":{\"title\":\"Bad \\\"Santa\\\" ( unrated )\"},\"link\":{\"url\":\"/movie/bad-santa-2003\",\"external\":false}}\n"])</script><script>self.__next_f.push([1,"2:{\"content\":[{\"imagePosition\":\"front\",\"orientation\":\"landscape\",\"size\":\"m\",\"accessibilityLabel\":\"Small Town Trouble: Texas\",\"actions\":[{\"visible\":true,\"icon\":\"movie\",\"id\":\"goToMovie\",\"label\":\"Go to Movie\",\"link\":{\"url\":\"/movie/small-town-trouble-texas-2026\",\"external\":false},\"metrics\":{\"click\":{\"event\":\"client:click\",\"interaction\":true,\"properties\":{\"context\":\"discover.trending:hub.home.trending-trailers\",\"contextGroupIndex\":0,\"contextItemIndex\":0,\"action\":\"goToMovie\"}}}},{\"visible\":true,\"id\":\"getActionsForMetadataItem\",\"previewData\":{\"title\":\"Small Town Trouble: Texas\"},\"label\":\"More\"}]}]}\n"])</script>
        """;

    private const string YearIndexHtml = """
        <div><a href="/movie-database/1920">1920</a><a href="/movie-database/1999">1999</a>
        <a href="/movie-database/2027">2027</a><a href="/movie-database/1999">1999</a>
        <a href="/live-tv/category/movies">movies</a></div>
        """;

    [Fact]
    public void ParseYearPageLinks_Reads_Real_Pattern()
    {
        var items = PlexParser.ParseYearPageLinks(YearPageHtml, 1999);

        Assert.Equal(7, items.Count); // дублікат /movie/the-matrix злитий
        var pie = items.Single(i => i.Slug == "american-pie-1999");
        Assert.Equal("American Pie", pie.Title);
        Assert.Equal(1999, pie.Year); // рік береться зі сторінки
        Assert.Equal("/movie/american-pie-1999", pie.Href);

        // слаг БЕЗ року — теж валідний (канонічний редірект)
        Assert.Equal("10-things-i-hate-about-you", items[1].Slug);
        Assert.Equal("10 Things I Hate About You", items[1].Title);

        // HTML-entities декодовані
        Assert.Equal("Mr. Holland's Opus", items.Single(i => i.Slug == "mr-hollands-opus").Title);
        Assert.Equal("Tom & Jerry", items.Single(i => i.Slug == "ben-and-me").Title);
        // не-латиниця проходить
        Assert.Equal("兔儿爷", items.Single(i => i.Slug == "tu-er-ye").Title);
    }

    [Fact]
    public void ParseCardList_Reads_AriaLabel_With_Year()
    {
        var items = PlexParser.ParseCardList(DiscoverCardsHtml);

        Assert.Equal(3, items.Count); // /show/breaking-bad відкинуто
        var first = items[0];
        Assert.Equal("24-hours-to-live-2017", first.Slug);
        Assert.Equal("24 Hours to Live", first.Title);
        Assert.Equal(2017, first.Year);

        // картка без року в підписі → Year=null (матчинг це допускає)
        Assert.Null(items.Single(i => i.Slug == "forgotten-island").Year);
    }

    [Fact]
    public void ParseFlightChunks_Concatenates_Real_Chunks()
    {
        var payload = PlexParser.ParseFlightChunks(FlightHtml);

        Assert.Contains("\"previewData\":{\"title\":\"24 Hours to Live\"}", payload);
        Assert.Contains("\"link\":{\"url\":\"/movie/24-hours-to-live-2017\",\"external\":false}", payload);
        Assert.Contains('\"', payload); // екраповані лапки декодовані до звичайних
        Assert.Contains("push([0])", FlightHtml); // push без строкового аргумента ігнорується — перевірка фікстури
        Assert.DoesNotContain("push([0])", payload);
    }

    [Fact]
    public void ParseFlightMovieLinks_Reads_Link_With_PreviewTitle()
    {
        var items = PlexParser.ParseFlightMovieLinks(PlexParser.ParseFlightChunks(FlightHtml));

        Assert.Equal(3, items.Count);
        Assert.Equal("24-hours-to-live-2017", items[0].Slug);
        Assert.Equal("24 Hours to Live", items[0].Title); // форма 1: previewData до лінка
        Assert.Null(items[0].Year); // року в чанках немає

        // екрановані лапки в назві декодовані JSON-парсером
        Assert.Equal("Bad \"Santa\" ( unrated )", items[1].Title);
        Assert.Equal("/movie/bad-santa-2003", items[1].Href);

        // форма 2 (trending): accessibilityLabel того самого content-елемента, а НЕ previewData сусіда
        Assert.Equal("small-town-trouble-texas-2026", items[2].Slug);
        Assert.Equal("Small Town Trouble: Texas", items[2].Title);
    }

    [Fact]
    public void ParseFlightMovieLinks_Falls_Back_To_Slug()
    {
        var payload = "{\"link\":{\"url\":\"/movie/forgotten-island\",\"external\":false}}";
        var items = PlexParser.ParseFlightMovieLinks(payload);
        var item = Assert.Single(items);
        Assert.Equal("forgotten-island", item.Title); // без previewData — назва зі слага
    }

    [Fact]
    public void ParseYearIndex_Reads_Years_Only()
    {
        var years = PlexParser.ParseYearIndex(YearIndexHtml);
        Assert.Equal([1920, 1999, 2027], years); // дублікат злитий, /live-tv не чіпається
    }

    [Theory]
    [InlineData("The Matrix", "thematrix")]
    [InlineData("Mr. Holland's Opus: Director's Cut", "mrhollandsopusdirectorscut")]
    [InlineData("2 Fast 2 Furious", "2fast2furious")]
    [InlineData("Tom & Jerry: The Movie", "tomjerrythemovie")]
    [InlineData("兔儿爷", "兔儿爷")]
    [InlineData("Die Hard – With a Vengeance!", "diehardwithavengeance")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeTitle_Strips_Punctuation_And_Case(string? input, string expected)
    {
        Assert.Equal(expected, PlexParser.NormalizeTitle(input));
    }

    [Fact]
    public void YearPagePath_And_MovieUrl_Are_Canonical()
    {
        Assert.Equal("/movie-database/1999", PlexParser.YearPagePath(1999));
        Assert.Equal("https://watch.plex.tv/movie/the-matrix", PlexParser.MovieUrl("https://watch.plex.tv", "/movie/the-matrix"));
        Assert.Equal("https://watch.plex.tv/movie/the-matrix", PlexParser.MovieUrl("https://watch.plex.tv/", "/movie/the-matrix"));
    }
}

/// <summary>Матчинг і рядки providers (чисті функції).</summary>
public class PlexMatchTests
{
    private static PlexCandidate C(int id, string? en = null, string? uk = null, string? ru = null, int? year = null) =>
        new(id, en, uk, ru, year);

    private static PlexCatalogItem Item(string slug, string title, int? year = null) =>
        new(slug, title, year, $"/movie/{slug}");

    [Fact]
    public void Match_By_En_Title_Within_Year_Window()
    {
        var candidates = new[]
        {
            C(1, "The Matrix", year: 1999),
            C(2, "American Pie", year: 1999),
        };
        var matched = PlexSyncService.MatchItems([Item("the-matrix", "The Matrix", 1999)], candidates, 1999);

        var (item, titleId) = Assert.Single(matched);
        Assert.Equal(1, titleId);
        Assert.Equal("the-matrix", item.Slug);
    }

    [Fact]
    public void Match_By_Uk_And_Ru_Titles()
    {
        var candidates = new[]
        {
            C(5, uk: "Матриця", year: 1999),
            C(6, ru: "Брат", year: 1997),
        };
        var matched = PlexSyncService.MatchItems(
            [Item("brat", "Брат", 1997)], candidates, 1997);

        Assert.Equal(6, Assert.Single(matched).TitleId);
    }

    [Fact]
    public void Match_Year_Window_Is_Enforced()
    {
        var candidates = new[] { C(1, "The Matrix", year: 1996) }; // |Δ| = 3 від 1999
        var matched = PlexSyncService.MatchItems([Item("the-matrix", "The Matrix", 1999)], candidates, 1999);
        Assert.Empty(matched);
    }

    [Fact]
    public void Match_Null_Year_Candidate_Accepts_Page_Year()
    {
        var candidates = new[] { C(9, "Eyes Wide Shut", year: null) };
        var matched = PlexSyncService.MatchItems([Item("eyes-wide-shut", "Eyes Wide Shut", 1999)], candidates, 1999);
        Assert.Equal(9, Assert.Single(matched).TitleId);
    }

    [Fact]
    public void Match_Exact_Year_Wins_Over_Neighbour_Years()
    {
        var candidates = new[]
        {
            C(7, "Foo", year: 1998), // |Δ|=1 — у словнику перший за порядком Id, але 1999 ближчий
            C(4, "Foo", year: 1999), // |Δ|=0
        };
        var matched = PlexSyncService.MatchItems([Item("foo", "Foo", 1999)], candidates, 1999);
        Assert.Equal(4, Assert.Single(matched).TitleId);
    }

    [Fact]
    public void Match_Dedupes_Title_Ids_First_Wins()
    {
        var candidates = new[] { C(1, "The Matrix", year: 1999) };
        var matched = PlexSyncService.MatchItems(
            [Item("the-matrix", "The Matrix", 1999), Item("the-matrix-1", "The Matrix", 1999)],
            candidates, 1999);

        var (item, titleId) = Assert.Single(matched);
        Assert.Equal(1, titleId);
        Assert.Equal("the-matrix", item.Slug); // перший слаг у порядку сторінки виграє
    }

    [Fact]
    public void BuildProvider_Has_Contract_Fields()
    {
        var item = Item("the-matrix", "The Matrix", 1999);
        var p = PlexSyncService.BuildProvider(42, item, "https://watch.plex.tv");

        Assert.Equal(42, p.TitleId);
        Assert.Equal("plex", p.ProviderName);
        Assert.Equal("free", p.Kind);
        Assert.Equal("plex", p.Source);
        Assert.Equal("UA", p.Region);
        Assert.Equal("https://watch.plex.tv/movie/the-matrix", p.Url);
    }
}

/// <summary>Sync на InMemory + стаб-HTTP (реальних HTTP-викликів немає).</summary>
public class PlexSyncServiceTests
{
    private const string Year1999Html = PlexParserTests.YearPageHtml;
    private const string Year2000Html = """
        <li><span><a href="/movie/gladiator-2000"><span>Gladiator</span></a></span></li>
        """;

    private static PlexOptions TestOptions() => new()
    {
        BaseUrl = "https://watch.plex.tv",
        RequestDelayMs = 0, // тести без пауз
        MaxPages = 10,
        YearFrom = 1999,
        YearTo = 2000,
        MaxRetries = 3,
    };

    /// <summary>1999 — 200; 2000 — стійкий 503 (реальний кейс Plex edge) з Retry-After: 0 для швидкості.</summary>
    private static HttpResponseMessage? Respond(HttpRequestMessage req)
    {
        var url = req.RequestUri?.ToString() ?? "";
        if (url.Contains("/movie-database/1999")) return StubHttp.Html(Year1999Html);
        if (url.Contains("/movie-database/2000"))
        {
            var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(""),
            };
            resp.Headers.Add("Retry-After", "0");
            return resp;
        }
        return null;
    }

    private static HttpClient Stub() => StubHttp.Client(Respond);

    [Fact]
    public async Task Sync_Matches_Catalog_And_Writes_Providers()
    {
        var db = TestDb.Create(nameof(Sync_Matches_Catalog_And_Writes_Providers));
        SeedTitles(db);

        var service = new PlexSyncService(Stub(), db, Options.Create(TestOptions()), Logs.Null<PlexSyncService>());
        var result = await service.RunAsync(new PlexSyncJob(MaxPages: 10, YearFrom: 1999, YearTo: 2000), default);

        Assert.Equal(1, result.Pages);       // 1999 узято, 2000 пропущено
        Assert.Equal(1, result.SkippedPages);
        Assert.Equal(7, result.Items);       // тайтлів на сторінці (до матчингу, дублі злиті)
        Assert.Equal(5, result.Matched);     // matrix/pie/things/shut/tu-er-ye; Mr. Holland і Tom&Jerry — без збігів

        var rows = await db.Providers.Where(p => p.Source == "plex").ToListAsync();
        Assert.Equal(5, rows.Count);
        var matrix = rows.Single(p => p.Url!.EndsWith("/movie/the-matrix"));
        Assert.Equal("plex", matrix.ProviderName);
        Assert.Equal("free", matrix.Kind);
        Assert.Equal("UA", matrix.Region);
        Assert.Equal(1, matrix.TitleId); // The Matrix (id=1)

        // American Pie 1999 → id=2
        Assert.Contains(rows, p => p.TitleId == 2 && p.Url!.EndsWith("/movie/american-pie-1999"));
        // null-рік тайтл заматчився з year-сторінки
        Assert.Contains(rows, p => p.TitleId == 4 && p.Url!.EndsWith("/movie/eyes-wide-shut"));
        // не-латинна назва теж матчиться
        Assert.Contains(rows, p => p.TitleId == 5 && p.Url!.EndsWith("/movie/tu-er-ye"));

        var state = await db.SyncStates.SingleAsync(s => s.Source == "plex");
        Assert.Equal("movie", state.Kind);
        Assert.Equal(5, state.LastCount);
        Assert.Equal(1, state.LastPages);
    }

    [Fact]
    public async Task Sync_Is_Idempotent_On_Second_Run()
    {
        var db = TestDb.Create(nameof(Sync_Is_Idempotent_On_Second_Run));
        SeedTitles(db);
        var service = new PlexSyncService(Stub(), db, Options.Create(TestOptions()), Logs.Null<PlexSyncService>());

        await service.RunAsync(new PlexSyncJob(10, 1999, 2000), default);
        var result2 = await service.RunAsync(new PlexSyncJob(10, 1999, 2000), default);

        Assert.Equal(5, result2.Deleted); // заміна: старі 5 знято
        Assert.Equal(5, result2.Added);   // свіжі 5 записано
        var rows = await db.Providers.Where(p => p.Source == "plex").ToListAsync();
        Assert.Equal(5, rows.Count);      // дублів немає (UNIQUE title+provider+kind+source+region)
    }

    [Fact]
    public async Task Sync_Replace_Does_Not_Touch_Other_Windows()
    {
        var db = TestDb.Create(nameof(Sync_Replace_Does_Not_Touch_Other_Windows));
        SeedTitles(db); // Casablanca (1942) у сіді

        // попередній прогін іншого вікна лишив рядок plex на Casablanca
        db.Providers.Add(new Provider
        {
            TitleId = 10,
            ProviderName = "plex",
            Kind = "free",
            Url = "https://watch.plex.tv/movie/casablanca-1942",
            Source = "plex",
            Region = "UA",
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var service = new PlexSyncService(Stub(), db, Options.Create(TestOptions()), Logs.Null<PlexSyncService>());
        await service.RunAsync(new PlexSyncJob(10, 1999, 2000), default); // вікно 1999..2000

        var rows = await db.Providers.Where(p => p.Source == "plex").ToListAsync();
        Assert.Equal(6, rows.Count); // 5 свіжих + Casablanca поза вікном — НЕ чіпнута
        Assert.Contains(rows, p => p.TitleId == 10 && p.Url!.EndsWith("/movie/casablanca-1942"));
    }

    [Fact]
    public async Task Sync_Skips_Year_After_Retries_Exhausted()
    {
        var db = TestDb.Create(nameof(Sync_Skips_Year_After_Retries_Exhausted));
        SeedTitles(db);

        // власний handler — щоб порахувати спроби по 2000
        var handler = new StubHttpHandler(Respond);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") };
        var service = new PlexSyncService(client, db, Options.Create(TestOptions()), Logs.Null<PlexSyncService>());
        var result = await service.RunAsync(new PlexSyncJob(10, 1999, 2000), default);

        Assert.Equal(1, result.SkippedPages); // 2000 віддав 503 на всі спроби
        // MaxRetries=3 → рівно 3 запити на 2000
        Assert.Equal(3, handler.Requests.Count(r => r.Contains("/movie-database/2000")));
    }

    private static void SeedTitles(MovieDiscoveryDbContext db)
    {
        db.Titles.AddRange(
            new Title { Id = 1, Type = "movie", TitleEn = "The Matrix", Year = 1999 },
            new Title { Id = 2, Type = "movie", TitleEn = "American Pie", Year = 1999 },
            new Title { Id = 3, Type = "movie", TitleEn = "10 Things I Hate About You", Year = 1999 },
            new Title { Id = 4, Type = "movie", TitleEn = "Eyes Wide Shut", Year = null }, // реальний кейс: рік невідомий
            new Title { Id = 5, Type = "movie", TitleEn = "兔儿爷", Year = 1999 },          // не-латиниця — збігів не має
            new Title { Id = 6, Type = "series", TitleEn = "The Matrix", Year = 1999 },    // серіал — не матчиться
            new Title { Id = 10, Type = "movie", TitleEn = "Casablanca", Year = 1942 });
        db.SaveChanges();
    }
}
