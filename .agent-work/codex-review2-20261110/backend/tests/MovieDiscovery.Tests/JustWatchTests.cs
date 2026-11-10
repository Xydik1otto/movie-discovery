using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watch;

namespace MovieDiscovery.Tests;

/// <summary>Мапінг monetization_type JustWatch → kind providers (рішення власника: усі типи монетизації).</summary>
public class JustWatchKindMapperTests
{
    [Theory]
    [InlineData("FREE", "free")]
    [InlineData("ADS", "free")]
    [InlineData("FLATRATE", "stream")]
    [InlineData("RENT", "rent")]
    [InlineData("BUY", "buy")]
    [InlineData("flatrate", "stream")] // регістр відповіді не критичний
    public void Map_Known(string mt, string kind) => Assert.Equal(kind, JustWatchMapper.MapKind(mt));

    [Theory]
    [InlineData("CINEMA")]
    [InlineData("")]
    [InlineData(null)]
    public void Map_Unknown_Null(string? mt) => Assert.Null(JustWatchMapper.MapKind(mt));
}

/// <summary>Нормалізація назв провайдерів: clearName > technicalName, порожнє → null.</summary>
public class JustWatchProviderNameTests
{
    [Theory]
    [InlineData("Apple TV", "appletvplus", "Apple TV")]
    [InlineData("  Netflix  ", "netflix", "Netflix")]
    [InlineData(null, "netflix", "netflix")]
    [InlineData("", "max", "max")]
    [InlineData("   ", "  ", null)]
    [InlineData(null, null, null)]
    public void Normalize(string? clear, string? tech, string? expected) =>
        Assert.Equal(expected, JustWatchMapper.ProviderName(clear, tech));
}

/// <summary>Дедуп офферів (пакет повторюється на кожен presentation-type) і побудова Provider-рядків.</summary>
public class JustWatchBuildProvidersTests
{
    private static JustWatchOffer Offer(string mt, string? url, string? clear, string? tech) => new(mt, url, clear, tech);

    [Fact]
    public void Dedupes_Presentation_Types_And_Skips_Unknown()
    {
        var now = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var offers = new[]
        {
            Offer("RENT", "https://tv.apple.com/ua/", "Apple TV Store", "itunes"),
            Offer("RENT", "https://tv.apple.com/ua/", "Apple TV Store", "itunes"), // дублікат пакет+kind
            Offer("BUY", "https://tv.apple.com/ua/", "Apple TV Store", "itunes"),
            Offer("FLATRATE", "https://www.netflix.com/ua/", "Netflix", "netflix"),
            Offer("CINEMA", "https://kino.example/", "Kino", "kino"), // невідома монетизація
            Offer("RENT", null, "   ", ""), // без назви пакета
        };

        var rows = JustWatchMapper.BuildProviders(7, offers, "UA", now);

        Assert.Equal(3, rows.Count); // itunes-rent, itunes-buy, netflix-stream
        Assert.All(rows, r =>
        {
            Assert.Equal(7, r.TitleId);
            Assert.Equal("justwatch", r.Source);
            Assert.Equal("UA", r.Region);
            Assert.Equal(now, r.UpdatedAt);
        });
        Assert.Contains(rows, r => r.ProviderName == "Apple TV Store" && r.Kind == "rent" && r.Url == "https://tv.apple.com/ua/");
        Assert.Contains(rows, r => r.ProviderName == "Apple TV Store" && r.Kind == "buy");
        Assert.Contains(rows, r => r.ProviderName == "Netflix" && r.Kind == "stream");
    }

    [Fact]
    public void Empty_Offers_No_Rows() =>
        Assert.Empty(JustWatchMapper.BuildProviders(1, [], "UA", DateTime.UtcNow));
}

