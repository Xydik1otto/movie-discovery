using MovieDiscovery.Api.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Tests;

/// <summary>
/// UX-05: трейлер-синк і верифікація. натуральна фраза запиту, мовні варіанти «трейлер»,
/// guard шуму, матчинг назв uk/ru/en, ланцюжок мови, канальний allowlist, зведений
/// verified_status, ідемпотентний upsert УСІХ кандидатів, фільтр показу в ToInfos.
/// Реальних HTTP-викликів нема — стаб-хендлери.
/// </summary>
public class TrailerSyncTests
{
    // ── Запит: натуральна фраза «{назва uk|en} трейлер українською» (доведено ефективнішою за «full movie») ──

    [Fact]
    public void BuildQuery_Uk_Name_Preferred_En_Fallback()
    {
        Assert.Equal("Інтерстеллар трейлер українською",
            TitleTrailerSyncService.BuildQuery(new Title { Type = "movie", TitleEn = "Interstellar", TitleUk = "Інтерстеллар", Year = 2014 }));
        Assert.Equal("Mission to Mars трейлер українською",
            TitleTrailerSyncService.BuildQuery(new Title { Type = "movie", TitleEn = "Mission to Mars", Year = 2000 }));
        Assert.Equal("", TitleTrailerSyncService.BuildQuery(new Title { Type = "movie", TitleEn = "" }));
    }

    // ── Фільтр кандидатів: трейлер-слово в uk/ru/en + guard шуму ──

    [Theory]
    [InlineData("Mission to Mars (2000) Trailer #1", "", true)]
    [InlineData("Інтерстеллар (2014) - трейлер українською", "", true)]
    [InlineData("Интерстеллар — русский трейлер", "", true)]
    [InlineData("Interstellar Teaser", "official teaser", true)]
    [InlineData("Interstellar (2014) Full Movie", "watch online", false)]
    [InlineData("Інтерстеллар — реакція на трейлер", "", false)] // reaction у назві
    [InlineData("Interstellar — ending explained", "trailer discussion", false)]
    public void IsTrailerCandidate_Multilang_Words_And_Noise(string title, string description, bool expected) =>
        Assert.Equal(expected, TrailerCandidateFilter.IsTrailerCandidate(title, description));

