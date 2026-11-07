using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

/// <summary>Парсери sweet.tv за патернами РЕАЛЬНИХ зразків (FR-14).</summary>
public class SweetTvParserTests
{
    [Fact]
    public void ParseCatalog_Reads_JsonLd_ItemList()
    {
        var items = SweetTvParser.ParseCatalog(SweetTvFixtures.CatalogHtml);

        Assert.Equal(3, items.Count);
        var first = items[0];
        Assert.Equal(39067, first.Id);
        Assert.Equal("kinec-ouk-strit", first.Slug);
        Assert.Equal("Кінець Оук-Стріт", first.NameUk);
        Assert.Equal(["детективи", "трилери", "фантастика"], first.GenresUk.Select(g => g.ToLowerInvariant()).ToArray());
        Assert.Contains("космічної події", first.Description);
    }

    [Fact]
    public void ParseCatalog_Falls_Back_To_Hrefs()
    {
        var items = SweetTvParser.ParseCatalog(SweetTvFixtures.CatalogNoLdHtml);
        Assert.Equal(2, items.Count); // дублікати href прибрано
        Assert.Equal(24347, items[0].Id);
        Assert.Equal("rozvagi-doroslih-divchat", items[0].Slug);
        Assert.Null(items[0].NameUk); // назва дістанеться з картки
    }

    [Fact]
    public void ParseMovieCard_Subscription_Card()
    {
        var card = SweetTvParser.ParseMovieCard(SweetTvFixtures.CardSubscriptionHtml);

        Assert.NotNull(card);
        Assert.Equal(35769, card!.Id);
        Assert.Equal("ti-kosmos", card.Slug);
        Assert.Equal("Ти — космос", card.NameUk);
        Assert.Equal("U Are the Universe", card.TitleEn); // оригінал з опису сторінки
        Assert.Equal(2025, card.Year);                     // dateCreated з JSON-LD Movie
        Assert.Equal(SweetTvParser.KindSubscription, card.Kind);
        Assert.Equal("https://sweet.tv/ua-uk/movie/35769-ti-kosmos", card.Url);
    }

    [Fact]
    public void ParseMovieCard_Free_Card()
    {
        var card = SweetTvParser.ParseMovieCard(SweetTvFixtures.CardFreeHtml);

        Assert.NotNull(card);
        Assert.Equal(12001, card!.Id);
        Assert.Equal("Ніч живих мерців", card.NameUk);
        Assert.Equal(1968, card.Year);
        Assert.Equal(SweetTvParser.KindFree, card.Kind);   // маркер «безкоштовн» в описі сторінки
        Assert.Null(card.TitleEn);                          // «– X» відсутнє → не вигадуємо
    }

    [Fact]
    public void ParseMovieCard_Year_Fallback_From_Title_Tag()
    {
        var card = SweetTvParser.ParseMovieCard(SweetTvFixtures.CardNoYearHtml);
        Assert.NotNull(card);
        Assert.Equal(23055, card!.Id);
        Assert.Equal("Автсайдери", card.NameUk);
        Assert.Equal(2019, card.Year); // з <title>, бо dateCreated нема
        Assert.Equal(SweetTvParser.KindSubscription, card.Kind);
    }

    [Fact]
    public void DetectKind_Ignores_Button_Markers()
    {
        // «Спробувати безкоштовно»/«Дивитися безкоштовно» — CTA на ВСІХ сторінках → ігнорується
        Assert.Equal(SweetTvParser.KindSubscription, SweetTvParser.DetectKind("Доступ до фільму у підписці"));
        Assert.Equal(SweetTvParser.KindFree, SweetTvParser.DetectKind("Дивіться фільм безкоштовно з рекламою"));
        Assert.Equal(SweetTvParser.KindSubscription, SweetTvParser.DetectKind(null));
        Assert.Equal(SweetTvParser.KindSubscription, SweetTvParser.DetectKind("Щось інше"));
    }

    [Fact]
    public void NormalizeGenres_Maps_Uk_Names_To_Canonical()
    {
        var genres = SweetTvParser.NormalizeGenres(["Комедії", "Фантастика", "Бойовики", "Невідомий жанр"]);
        Assert.Equal(["comedy", "science fiction", "action"], genres);
    }

    [Fact]
    public void Parse_Garbage_Returns_Null_Or_Empty()
    {
        Assert.Empty(SweetTvParser.ParseCatalog("not html at all"));
        Assert.Null(SweetTvParser.ParseMovieCard("<html><body>порожня сторінка</body></html>"));
    }
}

/// <summary>SweetTvService (FR-14) на InMemory + HTTP-стаб: upsert titles + watch_links, ідемпотентність.</summary>
public class SweetTvServiceTests
{
    private const int SweetId = 35769;

