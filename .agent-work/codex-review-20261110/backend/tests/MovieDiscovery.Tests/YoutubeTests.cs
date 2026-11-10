using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Tests;

/// <summary>ISO-8601 duration (videos.list contentDetails) + definition → якість.</summary>
public class YoutubeParserTests
{
    [Theory]
    [InlineData("PT1H32M10S", 1, 32, 10)]
    [InlineData("PT58M32S", 0, 58, 32)]
    [InlineData("PT45M", 0, 45, 0)]
    [InlineData("PT2H", 2, 0, 0)]
    [InlineData("P1DT2H30M", 26, 30, 0)] // 1 день + 2:30
    public void ParseDuration_Valid(string iso, int hours, int minutes, int seconds)
    {
        var ts = YoutubeParser.ParseDuration(iso);
        Assert.NotNull(ts);
        Assert.Equal(hours, ts.Value.Hours + 24 * ts.Value.Days);
        Assert.Equal(minutes, ts.Value.Minutes);
        Assert.Equal(seconds, ts.Value.Seconds);
    }

    [Theory]
    [InlineData("PT")]
    [InlineData("garbage")]
    [InlineData("PT0S")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseDuration_Invalid_Returns_Null(string? iso) =>
        Assert.Null(YoutubeParser.ParseDuration(iso));

    [Theory]
    [InlineData("hd", 1080)]
    [InlineData("sd", 480)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void QualityFromDefinition(string? definition, int? expected) =>
        Assert.Equal(expected, YoutubeParser.QualityFromDefinition(definition));

    [Fact]
    public void ParseSearch_Items_And_Skips_NonVideo()
    {
        var json = """
        {
          "items": [
            { "id": { "videoId": "vid1" }, "snippet": { "title": "Se7en Full Movie", "description": "1995", "channelTitle": "Movies", "publishedAt": "2020-01-01" } },
            { "id": { "playlistId": "pl1" } },
            { "id": { "videoId": "vid2" }, "snippet": { "title": "Se7en Trailer" } }
          ]
        }
        """;
        var items = YoutubeParser.ParseSearch(json);
        Assert.Equal(2, items.Count);
        Assert.Equal("vid1", items[0].VideoId);
        Assert.Equal("Se7en Full Movie", items[0].Title);
        Assert.Equal("Movies", items[0].ChannelTitle);
        Assert.Equal("2020-01-01", items[0].PublishedAt);
        Assert.Equal("vid2", items[1].VideoId);
        Assert.Equal("", items[1].Description);
    }

    [Fact]
    public void ParseVideos_Details_With_Thumbnails()
    {
        var json = """
        {
          "items": [
            { "id": "vid1",
              "snippet": { "title": "Se7en (1995) Full Movie", "channelTitle": "MovieHub",
                           "description": "ukrainian subtitles",
                           "thumbnails": { "high": { "url": "https://i.ytimg.com/vi/vid1/hqdefault.jpg" } } },
              "contentDetails": { "duration": "PT2H7M", "definition": "hd" } }
          ]
        }
        """;
        var videos = YoutubeParser.ParseVideos(json);
        Assert.Single(videos);
        var v = videos[0];
        Assert.Equal("vid1", v.VideoId);
        Assert.Equal("PT2H7M", v.Duration);
        Assert.Equal("hd", v.Definition);
        Assert.Equal("https://i.ytimg.com/vi/vid1/hqdefault.jpg", v.ThumbnailUrl);
        Assert.Equal("MovieHub", v.ChannelTitle);
    }
}

/// <summary>Трейлер-фільтр, duration-валідація, матчинг назви, розпізнавання мови, шаблони запитів.</summary>
public class YoutubeFiltersAndMatcherTests
{
    [Theory]
    [InlineData("Se7en Official Trailer (1995)", "", true)]
    [InlineData("Se7en", "Watch the teaser now", true)]
    [InlineData("Se7en", "Official clip: the box scene", true)]
    [InlineData("Se7en (1995) Full Movie", "great movie", false)]
    [InlineData("TRAILER фільму", "", true)] // регістр не має значення
    public void IsTrailer(string title, string description, bool expected) =>
        Assert.Equal(expected, YoutubeFilters.IsTrailer(title, description));

    [Fact]
    public void DurationOk_Movie_Minimum_45_Minutes()
    {
        Assert.True(YoutubeFilters.DurationOk(true, TimeSpan.FromMinutes(45)));
        Assert.False(YoutubeFilters.DurationOk(true, TimeSpan.FromMinutes(44) + TimeSpan.FromSeconds(59)));
        Assert.False(YoutubeFilters.DurationOk(true, null));
    }

    [Fact]
    public void DurationOk_Episode_Minimum_18_Minutes()
    {
        Assert.True(YoutubeFilters.DurationOk(false, TimeSpan.FromMinutes(18)));
        Assert.False(YoutubeFilters.DurationOk(false, TimeSpan.FromMinutes(17) + TimeSpan.FromSeconds(59)));
    }

    private static Title Title(string titleEn, string? titleUk = null, int? year = 1995) =>
        new() { Type = "movie", TitleEn = titleEn, TitleUk = titleUk, Year = year };

    [Fact]
    public void Matches_Title_With_Junk_Suffix_And_Year()
    {
        Assert.True(YoutubeMatcher.Matches(Title("Se7en"), "SE7EN (1995) | Full Movie | David Fincher"));
        Assert.True(YoutubeMatcher.Matches(Title("Breaking Bad", "Бреакінг Бед", 2008),
            "Breaking Bad S01E01 - Full Episode"));
        Assert.True(YoutubeMatcher.Matches(Title("Seven"), "Seven full movie")); // назва як є
    }

    [Fact]
    public void Matches_Ukrainian_Title()
    {
        Assert.True(YoutubeMatcher.Matches(Title("Breaking Bad", "Пуститися зелья", 2008),
            "Пуститися зелья (2008) повний серіал"));
    }

    [Fact]
    public void Matches_Wrong_Year_In_Video_Rejects()
    {
        Assert.False(YoutubeMatcher.Matches(Title("Se7en", year: 1995), "Se7en (2019) full movie")); // сиквел-рік
        Assert.True(YoutubeMatcher.Matches(Title("Se7en", year: 1995), "Se7en (1994) full movie"));  // ±1 ок
        Assert.True(YoutubeMatcher.Matches(Title("Se7en", year: 1995), "Se7en full movie"));         // рік невідомий → не блокує
    }

    [Fact]
    public void Matches_Word_Safety_Short_Titles()
    {
        Assert.False(YoutubeMatcher.Matches(Title("It", year: 2017), "Italian Full Movie HD")); // «it» ⊄ «italian»
        Assert.True(YoutubeMatcher.Matches(Title("It", year: 2017), "It (2017) Full Movie"));
    }

    [Fact]
    public void Matches_Empty_Video_Title_Rejects() =>
        Assert.False(YoutubeMatcher.Matches(Title("Se7en"), ""));

    [Fact]
    public void BuildQuery_By_Type_Contract()
    {
        Assert.Equal("Se7en 1995 full movie", YoutubeQueryBuilder.BuildQuery(Title("Se7en", year: 1995)));
        Assert.Equal("Breaking Bad 2008 full episode",
            YoutubeQueryBuilder.BuildQuery(new Title { Type = "series", TitleEn = "Breaking Bad", Year = 2008 }));
        Assert.Equal("Big Brother season 1 full episode",
            YoutubeQueryBuilder.BuildQuery(new Title { Type = "show", TitleEn = "Big Brother", Year = 2000 }));
        // аніме-фільм → full movie; аніме-серіал → full episode
        Assert.Equal("Demon Slayer 2019 full movie",
            YoutubeQueryBuilder.BuildQuery(new Title { Type = "anime", TitleEn = "Demon Slayer", Year = 2019, Format = "movie" }));
        Assert.Equal("Naruto 2002 full episode",
            YoutubeQueryBuilder.BuildQuery(new Title { Type = "anime", TitleEn = "Naruto", Year = 2002, Format = "tv" }));
        // uk-назва fallback, коли en порожня
        Assert.Equal("Солодкий 2020 full movie",
            YoutubeQueryBuilder.BuildQuery(new Title { Type = "movie", TitleEn = "", TitleUk = "Солодкий", Year = 2020 }));
    }

    [Fact]
    public void Detect_Languages_And_Multi()
    {
        Assert.Equal("ukr", YoutubeLangDetector.Detect("Film", "Озвучення українською мовою", "Channel"));
        Assert.Equal("russ", YoutubeLangDetector.Detect("Film", "russian dub", "Channel"));
        Assert.Equal("eng", YoutubeLangDetector.Detect("Film", "English audio", "Channel"));
        Assert.Equal("multi", YoutubeLangDetector.Detect("Film", "multi-language audio", "Channel"));
        Assert.Equal("jpn", YoutubeLangDetector.Detect("Film", "japanese original", "Channel"));
        Assert.Equal("kor", YoutubeLangDetector.Detect("Film", "korean drama", "Channel"));
        Assert.Equal("zho", YoutubeLangDetector.Detect("Film", "mandarin", "Channel"));
        Assert.Null(YoutubeLangDetector.Detect("Film", "just a movie", "Channel"));
        Assert.Equal("ukr", YoutubeLangDetector.Detect("Film", "", "Українське Кіно")); // канал
    }
}

/// <summary>YoutubeClient на стаб-хендлері (без реальних HTTP): URL-форми, парсинг, помилки API.</summary>
public class YoutubeClientTests
{
    private const string SearchJson = """
        { "items": [ { "id": { "videoId": "v1" }, "snippet": { "title": "Movie 1995 full movie" } } ] }
        """;

    private const string VideosJson = """
        { "items": [ { "id": "v1", "snippet": { "title": "Movie 1995 full movie" }, "contentDetails": { "duration": "PT2H", "definition": "hd" } } ] }
        """;

    private static (YoutubeClient Client, StubHttpHandler Handler) Make(
        string? search = SearchJson, string? videos = VideosJson, string apiKey = "KEY",
        System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK)
    {
        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            HttpResponseMessage Content(string? body) =>
                body is null
                    ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                    : new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            if (url.Contains("search?")) return Content(search);
            if (url.Contains("videos?")) return Content(videos);
            return null;
        });
        var client = new YoutubeClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") },
            Options.Create(new YoutubeOptions { ApiKey = apiKey }),
            Logs.Null<YoutubeClient>());
        return (client, handler);
    }

    [Fact]
    public async Task SearchFullAsync_Builds_Contract_Query()
    {
        var (client, handler) = Make();
        var items = await client.SearchFullAsync("Se7en 1995 full movie", maxResults: 5);

        var url = Uri.UnescapeDataString(handler.Requests.Single());
        Assert.Contains("part=snippet", url);
        Assert.Contains("type=video", url);
        Assert.Contains("videoDuration=long", url);
        Assert.Contains("maxResults=5", url);
        Assert.Contains("q=Se7en 1995 full movie", url);
        Assert.Contains("key=KEY", url);
        Assert.Single(items);
        Assert.Equal("v1", items[0].VideoId);
    }

    [Fact]
    public async Task DetailsAsync_Joins_Ids()
    {
        var (client, handler) = Make();
        var videos = await client.DetailsAsync(["v1", "v2", "v1"]);

        var url = Uri.UnescapeDataString(handler.Requests.Single());
        Assert.Contains("part=snippet,contentDetails", url);
        Assert.Contains("id=v1,v2", url); // дублікат прибрано, кома-список
        Assert.Single(videos);
        Assert.Equal("PT2H", videos[0].Duration);
    }

    [Fact]
    public async Task Quota_Error_Throws_Typed_Exception()
    {
        var quotaJson = """ { "error": { "code": 403, "message": "quotaExceeded", "errors": [ { "reason": "quotaExceeded" } ] } } """;
        var (client, _) = Make(search: quotaJson, videos: quotaJson, status: System.Net.HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<YoutubeApiException>(() => client.SearchFullAsync("q"));
        Assert.Equal(403, ex.StatusCode);
        Assert.Contains("quotaExceeded", ex.Message);
    }

    [Fact]
    public void IsConfigured_Requires_Key()
    {
        var (client, _) = Make(apiKey: "");
        Assert.False(client.IsConfigured);
        var (withKey, _) = Make();
        Assert.True(withKey.IsConfigured);
    }
}