/// <summary>Парсинг відповіді popularTitles (фіксстура — мінімальна за зразком живого jw_response.json).</summary>
public class JustWatchParseTests
{
    private const string PageJson = """
        {"data":{"popularTitles":{"edges":[
          {"node":{"id":"tm10","objectType":"MOVIE","content":{"title":"Матриця","originalReleaseYear":1999,"fullPath":"","externalIds":{"imdbId":"tt0133093","tmdbId":"603"}},
           "offers":[{"monetizationType":"RENT","standardWebURL":"https://tv.apple.com/ua/x","package":{"clearName":"Apple TV Store","technicalName":"itunes"}},
                     {"monetizationType":"FLATRATE","standardWebURL":null,"package":{"clearName":"Netflix","technicalName":"netflix"}}]}},
          {"node":{"id":"ts1","objectType":"SHOW","content":{"title":"Без офферів","originalReleaseYear":2020,"externalIds":{"imdbId":"tt111"}},"offers":[]}}
        ],"pageInfo":{"hasNextPage":true,"endCursor":"Mg=="}}}}
        """;

    [Fact]
    public void Parses_Titles_Offers_Pagination()
    {
        var page = JustWatchMapper.ParseSearch(PageJson);

        Assert.Equal(2, page.Titles.Count);
        Assert.True(page.HasNextPage);
        Assert.Equal("Mg==", page.EndCursor);

        var m = page.Titles[0];
        Assert.Equal("tm10", m.JwId);
        Assert.Equal("MOVIE", m.ObjectType);
        Assert.Equal("Матриця", m.Title);
        Assert.Equal(1999, m.Year);
        Assert.Equal("tt0133093", m.ImdbId);
        Assert.Equal("603", m.TmdbId);
        Assert.Equal(2, m.Offers.Count);
        Assert.Equal("RENT", m.Offers[0].MonetizationType);
        Assert.Equal("itunes", m.Offers[0].PackageTechnicalName);
        Assert.Null(m.Offers[1].StandardWebUrl); // без URL — null, не виключення
        Assert.Empty(page.Titles[1].Offers);
    }

    [Fact]
    public void Graphql_Errors_Throw() => Assert.Throws<InvalidOperationException>(() =>
        JustWatchMapper.ParseSearch("""{"errors":[{"message":"boom"}],"data":null}"""));

    [Fact]
    public void Null_Connection_Is_Empty_Page() =>
        Assert.Empty(JustWatchMapper.ParseSearch("""{"data":{"popularTitles":null}}""").Titles);
}

/// <summary>Матчинг результатів пошуку: imdb точний > фолбек назва+рік.</summary>
public class JustWatchMatchTests
{
    [Fact]
    public void Imdb_Match_Wins()
    {
        var results = new[]
        {
            new JustWatchTitle("a", "MOVIE", "Матриця", 1999, "tt0133093", null, []),
            new JustWatchTitle("b", "MOVIE", "The Matrix", 1999, "tt999", null, []),
        };
        Assert.Same(results[0], JustWatchMapper.MatchByImdb(results, "TT0133093")); // регістр не критичний
        Assert.Null(JustWatchMapper.MatchByImdb(results, "tt42"));
    }

    [Fact]
    public void TitleYear_Fallback()
    {
        var results = new[]
        {
            new JustWatchTitle("a", "MOVIE", "Матриця", 1999, null, null, []),
            new JustWatchTitle("b", "MOVIE", "Інша", 1998, null, null, []),
        };
        Assert.Same(results[0], JustWatchMapper.MatchByTitleYear(results, "матриця", 1999));
        Assert.Null(JustWatchMapper.MatchByTitleYear(results, "нема", 1999));
    }
}

