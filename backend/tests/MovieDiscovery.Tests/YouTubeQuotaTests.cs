using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Tests;

/// <summary>
/// Денний ГЛОБАЛЬНИЙ ліміт YouTube search-викликів (YouTubeDailySearchQuota):
/// атомарний +1 у IMemoryCache-ключі youtube:search-count:YYYY-MM-DD, ліміт 90/день;
/// гейт в ON-DEMAND шляхах (SyncTitleAsync / EnsureYoutubeLinkAsync) кидає
/// YouTubeQuotaExceededException → контролери маплять на 429 quota_exceeded.
/// Батч-шляхи (RunAsync/SyncAsync → SyncOneAsync/FindAndUpsertAsync) гейт минають.
/// </summary>
public class YouTubeQuotaTests
{
    private static YouTubeDailySearchQuota NewQuota() =>
        new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void KeyFor_HasRequiredFormat_UtcDate()
    {
        Assert.Equal("youtube:search-count:2026-11-10",
            YouTubeDailySearchQuota.KeyFor(new DateTime(2026, 11, 10, 23, 59, 59, DateTimeKind.Utc)));
    }

    [Fact]
    public void TryConsume_AllowsUpToDailyLimit_ThenBlocks()
    {
        var quota = NewQuota();

        for (var i = 0; i < YouTubeDailySearchQuota.DailyLimit; i++)
            Assert.True(quota.TryConsume(), $"виклик #{i + 1} мав пройти");

        Assert.False(quota.TryConsume()); // 91-й — вже за лімітом
        Assert.Equal(YouTubeDailySearchQuota.DailyLimit, quota.UsedToday());
    }

    [Fact]
    public void TryConsume_IsAtomic_UnderConcurrency()
    {
        var quota = NewQuota();
        var successes = 0;

        Parallel.For(0, 200, _ =>
        {
            if (quota.TryConsume())
                Interlocked.Increment(ref successes);
        });

        Assert.Equal(YouTubeDailySearchQuota.DailyLimit, successes); // рівно 90, ні більше, ні менше
    }

    [Fact]
    public void QuotaException_IsNotYoutubeApiException()
    {
        // СВІДОМО: м'які catch (YoutubeApiException) у сервісах не мають проковтувати
        // вичерпання квоти — інакше контролер не зміг би віддати 429 quota_exceeded.
        Assert.False(new YouTubeQuotaExceededException() is YoutubeApiException);
        Assert.Equal("Денна квота трейлерів вичерпана, спробуйте завтра",
            new YouTubeQuotaExceededException().Message);
    }

    // ─── Гейт у on-demand шляхах ───

    private static (TitleTrailerSyncService Sync, MovieDiscoveryDbContext Db, YouTubeDailySearchQuota Quota)
        MakeTrailerEnv(YouTubeDailySearchQuota? quota)
    {
        var db = TestDb.Create("ytq_" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new YoutubeOptions { ApiKey = "KEY" });
        var client = new YoutubeClient(
            StubHttp.Client(_ => StubHttp.Json("""{"items": []}""")), opts, Logs.Null<YoutubeClient>());
        var sync = new TitleTrailerSyncService(
            client, new TrailerVerifier(
                StubHttp.Client(_ => StubHttp.Json("""{"items": []}""")), client, Logs.Null<TrailerVerifier>()),
            db, new TestDb.CacheStub(), opts, Logs.Null<TitleTrailerSyncService>(), quota);
        return (sync, db, quota ?? new YouTubeDailySearchQuota(new MemoryCache(new MemoryCacheOptions())));
    }

    private static async Task<Title> SeedAsync(MovieDiscoveryDbContext db)
    {
        var title = new Title { Type = "movie", TitleEn = "Se7en", Year = 1995, ImdbId = "tt0114369" };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        return title;
    }

    [Fact]
    public async Task SyncTitleAsync_ExhaustedQuota_ThrowsQuotaExceeded()
    {
        var (sync, db, quota) = MakeTrailerEnv(NewQuota());
        var title = await SeedAsync(db);
        while (quota.TryConsume()) { } // вичерпали денний ліміт

        await Assert.ThrowsAsync<YouTubeQuotaExceededException>(() => sync.SyncTitleAsync(title.Id));
    }

    [Fact]
    public async Task SyncTitleAsync_AvailableQuota_DoesNotThrow_SearchRuns()
    {
        var (sync, db, quota) = MakeTrailerEnv(NewQuota());
        var title = await SeedAsync(db);

        var added = await sync.SyncTitleAsync(title.Id); // items: [] → 0, БЕЗ винятку

        Assert.Equal(0, added);
        Assert.Equal(1, quota.UsedToday()); // 1 search зараховано
    }