/// <summary>
/// Enrich-семантика (FR-20): on-demand для одного тайтла, батч-синк, ідемпотентний upsert,
/// кеш search-запитів; постер-фолбеку НЕМА (UX-02). Стаб-хендлер, реальних HTTP-викликів нема.
/// </summary>
public class YouTubeEnrichServiceTests
{
    private const string SearchJson = """
        {
          "items": [
            { "id": { "videoId": "trailer1" }, "snippet": { "title": "Se7en Official Trailer (1995)" } },
            { "id": { "videoId": "movie1" }, "snippet": { "title": "Se7en (1995) Full Movie", "description": "Озвучення українською", "channelTitle": "MovieHub" } }
          ]
        }
        """;

    private const string VideosJson = """
        {
          "items": [
            { "id": "trailer1", "snippet": { "title": "Se7en Official Trailer (1995)" }, "contentDetails": { "duration": "PT2M30S", "definition": "hd" } },
            { "id": "movie1", "snippet": { "title": "Se7en (1995) Full Movie", "description": "Озвучення українською", "channelTitle": "MovieHub" }, "contentDetails": { "duration": "PT2H7M", "definition": "hd" } }
          ]
        }
        """;

    private sealed record Env(
        YouTubeEnrichService Service, MovieDiscoveryDbContext Db, StubHttpHandler Handler, TestDb.CacheStub Cache);