/// <summary>Upsert-семантика ReplaceProvidersAsync: stale justwatch → delete, чужі джерела не чіпаємо, ідемпотентно.</summary>
public class JustWatchReplaceProvidersTests
{
    [Fact]
    public async Task Replaces_Stale_Keeps_Other_Sources()
    {
        await using var db = TestDb.Create("jw-replace-1");
        var t = new Title { Type = "movie", TitleEn = "X", ImdbId = "tt1" };
        db.Titles.Add(t);
        await db.SaveChangesAsync();
        db.Providers.Add(new Provider { TitleId = t.Id, ProviderName = "Old", Kind = "stream", Source = "justwatch", Region = "UA" });
        db.Providers.Add(new Provider { TitleId = t.Id, ProviderName = "Sweet.TV", Kind = "free", Source = "sweettv", Region = "UA" });
        await db.SaveChangesAsync();

        var fresh = JustWatchMapper.BuildProviders(t.Id,
            [new JustWatchOffer("RENT", "https://tv.apple.com/ua/x", "Apple TV Store", "itunes")], "UA", DateTime.UtcNow);
        await JustWatchSyncService.ReplaceProvidersAsync(db, t.Id, fresh, default);

        var rows = await db.Providers.Where(p => p.TitleId == t.Id).ToListAsync();
        Assert.Equal(2, rows.Count); // stale justwatch видалено, sweettv живий, 1 новий justwatch
        Assert.Contains(rows, r => r.Source == "sweettv" && r.ProviderName == "Sweet.TV");
        Assert.Contains(rows, r => r.Source == "justwatch" && r.ProviderName == "Apple TV Store" && r.Kind == "rent");

        // повторний прогін — той самий набір (ідемпотентність)
        await JustWatchSyncService.ReplaceProvidersAsync(db, t.Id, fresh, default);
        Assert.Equal(2, await db.Providers.CountAsync(p => p.TitleId == t.Id));
    }

    [Fact]
    public async Task Empty_Rows_Clears_Justwatch_Only()
    {
        await using var db = TestDb.Create("jw-replace-2");
        var t = new Title { Type = "movie", TitleEn = "Y", ImdbId = "tt2" };
        db.Titles.Add(t);
        await db.SaveChangesAsync();
        db.Providers.Add(new Provider { TitleId = t.Id, ProviderName = "Old", Kind = "buy", Source = "justwatch", Region = "UA" });
        db.Providers.Add(new Provider { TitleId = t.Id, ProviderName = "Sweet.TV", Kind = "free", Source = "sweettv", Region = "UA" });
        await db.SaveChangesAsync();

        await JustWatchSyncService.ReplaceProvidersAsync(db, t.Id, [], default);

        var left = await db.Providers.Where(p => p.TitleId == t.Id).ToListAsync();
        Assert.Single(left);
        Assert.Equal("sweettv", left[0].Source);
    }
}

/// <summary>JustWatchSyncService на InMemory: популяр-прохід і точковий пошук за стаб-GraphQL (без HTTP).</summary>
public class JustWatchSyncServiceTests
{
    private const string PopularJson = """
        {"data":{"popularTitles":{"edges":[
          {"node":{"id":"tm10","objectType":"MOVIE","content":{"title":"The Matrix","originalReleaseYear":1999,"externalIds":{"imdbId":"tt0133093"}},
           "offers":[{"monetizationType":"FLATRATE","standardWebURL":"https://www.netflix.com/ua/title/80002515","package":{"clearName":"Netflix","technicalName":"netflix"}}]}},
          {"node":{"id":"ts999","objectType":"SHOW","content":{"title":"Невідомий","originalReleaseYear":2020,"externalIds":{"imdbId":"tt9999991"}},"offers":[]}}
        ],"pageInfo":{"hasNextPage":false}}}}
        """;

    private const string SearchJson = """
        {"data":{"popularTitles":{"edges":[
          {"node":{"id":"tm92641","objectType":"MOVIE","content":{"title":"Початок","originalReleaseYear":2010,"externalIds":{"imdbId":"tt1375666"}},
           "offers":[{"monetizationType":"RENT","standardWebURL":"https://tv.apple.com/ua/x","package":{"clearName":"Apple TV Store","technicalName":"itunes"}}]}}
        ],"pageInfo":{"hasNextPage":false}}}}
        """;

    private static JustWatchSyncService NewService(MovieDiscoveryDbContext db, StubHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") }, db,
            Options.Create(new JustWatchSyncOptions { RequestDelayMs = 0, RetryBaseDelayMs = 0 }), Logs.Null<JustWatchSyncService>());

