using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Embeddings; // IHttpClientFactoryAccessor
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Tmdb;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>Fake ITmdbImagesService: виклики записує, відповіді керує тестом (ById/Resolver/Delay).</summary>
public sealed class FakeTmdbImagesService : ITmdbImagesService
{
    public bool IsConfigured { get; set; } = true;
    public List<(int TmdbId, string Type)> Calls { get; } = [];
    public Dictionary<int, TmdbResolvedImages> ById { get; } = [];
    public Func<int, string, TmdbResolvedImages?>? Resolver { get; set; }
    public TimeSpan? Delay { get; set; }
    public Dictionary<string, TmdbImdbResolved> ByImdb { get; } = [];
    public int ImdbCalls { get; private set; }

    public Task<TmdbImdbResolved?> ResolveByImdbAsync(string imdbId, string type, CancellationToken ct = default)
    {
        ImdbCalls++;
        return Task.FromResult(ByImdb.TryGetValue(imdbId, out var hit) ? hit : null);
    }

    public async Task<TmdbResolvedImages?> ResolveAsync(int tmdbId, string type, CancellationToken ct = default)
    {
        Calls.Add((tmdbId, type));
        if (Delay is { } d) await Task.Delay(d, ct);
        if (Resolver is not null) return Resolver(tmdbId, type);
        return ById.TryGetValue(tmdbId, out var r) ? r : null;
    }
}

/// <summary>IHttpClientFactoryAccessor-стаб: завжди віддає один підготовлений HttpClient.</summary>
public sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactoryAccessor
{
    public HttpClient CreateClient(string name) => client;
}

/// <summary>
/// Тести poster-source-sync (автоматичне доціплення постерів):
/// (a) масовий прогін заповнює poster_path/backdrop_path з резолвера (fill-only);
/// (b) порядок popularity DESC (найпопулярніші перші) + keyset-батчі + MaxRows;
/// (c) on-demand у GET /api/title/{id}: резолвнутий URL у відповіді + запис у БД + точкова джоба в черзі;
/// (d) 429-ретрай TmdbImagesService поважає Retry-After (з капом);
/// + покриття аніме/манги (Shikimori/Jikan, circuit-breaker), тротлінг 300мс, кеш деталей.
/// Реальних HTTP-викликів нема — StubHttpHandler.
/// </summary>
public class PosterSourceSyncTests
{
    private static readonly CancellationToken TestCt = CancellationToken.None;

    private static (MovieDiscoveryDbContext Db, PosterSourceSyncService Service) NewSync(
        FakeTmdbImagesService? fake = null, StubHttpHandler? handler = null)
    {
        var db = TestDb.Create($"poster-sync-{Guid.NewGuid():N}");
        var http = handler is not null
            ? new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") }
            : StubHttp.Client(_ => null);
        var coverage = new PosterCoverageService(
            new StubHttpClientFactory(http),
            Options.Create(new ShikimoriOptions()),
            Options.Create(new JikanOptions()),
            new ShikimoriThrottle(),
            new JikanThrottle(),
            Logs.Null<PosterCoverageService>());
        var svc = new PosterSourceSyncService(fake ?? new FakeTmdbImagesService(), coverage, db, Logs.Null<PosterSourceSyncService>());
        return (db, svc);
    }

    private static TmdbImagesService NewTmdbImages(
        StubHttpHandler handler, int throttleMs = 10, TimeSpan? retryAfterCap = null, int maxAttempts = 3)
    {
        var options = Options.Create(new TmdbOptions { ApiKey = "test-key" });
        return new TmdbImagesService(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") },
            new TestDb.CacheStub(), options, Logs.Null<TmdbImagesService>(),
            TimeSpan.FromMilliseconds(throttleMs),
            retryAfterCap ?? TimeSpan.FromSeconds(3),
            maxAttempts);
    }