    private static Env MakeEnv(string apiKey = "KEY")
    {
        var db = TestDb.Create("yte_" + Guid.NewGuid().ToString("N"));
        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("search?")) return StubHttp.Json(SearchJson);
            if (url.Contains("videos?")) return StubHttp.Json(VideosJson);
            return null;
        });
        var opts = Options.Create(new YoutubeOptions { ApiKey = apiKey });
        var client = new YoutubeClient(new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") }, opts, Logs.Null<YoutubeClient>());
        var cache = new TestDb.CacheStub();
        var service = new YouTubeEnrichService(client, db, cache, opts, Logs.Null<YouTubeEnrichService>());
        return new Env(service, db, handler, cache);
    }

    private static async Task<Title> SeedAsync(MovieDiscoveryDbContext db, string titleEn = "Se7en", string? poster = null)
    {
        var title = new Title { Type = "movie", TitleEn = titleEn, Year = 1995, ImdbId = "tt0114369", PosterPath = poster };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return title;
    }

    [Fact]
    public async Task EnsureYoutubeLink_Adds_Link_Audio_Quality_Dub_Without_Poster_Fallback()
    {
        var env = MakeEnv();
        var title = await SeedAsync(env.Db); // PosterPath = null → фолбеку НЕМА (UX-02)

        var added = await env.Service.EnsureYoutubeLinkAsync(title.Id);

        Assert.True(added); // трейлер відфільтровано, full movie заматчено
        var link = await env.Db.WatchLinks.SingleAsync(w => w.TitleId == title.Id);
        Assert.Equal("youtube", link.Provider);
        Assert.Equal("free", link.Kind);
        Assert.Equal("*", link.Region);
        Assert.Equal("https://www.youtube.com/watch?v=movie1", link.Url);
        Assert.True(link.IsAvailable);
        Assert.Equal("ukr", link.AudioLang);  // опис українською → розпізнано
        Assert.Equal(1080, link.Quality);     // definition=hd
        Assert.Equal("original", link.Dub);

        // UX-02: YouTube-thumbnail НЕ стає постером — poster_path лишається порожнім
        // (запису з i.ytimg.com у постері немає; справжній постер дає синк wikipedia-posters)
        var stored = await env.Db.Titles.SingleAsync(t => t.Id == title.Id);
        Assert.Null(stored.PosterPath);
        Assert.DoesNotContain("ytimg.com", stored.PosterPath); // подвійна перевірка дефекту

        // 1 search + 1 videos.list на запит
        Assert.Single(env.Handler.Requests, r => r.Contains("search?"));
        Assert.Single(env.Handler.Requests, r => r.Contains("videos?"));
    }

    [Fact]
    public async Task Existing_Poster_Is_Not_Overwritten()
    {
        var env = MakeEnv();
        var title = await SeedAsync(env.Db, poster: "/tmdb_poster.jpg");

        await env.Service.EnsureYoutubeLinkAsync(title.Id);

        Assert.Equal("/tmdb_poster.jpg", (await env.Db.Titles.SingleAsync(t => t.Id == title.Id)).PosterPath);
    }

    [Fact]
    public async Task Existing_Youtube_Link_Skips_Search()
    {
        var env = MakeEnv();
        var title = await SeedAsync(env.Db);
        env.Db.WatchLinks.Add(new WatchLink { TitleId = title.Id, Provider = "youtube", Kind = "free", Region = "*", Url = "https://www.youtube.com/watch?v=old", IsAvailable = true });
        await env.Db.SaveChangesAsync();

        var added = await env.Service.EnsureYoutubeLinkAsync(title.Id);

        Assert.False(added);
        Assert.Empty(env.Handler.Requests); // 0 викликів — лінк уже є
        var link = await env.Db.WatchLinks.SingleAsync(w => w.TitleId == title.Id);
        Assert.Equal("https://www.youtube.com/watch?v=old", link.Url);
    }

    [Fact]
    public async Task No_ApiKey_Is_Dormant_No_Http()
    {
        var env = MakeEnv(apiKey: "");
        var title = await SeedAsync(env.Db);

        Assert.False(env.Service.IsConfigured);
        Assert.False(await env.Service.EnsureYoutubeLinkAsync(title.Id));
        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public async Task Search_Results_Cached_24h_Second_Title_No_Search_Call()
    {
        var env = MakeEnv();
        var t1 = await SeedAsync(env.Db);
        var t2 = await SeedAsync(env.Db, titleEn: "Se7en"); // та сама назва/рік → та сама query

        await env.Service.EnsureYoutubeLinkAsync(t1.Id);
        await env.Service.EnsureYoutubeLinkAsync(t2.Id);

        Assert.Single(env.Handler.Requests, r => r.Contains("search?")); // 1 search на обидва
        Assert.Equal(2, env.Handler.Requests.Count(r => r.Contains("videos?")));
        Assert.Equal(2, await env.Db.WatchLinks.CountAsync(w => w.Provider == "youtube"));
    }

    [Fact]
    public async Task Upsert_Is_Idempotent_Single_Row()
    {
        var env = MakeEnv();
        var title = await SeedAsync(env.Db);

        var first = await env.Service.FindAndUpsertAsync(title);
        var second = await env.Service.FindAndUpsertAsync(title);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);   // той самий рядок
        Assert.Equal(1, await env.Db.WatchLinks.CountAsync(w => w.TitleId == title.Id && w.Provider == "youtube"));
        Assert.Equal("https://www.youtube.com/watch?v=movie1", second.Url);
    }

    [Fact]
    public async Task No_Match_After_Filters_No_Link_No_Poster()
    {
        // лише трейлер (короткий) — DurationOk відсіє, матч не знайдено
        var db = TestDb.Create("yte_" + Guid.NewGuid().ToString("N"));
        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("search?"))
                return StubHttp.Json(""" { "items": [ { "id": { "videoId": "tr1" }, "snippet": { "title": "Se7en Official Trailer" } } ] } """);
            if (url.Contains("videos?"))
                return StubHttp.Json(""" { "items": [ { "id": "tr1", "snippet": { "title": "Se7en Official Trailer" }, "contentDetails": { "duration": "PT2M", "definition": "hd" } } ] } """);
            return null;
        });
        var opts = Options.Create(new YoutubeOptions { ApiKey = "KEY" });
        var client = new YoutubeClient(new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") }, opts, Logs.Null<YoutubeClient>());
        var service = new YouTubeEnrichService(client, db, new TestDb.CacheStub(), opts, Logs.Null<YouTubeEnrichService>());
        var title = new Title { Type = "movie", TitleEn = "Se7en", Year = 1995 };
        db.Titles.Add(title);
        await db.SaveChangesAsync();

        Assert.False(await service.EnsureYoutubeLinkAsync(title.Id));
        Assert.Empty(await db.WatchLinks.ToListAsync());
        Assert.Null((await db.Titles.SingleAsync(t => t.Id == title.Id)).PosterPath);
    }

    [Fact]
    public async Task SyncAsync_Enriches_Top_Titles_Without_Youtube_Links()
    {
        var env = MakeEnv();
        var t1 = await SeedAsync(env.Db, titleEn: "Se7en");
        var t2 = await SeedAsync(env.Db, titleEn: "Se7en");
        var t3 = await SeedAsync(env.Db, titleEn: "Se7en");
        env.Db.Titles.Add(new Title { Type = "movie", TitleEn = "WithLink", Year = 2000 }); // без лінку, але інша назва → інша query
        env.Db.WatchLinks.Add(new WatchLink { TitleId = t3.Id, Provider = "youtube", Kind = "free", Region = "*", Url = "https://yt/old", IsAvailable = true });
        await env.Db.SaveChangesAsync();

        var added = await env.Service.SyncAsync(limit: 200);

        Assert.Equal(2, added);            // t1 і t2 збагачено; t3 уже мав лінк
        Assert.Equal(3, await env.Db.WatchLinks.CountAsync(w => w.Provider == "youtube"));

        // sync_state source=youtube kind=enrich
        var state = await env.Db.SyncStates.SingleAsync(s => s.Source == "youtube");
        Assert.Equal("enrich", state.Kind);
        Assert.Equal(2, state.LastCount);
    }

    [Fact]
    public async Task Official_Trailer_Recorded_Alongside_Watch_Link()
    {
        // фікстура: trailer1 «Se7en Official Trailer (1995)» + movie1 «Se7en (1995) Full Movie»
        var env = MakeEnv();
        var title = await SeedAsync(env.Db);

        await env.Service.EnsureYoutubeLinkAsync(title.Id);

        // трейлер записано попутно (SaveChanges в UpsertLinkAsync покриває обидві мутації)
        var fresh = await env.Db.Titles.SingleAsync(t => t.Id == title.Id);
        Assert.Equal("trailer1", fresh.TrailerYtId);
        Assert.True(await env.Db.WatchLinks.AnyAsync(w => w.TitleId == title.Id && w.Provider == "youtube"));
    }

    [Fact]
    public async Task Trailer_Only_Search_Still_Saves_Trailer_Id()
    {
        // лише офіційний трейлер (короткий) — watch-лінка нема, але trailer_yt_id збережено
        var db = TestDb.Create("yte_" + Guid.NewGuid().ToString("N"));
        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("search?"))
                return StubHttp.Json(""" { "items": [ { "id": { "videoId": "tr1" }, "snippet": { "title": "Se7en Official Trailer (1995)" } } ] } """);
            if (url.Contains("videos?"))
                return StubHttp.Json(""" { "items": [ { "id": "tr1", "snippet": { "title": "Se7en Official Trailer (1995)" }, "contentDetails": { "duration": "PT2M", "definition": "hd" } } ] } """);
            return null;
        });
        var opts = Options.Create(new YoutubeOptions { ApiKey = "KEY" });
        var client = new YoutubeClient(new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") }, opts, Logs.Null<YoutubeClient>());
        var service = new YouTubeEnrichService(client, db, new TestDb.CacheStub(), opts, Logs.Null<YouTubeEnrichService>());
        var title = new Title { Type = "movie", TitleEn = "Se7en", Year = 1995 };
        db.Titles.Add(title);
        await db.SaveChangesAsync();

        Assert.False(await service.EnsureYoutubeLinkAsync(title.Id)); // лінка не додано
        Assert.Empty(await db.WatchLinks.ToListAsync());
        // явний SaveChanges у FindAndUpsertAsync зберіг трейлер попри відсутність лінка
        Assert.Equal("tr1", (await db.Titles.SingleAsync(t => t.Id == title.Id)).TrailerYtId);
    }
}