    [Fact]
    public async Task Popular_Walk_Matches_Catalog_By_Imdb()
    {
        await using var db = TestDb.Create("jw-popular");
        db.Titles.Add(new Title { Type = "movie", TitleEn = "The Matrix", ImdbId = "tt0133093", Popularity = 50 });
        await db.SaveChangesAsync();

        var service = NewService(db, new StubHttpHandler(_ => StubHttp.Json(PopularJson)));
        var result = await service.RunAsync(maxPages: 1, limit: 0);

        Assert.Equal(1, result.Pages);
        Assert.Equal(2, result.PopularSeen);
        Assert.Equal(1, result.PopularMatched); // tt9999991 без офферів — пропущено

        var rows = await db.Providers.Where(p => p.Source == "justwatch").ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Netflix", rows[0].ProviderName);
        Assert.Equal("stream", rows[0].Kind); // FLATRATE
        Assert.Equal("https://www.netflix.com/ua/title/80002515", rows[0].Url);
        Assert.Equal("UA", rows[0].Region);

        // sync_state записано
        var state = await db.SyncStates.SingleAsync(s => s.Source == "justwatch");
        Assert.Equal("providers", state.Kind);
        Assert.Equal(1, state.LastCount);

        // повторний прогін — ідемпотентно (все ще 1 рядок)
        await service.RunAsync(maxPages: 1, limit: 0);
        Assert.Equal(1, await db.Providers.CountAsync(p => p.Source == "justwatch"));
    }

    [Fact]
    public async Task Search_Phase_Matches_By_Imdb_And_Skips_Misses()
    {
        await using var db = TestDb.Create("jw-search");
        db.Titles.AddRange(
            new Title { Type = "movie", TitleEn = "Inception", Year = 2010, ImdbId = "tt1375666", Popularity = 60 },
            new Title { Type = "series", TitleEn = "Missing", Year = 2020, ImdbId = "tt0000001", Popularity = 40 });
        await db.SaveChangesAsync();

        var service = NewService(db, new StubHttpHandler(_ => StubHttp.Json(SearchJson)));
        var result = await service.RunAsync(maxPages: 0, limit: 5);

        Assert.Equal(2, result.Searched);
        Assert.Equal(1, result.TitlesWithOffers); // SearchJson містить лише tt1375666
        var rows = await db.Providers.Where(p => p.Source == "justwatch").ToListAsync();
        Assert.Single(rows);
        Assert.Equal("Apple TV Store", rows[0].ProviderName);
        Assert.Equal("rent", rows[0].Kind);
    }

    [Fact]
    public async Task Search_Skips_Titles_Already_Having_Providers()
    {
        await using var db = TestDb.Create("jw-search-skip");
        var t = new Title { Type = "movie", TitleEn = "Inception", Year = 2010, ImdbId = "tt1375666", Popularity = 60 };
        db.Titles.Add(t);
        await db.SaveChangesAsync();
        db.Providers.Add(new Provider { TitleId = t.Id, ProviderName = "Netflix", Kind = "stream", Source = "justwatch", Region = "UA" });
        await db.SaveChangesAsync();

        var requests = 0;
        var handler = new StubHttpHandler(_ => { requests++; return StubHttp.Json(SearchJson); });
        var service = NewService(db, handler);
        var result = await service.RunAsync(maxPages: 0, limit: 5);

        Assert.Equal(0, result.Searched); // кандидат відфільтрований NOT EXISTS
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Http_Failure_Is_Skipped_Not_Fatal()
    {
        await using var db = TestDb.Create("jw-http-fail");
        db.Titles.Add(new Title { Type = "movie", TitleEn = "Inception", Year = 2010, ImdbId = "tt1375666", Popularity = 60 });
        await db.SaveChangesAsync();

        var service = NewService(db, new StubHttpHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway) { Content = new StringContent("") }));
        var result = await service.RunAsync(maxPages: 1, limit: 5);

        Assert.Equal(0, result.PopularMatched); // батч пропущено після ретраїв
        Assert.Equal(0, await db.Providers.CountAsync(p => p.Source == "justwatch"));
        Assert.Equal(1, result.Searched); // кандидат було спробовано (search теж 502 — skip per-title)
        Assert.Equal(0, result.TitlesWithOffers);
    }
}
