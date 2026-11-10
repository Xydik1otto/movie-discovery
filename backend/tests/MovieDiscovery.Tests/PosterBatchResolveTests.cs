using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Tmdb;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// Тести POST /api/posters/batch-resolve (пакетний on-demand резолв постерів сітки каталогу):
/// (a) щасливий шлях — резолв без постера → fill-only у БД + абсолютні URL у відповіді;
/// (b) валідація 1..24 id (25 → 400, порожньо → 400);
/// (c) fill-only: наявний постер не перезаписується і без HTTP;
/// (d) misses (кап 800мс/джерело без картинки) → точкові фонові джоби в черзі (Count);
/// (e) лімітер 15/хв/IP → 429;
/// + unit-ланцюг ResolveOneAsync: аніме → Shikimori, вже заповнений постер → без HTTP.
/// WAF-фабрика PosterSourceTestFactory (env Testing — hosted off; джобу перевіряємо через Count).
/// </summary>
public class PosterBatchResolveTests
{
    private static readonly CancellationToken TestCt = CancellationToken.None;

    private static StringContent Body(params int[] ids) =>
        new($$"""{"titleIds":[{{string.Join(",", ids)}}]}""", Encoding.UTF8, "application/json");

    private static List<int> Misses(JsonElement json) =>
        [.. json.GetProperty("misses").EnumerateArray().Select(e => e.GetInt32())];

    // ── (a) Щасливий шлях: резолв → fill-only у БД → абсолютні URL ──