/// <summary>
/// FR-20+: чиста логіка «записувати чи відео як офіційний трейлер» (YoutubeEnrichLogic).
/// </summary>
public class YoutubeEnrichLogicTests
{
    private static Title Title(string titleEn = "Se7en", int? year = 1995) =>
        new() { Type = "movie", TitleEn = titleEn, Year = year };

    [Fact]
    public void Official_Trailer_Would_Be_Recorded()
    {
        // «official» + «trailer» у назві, назва відео містить назву тайтла
        Assert.True(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en Official Trailer (1995)", "", null));
        // «official» лише в описі — теж офіційний
        Assert.True(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en Trailer", "Official trailer by the studio", null));
        // порожній/пробільний поточний id = «не заповнено»
        Assert.True(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en Official Trailer (1995)", "", "  "));
    }

    [Fact]
    public void Already_Set_Not_Overwritten()
    {
        // наявний (не порожній) trailer_yt_id — жодне відео його не перезаписує
        Assert.False(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en Official Trailer (1995)", "", "existingId"));
        Assert.False(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en Official Teaser (1995)", "Official teaser", "existingId"));
    }

    [Fact]
    public void NonOfficial_Mention_Not_Recorded()
    {
        // «trailer» випадково в описі, без «official»
        Assert.False(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en (1995) Full Movie", "best trailer park scene", null));
        // «official clip» без слова trailer
        Assert.False(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en Official Clip", "the box scene", null));
        // взагалі не трейлер
        Assert.False(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en (1995) Full Movie", "", null));
    }

    [Fact]
    public void Foreign_Video_Or_Wrong_Year_Not_Recorded()
    {
        // офіційний трейлер ІНШОГО фільму — назва відео не містить назву тайтла
        Assert.False(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Fight Club Official Trailer (1999)", "", null));
        // рік відео суперечить року тайтла (сиквел/ремейк)
        Assert.False(YoutubeEnrichLogic.TreatAsTrailer(Title(), "Se7en Official Trailer (2019)", "", null));
    }

    [Fact]
    public void ToCard_Exposes_TrailerYtId()
    {
        var t = new Title { Type = "movie", TitleEn = "Se7en", TrailerYtId = "trailer1" };
        Assert.Equal("trailer1", MovieDiscovery.Api.Contracts.TitleMapper.ToCard(t, "uk").TrailerYtId);
        Assert.Null(MovieDiscovery.Api.Contracts.TitleMapper.ToCard(new Title { Type = "movie", TitleEn = "X" }, "en").TrailerYtId);
    }
}