    private static HttpResponseMessage Resp429(TimeSpan retryAfter)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited"),
        };
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return resp;
    }

    private static HttpResponseMessage Resp429WithoutHeader()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited"),
        };
        return resp;
    }

    // ── (a) Масовий прогін: fill-only оновлення з fake-резолвера ──

    [Fact]
    public async Task Sweep_Fills_Poster_And_Backdrop_Only_When_Empty()
    {
        var fake = new FakeTmdbImagesService
        {
            Resolver = (id, _) => id == 111 ? new TmdbResolvedImages("/p111.jpg", "/b111.jpg") : null,
        };
        var (db, svc) = NewSync(fake);
        db.Titles.AddRange(
            new Title { Id = 10, Type = "movie", TmdbId = 111, TitleEn = "A", Popularity = 9 },
            new Title { Id = 11, Type = "movie", TmdbId = 222, TitleEn = "B", Popularity = 8 }, // TMDB без картинок
            new Title { Id = 12, Type = "movie", TmdbId = 333, TitleEn = "C", Popularity = 7, PosterPath = "/existing.jpg" }, // не у вибірці
            new Title { Id = 13, Type = "anime", TitleEn = "D", Popularity = 6 }); // аніме без зовн. id → skip
        await db.SaveChangesAsync(TestCt);

        var result = await svc.RunAsync(null, null, TestCt);

        // t10/t11/t13 у вибірці (t12 має постер); оновлено лише t10
        Assert.Equal(3, result.Processed);
        Assert.Equal(1, result.Updated);

        var rows = db.Titles.AsNoTracking().OrderBy(t => t.Id).ToList();
        Assert.Equal("/p111.jpg", rows[0].PosterPath);
        Assert.Equal("/b111.jpg", rows[0].BackdropPath);
        Assert.Null(rows[1].PosterPath);          // резолвер дав null → лишається порожнім
        Assert.Equal("/existing.jpg", rows[2].PosterPath); // наявний НЕ перебито
        Assert.Null(rows[3].PosterPath);          // аніме без shikimori_id/mal_id → нічого

        // резолв викликався лише для тайтлів з tmdb_id (і не для вже заповненого t12)
        Assert.Equal([(111, "movie"), (222, "movie")], fake.Calls);
    }

    // ── (b) Порядок popularity DESC + MaxRows + keyset-батчі ──

    [Fact]
    public async Task Sweep_Orders_By_Popularity_Desc_Most_Popular_First()
    {
        var fake = new FakeTmdbImagesService
        {
            Resolver = (id, _) => new TmdbResolvedImages($"/p{id}.jpg", null),
        };
        var (db, svc) = NewSync(fake);
        db.Titles.AddRange(
            new Title { Id = 1, Type = "movie", TmdbId = 901, TitleEn = "Popular", Popularity = 30 },
            new Title { Id = 2, Type = "movie", TmdbId = 902, TitleEn = "Middle", Popularity = 20 },
            new Title { Id = 3, Type = "movie", TmdbId = 903, TitleEn = "Rare", Popularity = 10 });
        await db.SaveChangesAsync(TestCt);

        await svc.RunAsync(null, null, TestCt);

        // найпопулярніший — ПЕРШИМ
        Assert.Equal(new[] { 901, 902, 903 }, fake.Calls.Select(c => c.TmdbId).ToArray());
    }

    [Fact]
    public async Task Sweep_MaxRows_Caps_Processed_Count()
    {
        var fake = new FakeTmdbImagesService
        {
            Resolver = (id, _) => new TmdbResolvedImages($"/p{id}.jpg", null),
        };
        var (db, svc) = NewSync(fake);
        db.Titles.AddRange(
            new Title { Id = 1, Type = "movie", TmdbId = 901, TitleEn = "Popular", Popularity = 30 },
            new Title { Id = 2, Type = "movie", TmdbId = 902, TitleEn = "Middle", Popularity = 20 },
            new Title { Id = 3, Type = "movie", TmdbId = 903, TitleEn = "Rare", Popularity = 10 });
        await db.SaveChangesAsync(TestCt);

        var result = await svc.RunAsync(2, null, TestCt);

        Assert.Equal(2, result.Processed);
        // оброблено рівно топ-2 за популярністю
        Assert.Equal(new[] { 901, 902 }, fake.Calls.Select(c => c.TmdbId).ToArray());
        Assert.Null(db.Titles.AsNoTracking().First(t => t.Id == 3).PosterPath);
    }

    [Fact]
    public async Task Sweep_Paginates_Through_More_Than_One_Batch_Without_Gaps()
    {
        var fake = new FakeTmdbImagesService
        {
            Resolver = (id, _) => new TmdbResolvedImages($"/p{id}.jpg", null),
        };
        var (db, svc) = NewSync(fake);
        // 105 тайтлів (> батчу 100): keyset-курсор має добрати хвіст
        var popByTmdb = new Dictionary<int, int>();
        for (var i = 0; i < 105; i++)
        {
            var pop = 1000 - i;
            popByTmdb[5000 + i] = pop;
            db.Titles.Add(new Title { Id = 100 + i, Type = "movie", TmdbId = 5000 + i, TitleEn = $"T{i}", Popularity = pop });
        }
        await db.SaveChangesAsync(TestCt);

        var result = await svc.RunAsync(null, null, TestCt);

        Assert.Equal(105, result.Processed);
        Assert.Equal(105, fake.Calls.Count);
        Assert.Equal(popByTmdb.Keys.ToHashSet(), fake.Calls.Select(c => c.TmdbId).ToHashSet()); // без дублів і пропусків
        var rows = db.Titles.AsNoTracking().ToList();
        Assert.All(rows, t => Assert.Equal($"/p{t.TmdbId}.jpg", t.PosterPath));
        // строгий спадний порядок popularity
        for (var i = 1; i < fake.Calls.Count; i++)
            Assert.True(popByTmdb[fake.Calls[i - 1].TmdbId] >= popByTmdb[fake.Calls[i].TmdbId],
                $"порядок порушено на позиції {i}");
    }

    // ── Аніме/манга покриття (внутрішній branch по type/tmdb_id) ──

    [Fact]
    public async Task Anime_Sweep_Fills_Poster_From_Shikimori()
    {
        var handler = new StubHttpHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/api/animes/1234")
                ? StubHttp.Json("{\"id\":1234,\"image\":\"/system/animes/original/1234.jpg\"}")
                : null);
        var (db, svc) = NewSync(handler: handler);
        db.Titles.Add(new Title
        {
            Id = 20, Type = "anime", ShikimoriId = 1234, TitleEn = "Frieren", Popularity = 5,
        });
        await db.SaveChangesAsync(TestCt);

        var result = await svc.RunAsync(null, null, TestCt);

        Assert.Equal((1, 1), (result.Processed, result.Updated));
        Assert.Equal("https://shikimori.one/system/animes/original/1234.jpg",
            db.Titles.AsNoTracking().First(t => t.Id == 20).PosterPath);
    }

    [Fact]
    public async Task Anime_Sweep_Fills_Poster_From_Jikan_When_MalId()
    {
        var handler = new StubHttpHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/manga/555")
                ? StubHttp.Json("""{"data":{"images":{"jpg":{"image_url":"https://cdn.myanimelist.net/x.jpg","large_image_url":"https://cdn.myanimelist.net/large-x.jpg"}}}}""")
                : null);
        var (db, svc) = NewSync(handler: handler);
        db.Titles.Add(new Title
        {
            Id = 21, Type = "manga", MalId = 555, TitleEn = "Berserk", Popularity = 4,
        });
        await db.SaveChangesAsync(TestCt);

        await svc.RunAsync(null, null, TestCt);

        Assert.Equal("https://cdn.myanimelist.net/large-x.jpg",
            db.Titles.AsNoTracking().First(t => t.Id == 21).PosterPath);
    }

    [Fact]
    public async Task Jikan_CircuitBreaker_Stops_After_Three_Source_Failures()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            };
            return resp;
        });
        var (db, svc) = NewSync(handler: handler);
        for (var i = 0; i < 5; i++)
            db.Titles.Add(new Title { Id = 30 + i, Type = "anime", MalId = 400 + i, TitleEn = $"J{i}", Popularity = 50 - i });
        await db.SaveChangesAsync(TestCt);

        await svc.RunAsync(null, null, TestCt);

        // 3 невдачі підряд → гілка вимкнена: 4-й і 5-й тайтли НЕ стукали в джерело
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(db.Titles.AsNoTracking().ToList(), t => Assert.Null(t.PosterPath));
    }

    // ── (d) 429-ретрай шанує Retry-After (з капом) ──

    [Fact]
    public async Task TmdbImagesService_Retries_429_And_Honors_RetryAfter()
    {
        var attempt = 0;
        var handler = new StubHttpHandler(_ =>
        {
            var n = Interlocked.Increment(ref attempt);
            return n == 1 ? Resp429(TimeSpan.FromSeconds(1)) : StubHttp.Json("{\"poster_path\":\"/abc.jpg\",\"backdrop_path\":\"/bcd.jpg\"}");
        });
        var svc = NewTmdbImages(handler);

        var sw = Stopwatch.StartNew();
        var resolved = await svc.ResolveAsync(42, "movie", TestCt);
        sw.Stop();

        Assert.NotNull(resolved);
        Assert.Equal("/abc.jpg", resolved!.PosterPath);
        Assert.Equal("https://image.tmdb.org/t/p/w780/abc.jpg", resolved.PosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/bcd.jpg", resolved.BackdropUrl);
        Assert.Equal(2, attempt); // ретрай відбувся
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(0.95),
            $"Retry-After не поважено: {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task TmdbImagesService_Caps_Long_RetryAfter()
    {
        var attempt = 0;
        var handler = new StubHttpHandler(_ =>
        {
            var n = Interlocked.Increment(ref attempt);
            return n == 1 ? Resp429(TimeSpan.FromSeconds(120)) : StubHttp.Json("{\"poster_path\":\"/ok.jpg\"}");
        });
        var svc = NewTmdbImages(handler, retryAfterCap: TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        var resolved = await svc.ResolveAsync(42, "movie", TestCt);
        sw.Stop();

        Assert.NotNull(resolved);
        Assert.Equal(2, attempt);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(0.18),
            $"кап відпрацював як нуль: {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2),
            $"довгий Retry-After НЕ обрізано капом: {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task TmdbImagesService_Without_RetryAfter_Header_Retries_With_Backoff()
    {
        var attempt = 0;
        var handler = new StubHttpHandler(_ =>
        {
            var n = Interlocked.Increment(ref attempt);
            return n == 1 ? Resp429WithoutHeader() : StubHttp.Json("{\"poster_path\":\"/ok.jpg\"}");
        });
        var svc = NewTmdbImages(handler, throttleMs: 10);

        var resolved = await svc.ResolveAsync(42, "movie", TestCt);

        Assert.NotNull(resolved);
        Assert.Equal("/ok.jpg", resolved!.PosterPath);
        Assert.Equal(2, attempt); // 429 без заголовка → обережний 1с-фолбек, все одно ретрай
    }

    [Fact]
    public async Task TmdbImagesService_404_Returns_Null_Without_Retries()
    {
        var attempt = 0;
        var handler = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref attempt);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });
        var svc = NewTmdbImages(handler);

        var resolved = await svc.ResolveAsync(42, "movie", TestCt);

        Assert.Null(resolved);
        Assert.Equal(1, attempt); // 404 — без ретраїв
    }

    // ── Тротлінг і кеш деталей ──

    [Fact]
    public async Task TmdbImagesService_Throttles_Consecutive_Requests()
    {
        var timestamps = new List<DateTimeOffset>();
        var handler = new StubHttpHandler(_ =>
        {
            lock (timestamps) timestamps.Add(DateTimeOffset.UtcNow);
            return StubHttp.Json("{\"poster_path\":\"/p.jpg\"}");
        });
        var svc = NewTmdbImages(handler, throttleMs: 300);

        await svc.ResolveAsync(1, "movie", TestCt);
        await svc.ResolveAsync(2, "movie", TestCt);

        Assert.Equal(2, timestamps.Count);
        var gap = timestamps[1] - timestamps[0];
        Assert.True(gap >= TimeSpan.FromMilliseconds(290),
            $"пауза між запитами менша 300мс: {gap.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task TmdbImagesService_Uses_Cached_Details_Without_Http()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("HTTP не мав викликатись — є кеш"));
        var cache = new TestDb.CacheStub();
        cache.Items["tmdb|details:movie:555:en-US"] = "{\"id\":555,\"poster_path\":\"/cached.jpg\"}";
        var svc = new TmdbImagesService(
            new HttpClient(handler), cache, Options.Create(new TmdbOptions { ApiKey = "k" }),
            Logs.Null<TmdbImagesService>(), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(3), 3);

        var resolved = await svc.ResolveAsync(555, "movie", TestCt);

        Assert.NotNull(resolved);
        Assert.Equal("/cached.jpg", resolved!.PosterPath);
        Assert.Null(resolved.BackdropPath); // null-безпечно: нема поля → null
        Assert.Equal("https://image.tmdb.org/t/p/w780/cached.jpg", resolved.PosterUrl);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TmdbImagesService_Without_ApiKey_Skips_Http()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("HTTP не мав викликатись — ключа нема"));
        var svc = new TmdbImagesService(
            new HttpClient(handler), new TestDb.CacheStub(), Options.Create(new TmdbOptions { ApiKey = "" }),
            Logs.Null<TmdbImagesService>(), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(3), 3);

        Assert.False(svc.IsConfigured);
        Assert.Null(await svc.ResolveAsync(1, "movie", TestCt));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void ParsePaths_Null_Safe_When_Fields_Missing()
    {
        var resolved = TmdbImagesService.ParsePaths("{\"id\":1,\"title\":\"x\"}");

        Assert.Null(resolved.PosterPath);
        Assert.Null(resolved.BackdropPath);
        Assert.Null(resolved.PosterUrl);
        Assert.Null(resolved.BackdropUrl);
    }

    // ── (c) On-demand у GET /api/title/{id} (WAF, підміна ITmdbImagesService в DI) ──

    [Fact]
    public async Task Detail_OnDemand_Resolves_Poster_Writes_Db_And_Queues_Job()
    {
        var factory = new PosterSourceTestFactory();
        factory.Images.ById[777] = new TmdbResolvedImages("/poster777.jpg", "/back777.jpg");
        await factory.SeedAsync(new Title { Id = 6001, Type = "movie", TmdbId = 777, TitleEn = "Delta", Popularity = 1 });
        var client = factory.CreateDefaultClient();

        var resp = await client.GetAsync("/api/title/6001", TestCt);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(TestCt);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("https://image.tmdb.org/t/p/w780/poster777.jpg", json.GetProperty("posterUrl").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/original/back777.jpg", json.GetProperty("backdropUrl").GetString());

        // fill-only запис у БД (наступні перегляди — без HTTP)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            var row = await db.Titles.AsNoTracking().FirstAsync(t => t.Id == 6001, TestCt);
            Assert.Equal("/poster777.jpg", row.PosterPath);
            Assert.Equal("/back777.jpg", row.BackdropPath);
        }

        // в обох випадках — точкова фоновa джоба в черзі
        var queue = factory.Services.GetRequiredService<PosterSourceSyncQueue>();
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Detail_OnDemand_Slow_Resolve_Does_Not_Block_Response()
    {
        var factory = new PosterSourceTestFactory();
        factory.Images.Delay = TimeSpan.FromSeconds(3); // довше 1.5с капа
        await factory.SeedAsync(new Title { Id = 6002, Type = "movie", TmdbId = 888, TitleEn = "Echo", Popularity = 1 });
        var client = factory.CreateDefaultClient();

        var sw = Stopwatch.StartNew();
        var resp = await client.GetAsync("/api/title/6002", TestCt);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4),
            $"відповідь заблоковано довше капа: {sw.Elapsed.TotalSeconds:F2}s");
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestCt)).RootElement;
        Assert.Null(json.GetProperty("posterUrl").GetString()); // не встигли — у відповіді нічого

        // але точкова джоба поставлена в чергу — БД добереться фоном
        var queue = factory.Services.GetRequiredService<PosterSourceSyncQueue>();
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Detail_Without_Registered_Images_Service_Still_Works()
    {
        var factory = new PosterSourceTestFactory { RegisterImages = false };
        await factory.SeedAsync(new Title { Id = 6003, Type = "movie", TmdbId = 999, TitleEn = "Foxtrot", Popularity = 1 });
        var client = factory.CreateDefaultClient();

        var resp = await client.GetAsync("/api/title/6003", TestCt);

        // сервіс не зареєстрований → тихий no-op (без 500), черга не засмічується
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestCt)).RootElement;
        Assert.Null(json.GetProperty("posterUrl").GetString());
        var queue = factory.Services.GetRequiredService<PosterSourceSyncQueue>();
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Sweep_Imdb_Only_Title_Resolves_Via_Imdb_Bridge_And_Backfills_TmdbId()
    {
        var factory = new PosterSourceTestFactory();
        factory.Images.ByImdb["tt0183523"] = new TmdbImdbResolved(1701, "/mission-to-mars.jpg", null);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Titles.Add(new Title { Id = 7001, Type = "movie", ImdbId = "tt0183523", TitleEn = "Mission to Mars", Popularity = 10 });
            await db.SaveChangesAsync(TestCt);
        }
        var client = factory.CreateDefaultClient();

        var resp = await client.GetAsync("/api/title/7001", TestCt);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestCt)).RootElement;
        Assert.Contains("/mission-to-mars.jpg", json.GetProperty("posterUrl").GetString());

        // on-demand поставив джобу точкового доціплення (hosted вимкнений → Count==1)
        var queue = factory.Services.GetRequiredService<PosterSourceSyncQueue>();
        Assert.Equal(1, queue.Count);
        var svc = factory.Services.GetRequiredService<PosterSourceSyncService>();
        await svc.RunAsync(null, 7001, TestCt);

        Assert.Equal(1, factory.Images.ImdbCalls); // міст викликано рівно раз
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            var title = await db.Titles.AsNoTracking().SingleAsync(t => t.Id == 7001, TestCt);
            Assert.Equal("/mission-to-mars.jpg", title.PosterPath);
            Assert.Equal(1701, title.TmdbId); // бекфіл tmdb_id
        }
    }

    [Fact]
    public async Task Sweep_Tmdb_Id_Title_Does_Not_Call_Imdb_Bridge()
    {
        var factory = new PosterSourceTestFactory();
        factory.Images.ById[871] = new TmdbResolvedImages("/terminator.jpg", null);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Titles.Add(new Title { Id = 7002, Type = "movie", TmdbId = 871, TitleEn = "Terminator", Popularity = 9 });
            await db.SaveChangesAsync(TestCt);
        }

        var svc = factory.Services.GetRequiredService<PosterSourceSyncService>();
        await svc.RunAsync(10, null, TestCt);

        Assert.Equal(0, factory.Images.ImdbCalls); // з tmdb_id міст не потрібен
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            var title = await db.Titles.AsNoTracking().SingleAsync(t => t.Id == 7002, TestCt);
            Assert.Equal("/terminator.jpg", title.PosterPath);
        }
    }
}