    [Fact]
    public async Task BatchResolve_Resolves_Missing_Posters_Writes_Db_And_Urls_Are_Absolute()
    {
        var factory = new PosterSourceTestFactory();
        factory.Images.ById[777] = new TmdbResolvedImages("/poster777.jpg", "/back777.jpg");
        factory.Images.ByImdb["tt0183523"] = new TmdbImdbResolved(1701, "/mission.jpg", null);
        await factory.SeedAsync(
            new Title { Id = 6001, Type = "movie", TmdbId = 777, TitleEn = "Delta", Popularity = 1 },
            new Title { Id = 6002, Type = "movie", ImdbId = "tt0183523", TitleEn = "Mission to Mars", Popularity = 2 },
            new Title { Id = 6003, Type = "movie", TmdbId = 555, TitleEn = "HasPoster", Popularity = 3, PosterPath = "/have.jpg" });
        var client = factory.CreateDefaultClient();

        var resp = await client.PostAsync("/api/posters/batch-resolve", Body(6001, 6002, 6003, 999_999), TestCt);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestCt)).RootElement;

        // ключі resolved — рядкові id; URL абсолютні (raw-шлях TMDB → image.tmdb.org)
        var resolved = json.GetProperty("resolved");
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster777.jpg",
            resolved.GetProperty("6001").GetProperty("posterUrl").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/w780/back777.jpg",
            resolved.GetProperty("6001").GetProperty("backdropUrl").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/w500/mission.jpg",
            resolved.GetProperty("6002").GetProperty("posterUrl").GetString());
        Assert.Equal("https://image.tmdb.org/t/p/w500/have.jpg",
            resolved.GetProperty("6003").GetProperty("posterUrl").GetString());
        Assert.Equal([999_999], Misses(json));

        // fill-only запис у БД + бекфіл tmdb_id після imdb-місту
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            var rows = await db.Titles.AsNoTracking().ToDictionaryAsync(t => t.Id, TestCt);
            Assert.Equal("/poster777.jpg", rows[6001].PosterPath);
            Assert.Equal("/back777.jpg", rows[6001].BackdropPath);
            Assert.Equal("/mission.jpg", rows[6002].PosterPath);
            Assert.Equal(1701, rows[6002].TmdbId); // бекфіл tmdb_id
            Assert.Equal("/have.jpg", rows[6003].PosterPath); // наявний не перебито
        }

        // усі встигли → у черзі порожньо (фонові джоби не потрібні)
        Assert.Equal(0, factory.Services.GetRequiredService<PosterSourceSyncQueue>().Count);
    }

    // ── (b) Валідація 1..24 id ──

    [Fact]
    public async Task BatchResolve_More_Than_24_Ids_Returns_400()
    {
        var factory = new PosterSourceTestFactory();
        var client = factory.CreateDefaultClient();

        var tooMany = await client.PostAsync("/api/posters/batch-resolve", Body(Enumerable.Range(1, 25).ToArray()), TestCt);
        var empty = await client.PostAsync("/api/posters/batch-resolve", Body(), TestCt);

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    // ── (c) Fill-only: наявний постер не перезаписується, HTTP не викликається ──

    [Fact]
    public async Task BatchResolve_Existing_Poster_Not_Overwritten_And_No_Http()
    {
        var factory = new PosterSourceTestFactory();
        // якби резолв викликався — він би «перебив» постер; тест ловить саме це
        factory.Images.Resolver = (id, _) => new TmdbResolvedImages($"/new{id}.jpg", null);
        await factory.SeedAsync(new Title
        {
            Id = 6100, Type = "movie", TmdbId = 321, TitleEn = "Full", Popularity = 1, PosterPath = "/existing.jpg",
        });
        var client = factory.CreateDefaultClient();

        var resp = await client.PostAsync("/api/posters/batch-resolve", Body(6100), TestCt);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestCt)).RootElement;
        Assert.Equal("https://image.tmdb.org/t/p/w500/existing.jpg",
            json.GetProperty("resolved").GetProperty("6100").GetProperty("posterUrl").GetString());
        Assert.Empty(json.GetProperty("misses").EnumerateArray());
        Assert.Empty(factory.Images.Calls); // без HTTP
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            var row = await db.Titles.AsNoTracking().FirstAsync(t => t.Id == 6100, TestCt);
            Assert.Equal("/existing.jpg", row.PosterPath); // fill-only: не перебито
        }
    }

    // ── (d) Misses (кап 800мс / джерело без картинки) → фонові джоби в черзі ──

    [Fact]
    public async Task BatchResolve_Misses_Enqueue_Background_Jobs()
    {
        var factory = new PosterSourceTestFactory();
        factory.Images.Delay = TimeSpan.FromSeconds(3); // довше м'якого капа 800мс → не встигає
        await factory.SeedAsync(
            new Title { Id = 6201, Type = "movie", TmdbId = 801, TitleEn = "Slow", Popularity = 1 },
            new Title { Id = 6202, Type = "movie", TmdbId = 802, TitleEn = "NoImage", Popularity = 2 }); // резолвер → null
        var client = factory.CreateDefaultClient();

        var sw = Stopwatch.StartNew();
        var resp = await client.PostAsync("/api/posters/batch-resolve", Body(6201, 6202), TestCt);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"м'який кап 800мс не спрацював: {sw.Elapsed.TotalSeconds:F2}s");
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestCt)).RootElement;
        Assert.Equal([6201, 6202], Misses(json)); // 6201 — таймаут, 6202 — джерело без картинок
        Assert.Empty(json.GetProperty("resolved").EnumerateObject());

        // БД не засмічена (fill-only не пише порожнє), але фонові точкові джоби поставлені
        var queue = factory.Services.GetRequiredService<PosterSourceSyncQueue>();
        Assert.Equal(2, queue.Count);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            var rows = await db.Titles.AsNoTracking().ToDictionaryAsync(t => t.Id, TestCt);
            Assert.Null(rows[6201].PosterPath);
            Assert.Null(rows[6202].PosterPath);
        }
    }

    // ── (e) Лімітер 15 req/хв/IP → 429 ──

    [Fact]
    public async Task BatchResolve_RateLimits_16th_Request_Per_Ip()
    {
        var factory = new PosterSourceTestFactory();
        await factory.SeedAsync(new Title
        {
            Id = 6300, Type = "movie", TmdbId = 900, TitleEn = "R", Popularity = 1, PosterPath = "/p.jpg",
        });
        var client = factory.CreateDefaultClient();

        for (var i = 0; i < 15; i++)
        {
            var ok = await client.PostAsync("/api/posters/batch-resolve", Body(6300), TestCt);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var limited = await client.PostAsync("/api/posters/batch-resolve", Body(6300), TestCt);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var err = JsonDocument.Parse(await limited.Content.ReadAsStringAsync(TestCt)).RootElement;
        Assert.Equal("rate_limited", err.GetProperty("error").GetString());
    }

    // ── Unit-ланцюг ResolveOneAsync (у WAF-фабриці PosterCoverageService реальний → HTTP) ──

    private static (MovieDiscoveryDbContext Db, PosterSourceSyncService Service, FakeTmdbImagesService Fake) NewSync(
        StubHttpHandler? handler = null)
    {
        var db = TestDb.Create($"batch-resolve-{Guid.NewGuid():N}");
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
        var fake = new FakeTmdbImagesService();
        var svc = new PosterSourceSyncService(fake, new CinemetaFake(), coverage, db, Logs.Null<PosterSourceSyncService>());
        return (db, svc, fake);
    }

    [Fact]
    public async Task ResolveOneAsync_Anime_Without_Tmdb_Uses_Coverage_Shikimori()
    {
        var handler = new StubHttpHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/api/animes/1234")
                ? StubHttp.Json("{\"id\":1234,\"image\":\"/system/animes/original/1234.jpg\"}")
                : null);
        var (_, svc, fake) = NewSync(handler);
        var title = new Title { Id = 40, Type = "anime", ShikimoriId = 1234, TitleEn = "Frieren" };

        var (poster, backdrop) = await svc.ResolveOneAsync(title, TestCt);

        Assert.Equal("https://shikimori.one/system/animes/original/1234.jpg", poster); // абсолютний сам по собі
        Assert.Null(backdrop); // у Shikimori/Jikan бекдропів нема
        Assert.Empty(fake.Calls); // TMDB-шлях не чіпали
    }

    [Fact]
    public async Task ResolveOneAsync_Title_With_Poster_Returns_Null_Without_Http()
    {
        var (_, svc, fake) = NewSync();
        var title = new Title { Id = 41, Type = "movie", TmdbId = 111, TitleEn = "A", PosterPath = "/have.jpg" };

        var (poster, backdrop) = await svc.ResolveOneAsync(title, TestCt);

        Assert.Null(poster);
        Assert.Null(backdrop);
        Assert.Empty(fake.Calls); // ідемпотентно: без HTTP
    }
}