    [Fact]
    public async Task SyncTitleAsync_NonConfigured_DoesNotConsumeQuota()
    {
        var db = TestDb.Create("ytq_" + Guid.NewGuid().ToString("N"));
        var quota = NewQuota();
        var opts = Options.Create(new YoutubeOptions { ApiKey = "" }); // без ключа — on-demand неможливий
        var client = new YoutubeClient(StubHttp.Client(_ => null), opts, Logs.Null<YoutubeClient>());
        var sync = new TitleTrailerSyncService(
            client, new TrailerVerifier(StubHttp.Client(_ => StubHttp.Json("{}")), client, Logs.Null<TrailerVerifier>()),
            db, new TestDb.CacheStub(), opts, Logs.Null<TitleTrailerSyncService>(), quota);
        var title = await SeedAsync(db);

        Assert.Equal(0, await sync.SyncTitleAsync(title.Id));
        Assert.Equal(0, quota.UsedToday()); // без ключа search не буде — квоту не палимо
    }

    [Fact]
    public async Task SyncTitleAsync_NullQuota_BehavesAsBefore_NoLimit()
    {
        // Стара сигнатура (quota = null): тестові new-конструкції не ламаються, ліміт не діє.
        var (sync, db, _) = MakeTrailerEnv(quota: null);
        var title = await SeedAsync(db);
        var quota = new YouTubeDailySearchQuota(new MemoryCache(new MemoryCacheOptions())); // пустий, НЕ переданий

        for (var i = 0; i < YouTubeDailySearchQuota.DailyLimit + 5; i++)
            await sync.SyncTitleAsync(title.Id); // жодного винятку — гейту нема

        Assert.Equal(0, quota.UsedToday()); // лічильник взагалі не залучений
    }

    [Fact]
    public async Task SyncTitleAsync_BatchPath_BypassesGate()
    {
        // Батч RunAsync іде в SyncOneAsync ПОВЗАВЖД — навіть з вичерпаною квотою не кидає.
        var (sync, db, quota) = MakeTrailerEnv(NewQuota());
        await SeedAsync(db);
        while (quota.TryConsume()) { }

        var added = await sync.RunAsync(limit: 10);

        Assert.Equal(0, added); // прогін завершився (порожні items), без quota-винятку
    }

    private static (YouTubeEnrichService Enrich, MovieDiscoveryDbContext Db, YouTubeDailySearchQuota Quota)
        MakeEnrichEnv(YouTubeDailySearchQuota? quota)
    {
        var db = TestDb.Create("ytq_" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new YoutubeOptions { ApiKey = "KEY" });
        var client = new YoutubeClient(
            StubHttp.Client(_ => StubHttp.Json("""{"items": []}""")), opts, Logs.Null<YoutubeClient>());
        var enrich = new YouTubeEnrichService(
            client, db, new TestDb.CacheStub(), opts, Logs.Null<YouTubeEnrichService>(), quota);
        return (enrich, db, quota ?? new YouTubeDailySearchQuota(new MemoryCache(new MemoryCacheOptions())));
    }

    [Fact]
    public async Task EnsureYoutubeLinkAsync_ExhaustedQuota_ThrowsQuotaExceeded()
    {
        var (enrich, db, quota) = MakeEnrichEnv(NewQuota());
        var title = await SeedAsync(db);
        while (quota.TryConsume()) { }

        await Assert.ThrowsAsync<YouTubeQuotaExceededException>(() => enrich.EnsureYoutubeLinkAsync(title.Id));
    }

    [Fact]
    public async Task EnsureYoutubeLinkAsync_LinkAlreadyExists_DoesNotConsumeQuota()
    {
        var (enrich, db, quota) = MakeEnrichEnv(NewQuota());
        var title = await SeedAsync(db);
        db.WatchLinks.Add(new WatchLink
        { TitleId = title.Id, Provider = YouTubeEnrichService.Provider, Url = "https://youtu.be/x", Kind = "stream" });
        await db.SaveChangesAsync();

        Assert.False(await enrich.EnsureYoutubeLinkAsync(title.Id)); // лінк уже є → false
        Assert.Equal(0, quota.UsedToday()); // гейт стоїТЬ ПІСЛЯ перевірки лінка — квоту не палимо
    }

    [Fact]
    public async Task EnsureYoutubeLinkAsync_BatchPath_BypassesGate()
    {
        // Батч SyncAsync іде в FindAndUpsertAsync ПОВЗАВЖД — навіть з вичерпаною квотою не кидає.
        var (enrich, db, quota) = MakeEnrichEnv(NewQuota());
        await SeedAsync(db);
        while (quota.TryConsume()) { }

        var added = await enrich.SyncAsync(limit: 10);

        Assert.Equal(0, added);
    }
}
