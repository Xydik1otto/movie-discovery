using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Embeddings;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Sync;
using MovieDiscovery.Infrastructure.Tmdb;
using Npgsql;

namespace MovieDiscovery.Tests;

public sealed class CatalogSyncTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("/discover/tv")]
    [InlineData("/movie/44/external_ids")]
    [InlineData("/discover/tv", "{}")]
    [InlineData("/discover/tv", "{\"results\":null}")]
    [InlineData(null, null, true)]
    public async Task ScheduledTmdbSync_Records_Only_Complete_Discovery(
        string? failedPath, string? invalidResponse = null, bool emptyTv = false)
    {
        var requestCount = 0;
        using var http = StubHttp.Client(request =>
        {
            requestCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (emptyTv && path is ("/tv/popular" or "/discover/tv"))
                return StubHttp.Json("""{"results":[]}""");
            if (path == failedPath)
                return invalidResponse is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : StubHttp.Json(invalidResponse);

            // Movie and TV IDs belong to separate namespaces.
            if (path.EndsWith("/external_ids", StringComparison.Ordinal))
                return StubHttp.Json(path switch
                {
                    "/movie/42/external_ids" => """{"id":42,"imdb_id":"tt0000042"}""",
                    "/movie/43/external_ids" => """{"id":43,"imdb_id":"tt0000043"}""",
                    "/movie/44/external_ids" => """{"id":44,"imdb_id":null}""",
                    "/tv/42/external_ids" => """{"id":42,"imdb_id":"tt0000044"}""",
                    _ => throw new InvalidOperationException(path),
                });

            return StubHttp.Json(path.Contains("movie", StringComparison.Ordinal)
                ? """{"results":[{"id":42,"title":"Movie"},{"id":43,"title":"Conflict"},{"id":44,"title":"Unidentified"}]}"""
                : """{"results":[{"id":42,"name":"Series"}]}""");
        });

        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(databaseName));
        services.AddSingleton<EmbeddingQueue>();
        services.AddSingleton<IEmbedder, OfflineEmbedder>();
        services.AddSingleton(new TmdbClient(http, new TestDb.CacheStub(),
            Options.Create(new TmdbOptions { ApiKey = "test" }), Logs.Null<TmdbClient>()));
        await using var provider = services.BuildServiceProvider();
        var options = Options.Create(new CatalogSyncOptions { TmdbIntervalHours = 24 });
        var worker = new SyncBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(), options,
            Logs.Null<SyncBackgroundService>());
        var job = new SyncJob(1, "all", Automatic: true);

        int originalId;
        await using (var seed = provider.CreateAsyncScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            var movie = new Title { ImdbId = "tt0000042", Type = "movie", TitleEn = "IMDb movie" };
            seedDb.Titles.AddRange(movie, new Title { ImdbId = "tt0000043", Type = "anime" });
            await seedDb.SaveChangesAsync();
            originalId = movie.Id;
        }

        var incomplete = failedPath is not null || emptyTv;
        if (incomplete)
            await Assert.ThrowsAsync<InvalidDataException>(() => worker.RunJobAsync(job, default));
        else
            await worker.RunJobAsync(job, default);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var titles = await db.Titles.OrderBy(t => t.ImdbId).ToArrayAsync();
        Assert.Collection(titles.Take(2),
            movie => Assert.Equal((originalId, 42, "movie", "Movie"), (movie.Id, movie.TmdbId!.Value, movie.Type, movie.TitleEn)),
            conflict => { Assert.Equal("anime", conflict.Type); Assert.Null(conflict.TmdbId); });
        Assert.Equal(emptyTv ? 2 : 3, titles.Length);
        if (!emptyTv)
        {
            var tv = titles[2];
            Assert.Equal((42, "series", "Series"), (tv.TmdbId!.Value, tv.Type, tv.TitleEn));
        }

        if (incomplete)
        {
            Assert.Empty(await db.SyncStates.ToListAsync());
            return;
        }

        var state = await db.SyncStates.SingleAsync();
        Assert.Equal(SyncBackgroundService.ScheduledKind, state.Kind);
        Assert.Equal(2, state.LastCount);
        Assert.Equal(12, requestCount);

        await worker.RunJobAsync(job, default);
        await db.Entry(state).ReloadAsync();
        Assert.Equal(12, requestCount);
        Assert.Equal(3, await db.Titles.CountAsync());
    }

    [Fact]
    public async Task TmdbSync_Rechecks_Type_After_A_Concurrent_Imdb_Update()
    {
        var externalIdRequests = 0;
        using var http = StubHttp.Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/movie/42/external_ids")
            {
                externalIdRequests++;
                return StubHttp.Json("""{"id":42,"imdb_id":"tt0000042"}""");
            }
            return StubHttp.Json("""{"results":[{"id":42,"title":"Movie"}]}""");
        });

        var race = new ConcurrentImdbUpdate();
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(databaseName).AddInterceptors(race));
        services.AddSingleton<EmbeddingQueue>();
        services.AddSingleton<IEmbedder, OfflineEmbedder>();
        services.AddSingleton(new TmdbClient(http, new TestDb.CacheStub(),
            Options.Create(new TmdbOptions { ApiKey = "test" }), Logs.Null<TmdbClient>()));
        await using var provider = services.BuildServiceProvider();

        await using (var seed = provider.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Titles.Add(new Title { ImdbId = "tt0000042", Type = "movie", TitleEn = "IMDb title" });
            await db.SaveChangesAsync();
        }

        race.ReclassifyAsync = async ct =>
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            (await db.Titles.SingleAsync(ct)).Type = "series";
            await db.SaveChangesAsync(ct);
        };

        using var worker = new SyncBackgroundService(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CatalogSyncOptions()), Logs.Null<SyncBackgroundService>());
        await worker.RunJobAsync(new SyncJob(1, "movie"), default);

        await using var verification = provider.CreateAsyncScope();
        var result = verification.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var title = await result.Titles.SingleAsync();
        Assert.Equal("series", title.Type);
        Assert.Null(title.TmdbId);
        Assert.Equal(1, externalIdRequests);
    }

    private sealed class ConcurrentImdbUpdate : SaveChangesInterceptor
    {
        internal Func<CancellationToken, Task>? ReclassifyAsync { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (ReclassifyAsync is not { } reclassify) return result;
            ReclassifyAsync = null;
            await reclassify(cancellationToken);
            throw new PostgresException("Concurrent title update", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure);
        }
    }

    private sealed class OfflineEmbedder : IEmbedder
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);
    }
}