/// <summary>
/// WAF-фабрика для on-demand тестів постерів: InMemory-БД + fake ITmdbImagesService +
/// черга poster-source-sync ( BackgroundService у тестах не стартує — джобу перевіряємо через Count черги).
/// </summary>
public sealed class PosterSourceTestFactory : WebApplicationFactory<Program>
{
    public string DbName { get; } = $"poster-api-{Guid.NewGuid():N}";

    /// <summary>Fake-резолвер TMDB (реєструється в DI, коли RegisterImages).</summary>
    public FakeTmdbImagesService Images { get; } = new();

    /// <summary>false → сервіс НЕ зареєстрований (регресія «до wiring Program.cs» — без 500).</summary>
    public bool RegisterImages { get; set; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing"); // hosted-служби вимкнені (гейт !Testing): черга не висмоктується до ассертів
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GUEST_PROFILE_ID"] = "1",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
            services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(DbName));
            if (RegisterImages)
                services.AddSingleton<ITmdbImagesService>(Images);
            services.AddSingleton<PosterSourceSyncQueue>();
        });
    }

    /// <summary>Засіяти тайтли для тесту (id/tmdbId задаються явно).</summary>
    public async Task SeedAsync(params Title[] titles)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        db.Titles.AddRange(titles);
        await db.SaveChangesAsync();
    }
}
