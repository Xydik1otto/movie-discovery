using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Tmdb;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

/// <summary>Мапінг списків провайдерів TMDB → kind watch_links.</summary>
public class TmdbProviderKindMapperTests
{
    [Theory]
    [InlineData("flatrate", "subscription")]
    [InlineData("free", "free")]
    [InlineData("rent", "rent")]
    [InlineData("buy", "buy")]
    public void Map_Known_Lists(string list, string kind) => Assert.Equal(kind, TmdbProviderKindMapper.Map(list));

    [Theory]
    [InlineData("ads")]
    [InlineData("cinema")]
    [InlineData("")]
    public void Map_Unknown_Lists_Null(string list) => Assert.Null(TmdbProviderKindMapper.Map(list));

    [Fact]
    public void Lists_Orders_And_Skips_Empty()
    {
        var region = new TmdbWatchRegion
        {
            Link = "https://www.themoviedb.org/watch/123",
            Flatrate = [new TmdbProviderEntry { ProviderId = 8, ProviderName = "Netflix", DisplayPriority = 2 }],
            Rent = [new TmdbProviderEntry { ProviderId = 2, ProviderName = "Apple TV", DisplayPriority = 1 }],
            Buy = null,
            Free = [],
        };
        var mapped = TmdbProviderKindMapper.Lists(region).ToList();

        Assert.Equal(2, mapped.Count); // порожній free — пропущено
        Assert.Equal(("subscription", "Netflix"), (mapped[0].Kind, mapped[0].Entries[0].ProviderName));
        Assert.Equal(("rent", "Apple TV"), (mapped[1].Kind, mapped[1].Entries[0].ProviderName));
    }
}

/// <summary>TmdbProvidersService (FR-14, dormant): без ключа — SyncSkip; з ключем і стабом — створює links.</summary>
public class TmdbProvidersServiceTests
{
    private const string FindJson = """{"movie_results":[{"id":123,"title":"U Are the Universe"}],"tv_results":[]}""";
    private const string ProvidersJson = """
        {"id":123,"results":{"UA":{"link":"https://www.themoviedb.org/movie/123-watch","flatrate":[
            {"provider_id":8,"provider_name":"Netflix","display_priority":1}],
            "rent":[{"provider_id":2,"provider_name":"Apple TV","display_priority":2}],
            "buy":[],"free":[{"provider_id":33,"provider_name":"Sweet.TV","display_priority":3}]}}}
        """;

    private static TmdbProvidersService NewService(MovieDiscoveryDbContext db, StubHttpHandler handler, string apiKey)
    {
        var tmdb = new TmdbClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org/3/") },
            new TestDb.CacheStub(), Options.Create(new TmdbOptions { ApiKey = apiKey }), Logs.Null<TmdbClient>());
        return new TmdbProvidersService(tmdb, db, Logs.Null<TmdbProvidersService>());
    }

    private static HttpResponseMessage Respond(HttpRequestMessage req)
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/find/")) return StubHttp.Json(FindJson);
        if (url.Contains("/watch/providers")) return StubHttp.Json(ProvidersJson);
        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_Key_Throws_SyncSkip_With_Hint()
    {
        await using var db = TestDb.Create("tmdb-prov-nokey");
        var service = NewService(db, new StubHttpHandler(Respond), apiKey: "");

        var ex = await Assert.ThrowsAsync<SyncSkipException>(() => service.SyncAsync(10));
        Assert.Contains("themoviedb.org", ex.UserMessage); // підказка в повідомленні
    }

    [Fact]
    public async Task With_Key_Creates_Links_By_Kind()
    {
        await using var db = TestDb.Create("tmdb-prov-sync");
        db.Titles.AddRange(
            new Title { Type = "movie", TitleEn = "U Are the Universe", ImdbId = "tt35769" },
            new Title { Type = "movie", TitleEn = "No Imdb", ImdbId = null }); // пропускається
        await db.SaveChangesAsync();

        var service = NewService(db, new StubHttpHandler(Respond), apiKey: "test");
        var result = await service.SyncAsync(10);

        Assert.Equal(1, result.TitlesChecked);
        Assert.Equal(3, result.Links); // flatrate→subscription + free→free + rent→rent

        var links = await db.WatchLinks.Where(w => w.Provider == "tmdb-streaming").ToListAsync();
        Assert.Equal(3, links.Count);
        Assert.Contains(links, l => l.Kind == "subscription");
        Assert.Contains(links, l => l.Kind == "free");
        Assert.Contains(links, l => l.Kind == "rent");
        Assert.All(links, l =>
        {
            Assert.Equal("UA", l.Region);
            Assert.True(l.IsAvailable);
            Assert.Equal("https://www.themoviedb.org/movie/123-watch", l.Url);
        });

        // повторний sync — не дублює
        await service.SyncAsync(10);
        Assert.Equal(3, await db.WatchLinks.CountAsync());
    }

    [Fact]
    public async Task Find_Miss_Is_Not_Fatal()
    {
        await using var db = TestDb.Create("tmdb-prov-miss");
        db.Titles.Add(new Title { Type = "series", TitleEn = "Unknown", ImdbId = "tt9999999" });
        await db.SaveChangesAsync();

        var handler = new StubHttpHandler(_ => StubHttp.Json("""{"movie_results":[],"tv_results":[]}"""));
        var service = NewService(db, handler, apiKey: "test");

        var result = await service.SyncAsync(10);
        Assert.Equal(0, result.Links);
    }
}

/// <summary>Фільтр watchable (FR-14): EXISTS watch_links is_available — трансляція в SQL (без БД).</summary>
public class WatchableSearchTests
{
    private static MovieDiscoveryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=translation_check", npg => npg.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    private static Infrastructure.Services.SearchQuery Query(bool? watchable) => new(
        null, null, [], null, null, null, null, null, null, null, null,
        "relevance", 1, 24, Watchable: watchable);

    [Fact]
    public void Watchable_Translates_To_Exists_WatchLinks()
    {
        using var db = CreateContext();
        var sql = new Infrastructure.Services.SearchService(db).BuildQuery(Query(true)).ToQueryString();

        Assert.Contains("watch_links", sql);
        Assert.Contains("is_available", sql);
    }

    [Fact]
    public void Without_Watchable_No_WatchLinks_In_Query()
    {
        using var db = CreateContext();
        var sql = new Infrastructure.Services.SearchService(db).BuildQuery(Query(null)).ToQueryString();

        Assert.DoesNotContain("watch_links", sql);
    }
}
