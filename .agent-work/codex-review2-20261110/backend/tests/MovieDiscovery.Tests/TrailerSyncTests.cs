using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Api.Controllers;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.YouTube;
using Npgsql;

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
        var t = Title("Interstellar", "Інтерстеллар", "Интерстеллар", 2014);
        Assert.False(TrailerCandidateFilter.MatchesAnyName(t, "Интерстеллар 2 (2035) трейлер")); // інший реліз
        Assert.True(TrailerCandidateFilter.MatchesAnyName(t, "Интерстеллар (2013) трейлер"));     // ±1 ок
    }

    [Fact]
    public void MatchesAnyName_Foreign_Title_Rejects()
    {
        // НІ одна з назв тайтла (en/uk/ru) не входить у назву відео → не той тайтл
        var t = Title("Interstellar", "Інтерстеллар", "Интерстеллар");
        Assert.False(TrailerCandidateFilter.MatchesAnyName(t, "Форма воды трейлер"));
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
        StubHttpHandler Handler, TestDb.CacheStub Cache, string DbName);

    private static Env MakeEnv(
        string? searchJson = TrailerSearchJson, string? videosJson = TrailerVideosJson,
        string apiKey = "KEY", string? oembedPath = null)
    {
        var dbName = "ttr_" + Guid.NewGuid().ToString("N");
        var service = MakeService(dbName, searchJson, videosJson, apiKey, out var db, out var handler, out var verifier, out var cache);
        return new Env(service, verifier, db, handler, cache, dbName);
    }

    /// <summary>Друга сервіс-обгортка над ТОЮ САМОЮ InMemory-БД — імітація паралельного запиту (окремий scope).</summary>
    private static TitleTrailerSyncService MakeSiblingService(Env env, string? searchJson = TrailerSearchJson, string? videosJson = TrailerVideosJson, string apiKey = "KEY")
    {
        var service = MakeService(env.DbName, searchJson, videosJson, apiKey, out _, out _, out _, out _);
        return service;
    }

    private static TitleTrailerSyncService MakeService(
        string dbName, string? searchJson, string? videosJson, string apiKey,
        out MovieDiscoveryDbContext db, out StubHttpHandler handler,
        out TrailerVerifier verifier, out TestDb.CacheStub cache)
    {
        db = TestDb.Create(dbName);
        handler = new StubHttpHandler(req =>
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
        verifier = new TrailerVerifier(oembedClient, client, Logs.Null<TrailerVerifier>());
        cache = new TestDb.CacheStub();
        return new TitleTrailerSyncService(client, verifier, db, cache, opts, Logs.Null<TitleTrailerSyncService>());
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
        // t2 — інший тайтл з ТОЮ САМОЮ назвою/роком: спільний search-кеш, той самий набір кандидатів
        var t2 = await SeedAsync(env.Db, titleEn: "Interstellar", titleUk: "Інтерстеллар", titleRu: "Интерстеллар", year: 2014);

        // Перший прогін: обидва тайтли просинкані (один search — друга назва б'є в кеш першого)
        await env.Service.RunAsync(limit: 10);
        Assert.Single(env.Handler.Requests, r => r.Contains("search?"));
        Assert.Equal(3, await env.Db.TitleTrailers.CountAsync(x => x.TitleId == t1.Id));
        Assert.Equal(3, await env.Db.TitleTrailers.CountAsync(x => x.TitleId == t2.Id));
        var state1 = await env.Db.SyncStates.SingleAsync(s => s.Source == "youtube-trailers");
        Assert.Equal("sync", state1.Kind);
        Assert.Equal(6, state1.LastCount); // 3+3 ok-кандидатів за перший прогін

        // Повторний прогін: у ОБОХ уже є рядки → skip без жодного search (квота не витрачається)
        env.Handler.Requests.Clear();
        await env.Service.RunAsync(limit: 10);

        Assert.Empty(env.Handler.Requests);
        var state2 = await env.Db.SyncStates.SingleAsync(s => s.Source == "youtube-trailers");
        Assert.Equal(0, state2.LastCount); // другий прогін нічого не додав (і перезаписав лічильник)
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

    // ── Рев'ю Codex: single-flight/ідемпотентність під гонкою + черга ──

    [Fact]
    public async Task Concurrent_SyncOne_Does_Not_Duplicate_Unique_Guarded()
    {
        // Гонка ДВОХ незалежних синків (окремі DbContext — як окремі HTTP-запити) на той
        // самий тайтл: UNIQUE(title_id, yt_id) + catch unique-violation → дублів нема,
        // жодного краху; сумарно кожен кандидат записано один раз.
        var env = MakeEnv();
        var sibling = MakeSiblingService(env);
        var title = await SeedAsync(env.Db);

        var results = await Task.WhenAll(
            env.Service.SyncOneAsync(title),
            sibling.SyncOneAsync(title));

        Assert.Equal(3, await env.Db.TitleTrailers.CountAsync(x => x.TitleId == title.Id));
        var ids = await env.Db.TitleTrailers.Where(x => x.TitleId == title.Id).Select(x => x.YtId).ToListAsync();
        Assert.Equal(3, ids.Distinct().Count()); // жодного дубля (title_id, yt_id)
        Assert.All(results, r => Assert.True(r >= 0)); // синк не впав (гардовано)
    }

    [Fact]
    public void UniqueViolation_Detected_For_Npgsql_And_Message_Forms()
    {
        // message-форма (InMemory / обгорнуті провайдери)
        var msg = new DbUpdateException("boom", new Exception("duplicate key value violates unique constraint"));
        Assert.True(TitleTrailerSyncService.IsUniqueViolation(msg));

        // InMemory-форма: InvalidOperationException з "uniqueness" у повідомленні
        Assert.True(TitleTrailerSyncService.IsUniqueViolation(
            new InvalidOperationException("Violations of 'Index:...' uniqueness are not supported.")));

        var other = new DbUpdateException("boom", new Exception("timeout expired"));
        Assert.False(TitleTrailerSyncService.IsUniqueViolation(other));
        Assert.False(TitleTrailerSyncService.IsUniqueViolation(new Exception("connection refused")));
    }

    [Fact]
    public void SyncQueue_Bounded_DropOldest()
    {
        // Черга (BoundedChannel 10, DropOldest): TryEnqueue при заповненні НЕ відмовляє
        var queue = new TitleTrailerSyncQueue();
        for (var i = 0; i < 12; i++) Assert.True(queue.TryEnqueue(new TitleTrailerSyncJob(20)));
        Assert.Equal(10, queue.Count);
    }

    // ── IV-03: джоба реально ВИКОНУЄТЬСЯ фоновим сервісом (не лише 202 у контролері) ──

    [Fact]
    public async Task BackgroundService_Executes_Queued_Job_And_Writes_Rows()
    {
        var env = MakeEnv();
        var title = await SeedAsync(env.Db);

        // Композиція як у Program.cs: queue singleton + TitleTrailerSyncService у DI-scope
        var queue = new TitleTrailerSyncQueue();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(queue);
        services.AddScoped<TitleTrailerSyncService>(_ => env.Service);
        using var provider = services.BuildServiceProvider();

        var bg = new TitleTrailerSyncBackgroundService(
            provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Logs.Null<TitleTrailerSyncBackgroundService>());

        using var cts = new System.Threading.CancellationTokenSource();
        var runTask = bg.StartAsync(cts.Token); // запускає ExecuteAsync

        queue.TryEnqueue(new TitleTrailerSyncJob(1)); // та сама джоба, яку ставить контролер

        // чекаємо на обробку (stabs — мілісекунди)
        for (var i = 0; i < 50 && await env.Db.TitleTrailers.CountAsync(x => x.TitleId == title.Id) == 0; i++)
            await Task.Delay(50);

        cts.Cancel();
        await runTask; // loop коректно завершився після cancel

        var rows = await env.Db.TitleTrailers.Where(x => x.TitleId == title.Id).ToListAsync();
        Assert.Equal(3, rows.Count); // джоба РЕАЛЬНО виконала синк (search → верифікація → rows)
        var state = await env.Db.SyncStates.SingleAsync(s => s.Source == "youtube-trailers");
        Assert.Equal(3, state.LastCount);
    }

    // ── IV-06: контракт із SearchService.HasTrailer (координація tr-2 ↔ watch-2) ──

    [Fact]
    public async Task HasTrailer_Predicate_Sees_Verified_Rows_From_Sync()
    {
        // sync записав uk-трейлер (verified_status='ok') у тайтл БЕЗ titles.trailer_yt_id
        // → hasTrailer=true (предикат: EXISTS title_trailers ok OR trailer_yt_id)
        var env = MakeEnv();
        var title = await SeedAsync(env.Db);
        Assert.Null(title.TrailerYtId);

        await env.Service.SyncOneAsync(title);

        var okRows = await env.Db.TitleTrailers
            .CountAsync(x => x.TitleId == title.Id && x.VerifiedStatus == "ok");
        Assert.Equal(3, okRows); // sync пише 'ok' лише для живих embeddable-трейлерів

        var hits = await new MovieDiscovery.Infrastructure.Services.SearchService(env.Db)
            .SearchAsync(new MovieDiscovery.Infrastructure.Services.SearchQuery(
                null, null, [], null, null, null, null, null, null, null, null, "popularity", 1, 20,
                HasTrailer: true));
        Assert.Contains(hits.Items, t => t.Id == title.Id);
    }

    // ── IV-05: single-flight cold-cache concurrent GET (стаби, без реальної квоти) ──

    [Fact]
    public async Task Concurrent_Cold_Cache_Get_Runs_OnDemand_Search_Once()
    {
        // Два паралельні GET на ХОЛОДНИЙ кеш того самого тайтла (ColdCacheStub: Get → null).
        // Квота-дисципліна: search-викликів ≤ 1 на тайтл (single-flight gate контролера),
        // рядків рівно 3 (ідемпотентно), both GETs повертають повний набір.
        var env = MakeEnv();
        var title = await SeedAsync(env.Db);

        var coldCache = new ColdCacheStub();
        var svc = new TitleTrailerSyncService(
            new YoutubeClient(new HttpClient(env.Handler) { BaseAddress = new Uri("https://test.local/") },
                Options.Create(new YoutubeOptions { ApiKey = "KEY" }), Logs.Null<YoutubeClient>()),
            env.Verifier, env.Db, coldCache, Options.Create(new YoutubeOptions { ApiKey = "KEY" }),
            Logs.Null<TitleTrailerSyncService>());

        // Gate-семантика TrailersController.Get: другий вхід чекає, перечитує РЯДКИ
        // (плюс відповідь-кеш у реальному контролері), не шукає знову. Без gate обидва
        // побачили б 0 рядків і шукали б двічі (2×100 units).
        var gate = new System.Threading.SemaphoreSlim(1, 1);
        await Task.WhenAll(
            GateGet(gate, () => EmulateControllerGetAsync(svc, env, title.Id)),
            GateGet(gate, () => EmulateControllerGetAsync(svc, env, title.Id)));

        var searches = env.Handler.Requests.Count(r => r.Contains("search?"));
        Assert.True(searches <= 1, $"single-flight: очікувалось ≤1 search, було {searches}");
        Assert.Equal(3, await env.Db.TitleTrailers.CountAsync(x => x.TitleId == title.Id));
        Assert.Equal(3, TrailersController.ToInfos(
            await TrailersController.LoadRowsAsync(env.Db, title.Id, default)).Count);
    }

    /// <summary>Контролерський flow: cache-miss → рядки → on-demand search лише коли порожньо.</summary>
    private static async Task EmulateControllerGetAsync(TitleTrailerSyncService svc, Env env, int id)
    {
        var rows = await TrailersController.LoadRowsAsync(env.Db, id, default);
        if (rows.Count == 0)
            await svc.SyncTitleAsync(id);
    }

    /// <summary>Gate-семантика контролера: другий вхід чекає, перевіряє кеш/рядки, search не повторює.</summary>
    private static async Task GateGet(SemaphoreSlim gate, Func<Task> work)
    {
        await gate.WaitAsync();
        try { await work(); }
        finally { gate.Release(); }
    }

    /// <summary>Завжди-холодний кеш (IV-05): Get → null, Set → ігнорується.</summary>
    private sealed class ColdCacheStub : MovieDiscovery.Core.Abstractions.ICacheStore
    {
        public Task<string?> GetAsync(string source, string cacheKey, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task SetAsync(string source, string cacheKey, string payload, int ttlSeconds, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

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