    [Fact]
    public void DurationOk_Trailer_Under_Five_Minutes()
    {
        Assert.True(TrailerCandidateFilter.DurationOk(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30)));
        Assert.True(TrailerCandidateFilter.DurationOk(TimeSpan.FromMinutes(5)));
        Assert.False(TrailerCandidateFilter.DurationOk(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)));
        Assert.False(TrailerCandidateFilter.DurationOk(null));
        Assert.False(TrailerCandidateFilter.DurationOk(TimeSpan.Zero));
    }

    // ── Матчинг: назва uk/ru/en + рік ±1 ──

    private static Title Title(string titleEn, string? titleUk = null, string? titleRu = null, int? year = 2014) =>
        new() { Type = "movie", TitleEn = titleEn, TitleUk = titleUk, TitleRu = titleRu, Year = year };

    [Fact]
    public void MatchesAnyName_Uk_Ru_En_Titles()
    {
        var t = Title("Interstellar", "Інтерстеллар", "Интерстеллар");
        Assert.True(TrailerCandidateFilter.MatchesAnyName(t, "Інтерстеллар (2014) - трейлер українською"));
        Assert.True(TrailerCandidateFilter.MatchesAnyName(t, "Интерстеллар — Русский трейлер"));
        Assert.True(TrailerCandidateFilter.MatchesAnyName(t, "Interstellar (2014) Official Trailer"));
    }

    [Fact]
    public void MatchesAnyName_Wrong_Year_Rejects()
    {
        var t = Title("Interstellar", "Інтерстеллар", year: 2014);
        Assert.False(TrailerCandidateFilter.MatchesAnyName(t, "Интерстеллар 2 (2035) трейлер")); // інший реліз
        Assert.True(TrailerCandidateFilter.MatchesAnyName(t, "Интерстеллар (2013) трейлер"));     // ±1 ок
    }

    [Fact]
    public void MatchesAnyName_Foreign_Title_Rejects()
    {
        Assert.False(TrailerCandidateFilter.MatchesAnyName(Title("Interstellar"), "Форма воды трейлер"));
    }

    // ── Мова: defaultAudioLanguage → defaultLanguage → title-регекс → NULL (чесний unknown) ──

    [Theory]
    [InlineData("uk", "uk")]
    [InlineData("uk-UA", "uk")]
    [InlineData("ru-RU", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("zxx", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FromYouTubeLang_Maps_Base_Codes(string? input, string? expected) =>
        Assert.Equal(expected, TrailerLang.FromYouTubeLang(input));

    [Fact]
    public void Detect_Chain_Audio_Lang_Wins_Then_Title_Regex()
    {
        // 1) defaultAudioLanguage
        Assert.Equal("uk", TrailerLang.Detect("uk-UA", "ru", "трейлер", "", ""));
        // 2) defaultLanguage коли аудіо не вказано
        Assert.Equal("ru", TrailerLang.Detect(null, "ru", "трейлер", "", ""));
        // 3) title-регекс (українською / русский / german)
        Assert.Equal("uk", TrailerLang.Detect(null, null, "Інтерстеллар трейлер українською", "", ""));
        Assert.Equal("ru", TrailerLang.Detect(null, null, "Интерстеллар русский трейлер", "", ""));
        Assert.Equal("de", TrailerLang.Detect(null, null, "Interstellar Trailer German", "", ""));
        Assert.Equal("fr", TrailerLang.Detect(null, null, "Interstellar Bande annonce française", "french trailer", ""));
        Assert.Equal("es", TrailerLang.Detect(null, null, "Interstellar Tráiler español", "", ""));
        // 4) нічого не відомо → NULL (чесний unknown, не вигадуємо en)
        Assert.Null(TrailerLang.Detect(null, null, "Interstellar Official Trailer", "", "Paramount Pictures"));
    }

    // ── Офіційність: канальний allowlist + офіційна фраза ──

    [Theory]
    [InlineData("Rotten Tomatoes Classic Trailers", true)]
    [InlineData("WBRussia", true)]
    [InlineData("Warner Bros. Pictures", true)]
    [InlineData("UA-Cinema", true)]
    [InlineData("new kino ua", true)]
    [InlineData("Paramount Movies", true)]
    [InlineData("Movieclips Classic Trailers", true)]
    [InlineData("Рандомний канал про кіно", false)]
    [InlineData("trashtrailers", false)]
    [InlineData(null, false)]
    public void Allowlist_Detects_Official_Channels(string? channel, bool expected) =>
        Assert.Equal(expected, TrailerChannelAllowlist.IsOfficialChannel(channel));

    // ── Зведений verified_status ──

    private static TrailerVerification Ver(
        bool exists = true, bool? embeddable = true, string? privacy = "public", string? oembed = "ok") =>
        new("abc123", exists, "PT2M30S", embeddable, privacy, "processed", null, null, "Trailer", "", "Ch", oembed);

    [Fact]
    public void VerifiedStatus_Decision_Tree()
    {
        Assert.Equal("ok", Ver().VerifiedStatus);
        Assert.Equal("gone", Ver(exists: false).VerifiedStatus);                     // видалено (нема у videos.list)
        Assert.Equal("gone", Ver(oembed: "gone").VerifiedStatus);                    // oEmbed 404
        Assert.Equal("private", Ver(privacy: "private").VerifiedStatus);             // приватне
        Assert.Equal("nonembeddable", Ver(embeddable: false).VerifiedStatus);        // embed заборонено
        Assert.Equal("nonembeddable", Ver(oembed: "nonembeddable").VerifiedStatus);  // oEmbed 401
        Assert.Null(Ver(oembed: null).VerifiedStatus);                               // мережева помилка — чесний NULL
    }

    // ── Синк: search → фільтри → верифікація → upsert УСІХ кандидатів ──

    private const string TrailerSearchJson = """
        {
          "items": [
            { "id": { "videoId": "ukvid" }, "snippet": { "title": "Інтерстеллар (2014) - трейлер українською", "channelTitle": "UA-Cinema", "description": "офіційний трейлер" } },
            { "id": { "videoId": "ruvid" }, "snippet": { "title": "Интерстеллар — русский трейлер", "channelTitle": "WBRussia", "description": "дублированный трейлер" } },
            { "id": { "videoId": "envid" }, "snippet": { "title": "Interstellar (2014) Official Trailer", "channelTitle": "Paramount Movies", "description": "official trailer" } },
            { "id": { "videoId": "noisevid" }, "snippet": { "title": "Интерстеллар смотреть онлайн полный фильм", "channelTitle": "Random", "description": "full movie" } },
            { "id": { "videoId": "wrongvid" }, "snippet": { "title": "Форма воды трейлер", "channelTitle": "Random", "description": "" } }
          ]
        }
        """;

    private const string TrailerVideosJson = """
        {
          "items": [
            { "id": "ukvid", "snippet": { "title": "Інтерстеллар (2014) - трейлер українською", "channelTitle": "UA-Cinema", "description": "офіційний трейлер", "defaultAudioLanguage": "uk" },
              "contentDetails": { "duration": "PT2M22S", "definition": "hd" }, "status": { "embeddable": true, "privacyStatus": "public", "uploadStatus": "processed" } },
            { "id": "ruvid", "snippet": { "title": "Интерстеллар — русский трейлер", "channelTitle": "WBRussia", "description": "дублированный трейлер", "defaultAudioLanguage": "ru" },
              "contentDetails": { "duration": "PT2M5S", "definition": "hd" }, "status": { "embeddable": true, "privacyStatus": "public", "uploadStatus": "processed" } },
            { "id": "envid", "snippet": { "title": "Interstellar (2014) Official Trailer", "channelTitle": "Paramount Movies", "description": "official trailer", "defaultAudioLanguage": "en" },
              "contentDetails": { "duration": "PT2M50S", "definition": "hd" }, "status": { "embeddable": true, "privacyStatus": "public", "uploadStatus": "processed" } }
          ]
        }
        """;

    private sealed record Env(
        TitleTrailerSyncService Service, TrailerVerifier Verifier, MovieDiscoveryDbContext Db,
        StubHttpHandler Handler, TestDb.CacheStub Cache);

    private static Env MakeEnv(
        string? searchJson = TrailerSearchJson, string? videosJson = TrailerVideosJson,
        string apiKey = "KEY", string? oembedPath = null)
    {
        var db = TestDb.Create("ttr_" + Guid.NewGuid().ToString("N"));
        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("search?")) return searchJson is null ? null : StubHttp.Json(searchJson);
            if (url.Contains("videos?")) return videosJson is null ? null : StubHttp.Json(videosJson);
            if (url.Contains("oembed?")) return StubHttp.Json(""" { "title": "trailer" } """);
            return null;
        });
        var opts = Options.Create(new YoutubeOptions { ApiKey = apiKey });
        var client = new YoutubeClient(new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") }, opts, Logs.Null<YoutubeClient>());
        var oembedClient = new HttpClient(new OembedStub()) { BaseAddress = new Uri("https://test.local/") };
        var verifier = new TrailerVerifier(oembedClient, client, Logs.Null<TrailerVerifier>());
        var cache = new TestDb.CacheStub();
        var service = new TitleTrailerSyncService(client, verifier, db, cache, opts, Logs.Null<TitleTrailerSyncService>());
        return new Env(service, verifier, db, handler, cache);
    }

    /// <summary>oEmbed-стаб: 200 на все (для статус-тестів вистачає decision-tree вище).</summary>
    private sealed class OembedStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(""" { "title": "trailer" } """, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static async Task<Title> SeedAsync(MovieDiscoveryDbContext db, string titleEn = "Interstellar", string? titleUk = "Інтерстеллар", string? titleRu = "Интерстеллар", int? year = 2014)
    {
        var title = new Title { Type = "movie", TitleEn = titleEn, TitleUk = titleUk, TitleRu = titleRu, Year = year, Popularity = 100 };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return title;
    }

    [Fact]
    public async Task SyncOne_Saves_All_Language_Candidates_Verified()
    {
        var env = MakeEnv();
        var title = await SeedAsync(env.Db);

        var added = await env.Service.SyncOneAsync(title);

        Assert.Equal(3, added); // uk + ru + en; noise (full movie) і чужий тайтл відфільтровано
        var rows = await env.Db.TitleTrailers.Where(x => x.TitleId == title.Id).ToListAsync();

        var uk = rows.Single(r => r.YtId == "ukvid");
        Assert.Equal("uk", uk.Lang);
        Assert.True(uk.Official);                 // allowlist UA-Cinema + «офіційний»
        Assert.Equal("youtube", uk.Source);
        Assert.Equal("ok", uk.VerifiedStatus);    // embeddable + public + oEmbed 200
        Assert.NotNull(uk.VerifiedAt);

        var ru = rows.Single(r => r.YtId == "ruvid");
        Assert.Equal("ru", ru.Lang);
        Assert.True(ru.Official);                 // WBRussia — офіційний ru-канал дистриб'ютора

        var en = rows.Single(r => r.YtId == "envid");
        Assert.Equal("en", en.Lang);
        Assert.True(en.Official);                 // Paramount Movies

        Assert.DoesNotContain(rows, r => r.YtId == "noisevid"); // full movie — не трейлер
        Assert.DoesNotContain(rows, r => r.YtId == "wrongvid"); // чужий тайтл

        // 1 search + 1 videos.list на тайтл; oEmbed — без квоти
        Assert.Single(env.Handler.Requests, r => r.Contains("search?"));
        Assert.Single(env.Handler.Requests, r => r.Contains("videos?"));
    }

    [Fact]
    public async Task SyncOne_Idempotent_Second_Run_Does_Not_Duplicate()
    {
        var env = MakeEnv();
        var title = await SeedAsync(env.Db);

        await env.Service.SyncOneAsync(title);
        var addedSecond = await env.Service.SyncOneAsync(title);

        Assert.Equal(0, addedSecond);
        Assert.Equal(3, await env.Db.TitleTrailers.CountAsync(x => x.TitleId == title.Id));
    }

    [Fact]
    public async Task SyncOne_Caches_Search_Query()
    {
        var env = MakeEnv();
        var t1 = await SeedAsync(env.Db);
        var t2 = await SeedAsync(env.Db); // та сама назва → та сама query

        await env.Service.SyncOneAsync(t1);
        await env.Service.SyncOneAsync(t2);

        Assert.Single(env.Handler.Requests, r => r.Contains("search?")); // 1 search на обидва (кеш 24 год)
    }

    [Fact]
    public async Task SyncOne_Gone_Candidate_Saved_But_Not_Counted_As_Playable()
    {
        // videos.list віддає лише ukvid → envid «зник» під час верифікації (gone)
        var env = MakeEnv(videosJson: """
            {
              "items": [
                { "id": "ukvid", "snippet": { "title": "Інтерстеллар (2014) - трейлер українською", "channelTitle": "UA-Cinema", "defaultAudioLanguage": "uk" },
                  "contentDetails": { "duration": "PT2M22S", "definition": "hd" }, "status": { "embeddable": true, "privacyStatus": "public" } },
                { "id": "ruvid", "snippet": { "title": "Интерстеллар — русский трейлер", "channelTitle": "WBRussia", "defaultAudioLanguage": "ru" },
                  "contentDetails": { "duration": "PT2M5S", "definition": "hd" }, "status": { "embeddable": false, "privacyStatus": "public" } }
              ]
            }
            """);
        var title = await SeedAsync(env.Db);

        var added = await env.Service.SyncOneAsync(title);

        var rows = await env.Db.TitleTrailers.Where(x => x.TitleId == title.Id).ToListAsync();
        Assert.Equal("gone", rows.Single(r => r.YtId == "envid").VerifiedStatus);  // немає у videos.list
        Assert.Equal("nonembeddable", rows.Single(r => r.YtId == "ruvid").VerifiedStatus);
        Assert.Equal(1, added); // тільки ukvid — програваний
    }

    [Fact]
    public async Task RunAsync_Skips_Titles_With_Rows_And_Writes_SyncState()
    {
        var env = MakeEnv();
        var t1 = await SeedAsync(env.Db);
        var t2 = await SeedAsync(env.Db, titleEn: "Se7en", titleUk: null, titleRu: "Семь", year: 1995);

        await env.Service.RunAsync(limit: 10);
        env.Db.TitleTrailers.Add(new TitleTrailer { TitleId = t1.Id, YtId = "preexisting", Source = "titles" });
        await env.Db.SaveChangesAsync();

        // повторний прогін: t1 уже має рядки → не шукається; t2 не має → шукається знову
        env.Handler.Requests.Clear();
        var added = await env.Service.RunAsync(limit: 10);

        Assert.Single(env.Handler.Requests, r => r.Contains("search?")); // лише t2
        var state = await env.Db.SyncStates.SingleAsync(s => s.Source == "youtube-trailers");
        Assert.Equal("sync", state.Kind);
    }

    // ── Фільтр показу (TrailersController.ToInfos): не-ok синк-рядки не віддаються ──

    [Fact]
    public void ToInfos_Filters_NonOk_Keeps_FastPath_Stale()
    {
        var rows = new List<TitleTrailer>
        {
            new() { TitleId = 1, YtId = "okvid", Lang = "uk", Official = true, Source = "youtube", VerifiedStatus = "ok" },
            new() { TitleId = 1, YtId = "gonevid", Lang = "ru", Source = "youtube", VerifiedStatus = "gone" },
            new() { TitleId = 1, YtId = "unverified", Source = "youtube", VerifiedStatus = null },
            new() { TitleId = 1, YtId = "deadfast", Source = "titles", VerifiedStatus = "gone" },
        };

        var infos = TrailersController.ToInfos(rows);

        // gone синк-рядок ВИЛУЧЕНО; fast-path gone залишено свідомо (UX-05 f: повідомлення + зовнішнє посилання)
        Assert.Equal(3, infos.Count);
        Assert.DoesNotContain(infos, i => i.YtId == "gonevid");
        Assert.Contains(infos, i => i.YtId == "deadfast" && i.VerifiedStatus == "gone");

        // порядок: official спочатку, uk → ru → …
        Assert.Equal("okvid", infos[0].YtId);
    }

    [Fact]
    public void ToOrders_Uk_First_Among_NonOfficial()
    {
        var rows = new List<TitleTrailer>
        {
            new() { TitleId = 1, YtId = "b", Lang = "en", Official = false, Source = "youtube", VerifiedStatus = "ok" },
            new() { TitleId = 1, YtId = "a", Lang = "uk", Official = false, Source = "youtube", VerifiedStatus = "ok" },
            new() { TitleId = 1, YtId = "c", Lang = null, Official = false, Source = "youtube", VerifiedStatus = "ok" },
        };

        var infos = TrailersController.ToInfos(rows);

        Assert.Equal(["a", "b", "c"], infos.Select(i => i.YtId).ToArray());
    }

    // ── Контракт: TitleDetailResponse несе trailers[] ──

    [Fact]
    public void TitleDetail_Carries_Trailers_Contract()
    {
        var detail = new TitleDetailResponse { Trailers = [new TrailerInfo("abc123", "uk", true, "youtube", null, "ok")] };
        var trailer = Assert.Single(detail.Trailers);
        Assert.Equal("abc123", trailer.YtId);
        Assert.Equal("uk", trailer.Lang);
        Assert.True(trailer.Official);
        Assert.Equal("youtube", trailer.Source);
        Assert.Equal("ok", trailer.VerifiedStatus);
    }
}