    private static SweetTvService NewService(MovieDiscoveryDbContext db, StubHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://sweet.tv/") }, db,
            Options.Create(new SweetTvOptions()),
            new IntervalThrottle(TimeSpan.Zero), // без тротлінгу в тестах
            Logs.Null<SweetTvService>());

    private static HttpResponseMessage Respond(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/ua-uk/movie?") || url.EndsWith("/ua-uk/movie"))
            return StubHttp.Html(SweetTvFixtures.CatalogHtml);
        if (url.Contains("/ua-uk/movie/39067-"))
            return StubHttp.Html(SweetTvFixtures.CardOukHtml);
        if (url.Contains("/ua-uk/movie/33125-"))
            return StubHttp.Html(SweetTvFixtures.CardMinecraftHtml);
        if (url.Contains($"/ua-uk/movie/{SweetId}-"))
            return StubHttp.Html(SweetTvFixtures.CardSubscriptionHtml);
        return StubHttp.Html("<html><body>404</body></html>");
    }

    [Fact]
    public async Task Sync_Upserts_Title_And_WatchLink()
    {
        await using var db = TestDb.Create("sweettv-sync");
        var handler = new StubHttpHandler(Respond);
        var service = NewService(db, handler);

        var result = await service.SyncAsync(pages: 1);

        Assert.Equal(3, result.Cards);                 // 3 позиції каталогу, усі картки є
        Assert.Equal(3, result.TitlesCreated);
        Assert.Equal(3, result.Links);

        var title = await db.Titles.SingleAsync(t => t.TitleUk == "Ти — космос");
        Assert.Equal("movie", title.Type);
        Assert.Equal(2025, title.Year);
        Assert.Equal("U Are the Universe", title.TitleEn);
        Assert.Equal(SweetTvService.PopularityConst, title.Popularity);

        var link = await db.WatchLinks.SingleAsync(w => w.TitleId == title.Id);
        Assert.Equal("sweettv", link.Provider);
        Assert.Equal("subscription", link.Kind);
        Assert.Equal("UA", link.Region);
        Assert.True(link.IsAvailable);
        Assert.Contains($"/ua-uk/movie/{SweetId}-", link.Url);

        // прогін відправив каталог + картки (без /search і /payment)
        Assert.DoesNotContain(handler.Requests, u => u.Contains("/search") || u.Contains("/payment"));
    }

    [Fact]
    public async Task Second_Sync_Does_Not_Duplicate()
    {
        await using var db = TestDb.Create("sweettv-idempotent");
        var service = NewService(db, new StubHttpHandler(Respond));

        await service.SyncAsync(pages: 1);
        var titles = await db.Titles.CountAsync();
        var links = await db.WatchLinks.CountAsync();

        var second = await service.SyncAsync(pages: 1);
        Assert.Equal(0, second.TitlesCreated);
        Assert.Equal(3, second.TitlesMatched);
        Assert.Equal(titles, await db.Titles.CountAsync());  // ті самі тайтли
        Assert.Equal(links, await db.WatchLinks.CountAsync()); // ті самі посилання (UNIQUE-логіка)
    }

    [Fact]
    public async Task Sync_Matches_Existing_Tmdb_Title_By_Uk_Name_Year()
    {
        await using var db = TestDb.Create("sweettv-match");
        // Тайтл уже в каталозі (наприклад, з TMDB) — sweet.tv має ДОДАТИ посилання, а не створювати дублікат
        db.Titles.Add(new Title { Type = "movie", TitleEn = "U Are the Universe", TitleUk = "Ти — космос", Year = 2024 });
        await db.SaveChangesAsync();

        var service = NewService(db, new StubHttpHandler(Respond));
        var result = await service.SyncAsync(pages: 1);

        Assert.Equal(1, result.TitlesMatched);
        Assert.Equal(2, result.TitlesCreated); // решта каталогу — нові
        Assert.Equal(3, await db.WatchLinks.CountAsync());

        var matched = await db.Titles.FirstAsync(t => t.TitleEn == "U Are the Universe" && t.Year == 2024);
        Assert.Equal("Ти — космос", matched.TitleUk); // не перезаписано
        Assert.True(await db.WatchLinks.AnyAsync(w => w.TitleId == matched.Id && w.Provider == "sweettv"));
    }

    [Fact]
    public async Task Sync_Catalog_Page_Two_Uses_Page_Query()
    {
        await using var db = TestDb.Create("sweettv-pages");
        var handler = new StubHttpHandler(req =>
            req.RequestUri!.ToString().Contains("page=2")
                ? StubHttp.Html("<html><body>порожня сторінка</body></html>")
                : Respond(req));
        var service = NewService(db, handler);

        await service.SyncAsync(pages: 2);

        Assert.Contains(handler.Requests, u => u.EndsWith("/ua-uk/movie"));   // стор. 1
        Assert.Contains(handler.Requests, u => u.Contains("/ua-uk/movie?page=2")); // стор. 2
    }
}
