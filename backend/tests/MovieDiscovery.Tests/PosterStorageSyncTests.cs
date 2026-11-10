using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Api.Controllers;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Storage;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// Тести poster-storage синку (постери poster_path → власне S3-сховище MinIO → poster_local):
/// ключ-неймінг за Content-Type, put+update через фейкове сховище, fail-closed без запитів,
/// ретрай 429 з шануванням Retry-After, віддача контролером з immutable-кешем.
/// Реальних мережевих/MinIO викликів нема (StubHttpHandler + FakePosterStorage).
/// </summary>
public class PosterStorageSyncTests
{
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x20];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A];

    private static MovieDiscoveryDbContext NewDb() => TestDb.Create($"poster-sync-{Guid.NewGuid():N}");

    private static PosterStorageSyncService NewService(
        MovieDiscoveryDbContext db,
        IPosterStorage storage,
        HttpMessageHandler handler,
        PosterStorageSyncOptions? options = null) =>
        new(new HttpClient(handler), db, storage,
            NullLogger<PosterStorageSyncService>.Instance,
            Options.Create(options ?? new PosterStorageSyncOptions { RequestDelayMs = 0 }));

    private static HttpResponseMessage ImageResponse(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    // ── (a) ключ-неймінг за Content-Type ──

    [Theory]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/jpg", ".jpg")]
    [InlineData("IMAGE/WEBP", ".webp")]
    [InlineData("image/webp", ".webp")]
    [InlineData("image/png", ".png")]
    [InlineData("application/octet-stream", ".jpg")]
    [InlineData("", ".jpg")]
    [InlineData(null, ".jpg")]
    public void ExtensionForContentType_Maps_Expected_Extension(string? contentType, string expected) =>
        Assert.Equal(expected, PosterStorageSyncService.ExtensionForContentType(contentType));

    [Fact]
    public void StorageKeyFor_Uses_Hex8_TitleId_And_Extension()
    {
        Assert.Equal("posters/0000002a.jpg", PosterStorageSyncService.StorageKeyFor(42, ".jpg"));
        Assert.Equal("posters/00000abc.webp", PosterStorageSyncService.StorageKeyFor(0xABC, ".webp"));
        Assert.Equal("posters/0000f00d.png", PosterStorageSyncService.StorageKeyFor(0xF00D, ".png"));
    }

    [Fact]
    public void PublicUrlFor_Maps_Key_To_Api_Route() =>
        Assert.Equal("/api/img/posters/0000002a.jpg",
            PosterStorageSyncService.PublicUrlFor("posters/0000002a.jpg"));

    [Theory]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/a/ab/Poster.jpg", "https://upload.wikimedia.org/wikipedia/commons/a/ab/Poster.jpg")]
    [InlineData("https://cdn.anilist.co/file/anilistcdn/media/anime/cover/large/b2/x.jpg", "https://cdn.anilist.co/file/anilistcdn/media/anime/cover/large/b2/x.jpg")]
    [InlineData("/tmdb/abc.jpg", null)]   // TMDB-шлях: джерела нема (TMDB відхилено власником)
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void ResolveSourceUrl_Accepts_Only_Absolute_Urls(string? posterPath, string? expected) =>
        Assert.Equal(expected, PosterStorageSyncService.ResolveSourceUrl(posterPath));

    // ── (b) fake IPosterStorage: синк ставить файл і оновлює poster_local ──

    [Fact]
    public async Task Sync_Puts_Poster_By_Popularity_And_Updates_PosterLocal()
    {
        var db = NewDb();
        var low = new Title { TitleEn = "Low", PosterPath = "https://commons.example/w/Poster1.jpg", Popularity = 10 };
        var top = new Title { TitleEn = "Top", PosterPath = "https://anilist.example/cover/large/x.jpg", Popularity = 99 };
        var fake = new Title { TitleEn = "Fake", PosterPath = "https://i.ytimg.com/vi/abc/hqdefault.jpg", Popularity = 200 };
        var noPoster = new Title { TitleEn = "NoPoster", PosterPath = null, Popularity = 300 };
        var notUrl = new Title { TitleEn = "TmdbPath", PosterPath = "/tmdb/path.jpg", Popularity = 5 };
        db.Titles.AddRange(low, top, fake, noPoster, notUrl);
        await db.SaveChangesAsync();

        var handler = new StubHttpHandler(_ => ImageResponse(JpegBytes, "image/jpeg"));
        var storage = new FakePosterStorage(enabled: true);
        var svc = NewService(db, storage, handler);

        var result = await svc.RunAsync(null);

        // Кандидати: top + low + notUrl (ytimg відфільтровано на вибірці, порожній poster_path — не кандидат)
        Assert.Equal(2, result.Stored);
        Assert.Equal(1, result.Skipped);  // notUrl (чесний skip)
        Assert.Equal(2, handler.Requests.Count); // ytimg — жодного запиту
        // Порядок popularity DESC: top (99) раніше low (10)
        Assert.Equal(top.PosterPath, handler.Requests[0]);
        Assert.Equal(low.PosterPath, handler.Requests[1]);

        // PUT: ключ за titleId:x8 + ext за Content-Type, вміст і тип — як у відповіді
        var topPut = Assert.Single(storage.Puts, p => p.Key == $"posters/{top.Id:x8}.jpg");
        Assert.Equal("image/jpeg", topPut.ContentType);
        Assert.Equal(JpegBytes, topPut.Content);

        // БД: poster_local = публічний шлях маршруту /api/img/posters/{key}
        var topRow = await db.Titles.AsNoTracking().SingleAsync(t => t.Id == top.Id);
        Assert.Equal($"/api/img/posters/{top.Id:x8}.jpg", topRow.PosterLocal);
        var lowRow = await db.Titles.AsNoTracking().SingleAsync(t => t.Id == low.Id);
        Assert.Equal($"/api/img/posters/{low.Id:x8}.jpg", lowRow.PosterLocal);
        var skippedRow = await db.Titles.AsNoTracking().SingleAsync(t => t.Id == notUrl.Id);
        Assert.Null(skippedRow.PosterLocal); // skip чесно лишає null
        var fakeRow = await db.Titles.AsNoTracking().SingleAsync(t => t.Id == fake.Id);
        Assert.Null(fakeRow.PosterLocal);    // ytimg-фейк не синкається (чистить інша джоба)

        // sync_state записано
        var state = await db.SyncStates.SingleAsync(s => s.Source == PosterStorageSyncService.Source);
        Assert.Equal(2, state.LastCount);
    }

    [Fact]
    public async Task Sync_Keys_Png_And_Webp_By_Response_ContentType()
    {
        var db = NewDb();
        var pngTitle = new Title { TitleEn = "Png", PosterPath = "https://commons.example/x.png", Popularity = 20 };
        var webpTitle = new Title { TitleEn = "Webp", PosterPath = "https://commons.example/y", Popularity = 15 };
        db.Titles.AddRange(pngTitle, webpTitle);
        await db.SaveChangesAsync();

        var handler = new StubHttpHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/x.png")
            ? ImageResponse(PngBytes, "image/png")
            : ImageResponse(PngBytes, "image/webp"));
        var storage = new FakePosterStorage(enabled: true);
        var svc = NewService(db, storage, handler);

        await svc.RunAsync(null);

        Assert.Contains(storage.Puts, p => p.Key == $"posters/{pngTitle.Id:x8}.png" && p.ContentType == "image/png");
        Assert.Contains(storage.Puts, p => p.Key == $"posters/{webpTitle.Id:x8}.webp" && p.ContentType == "image/webp");
    }

    // ── (c) enabled=false → джоба не робить запитів ──

    [Fact]
    public async Task Sync_With_Disabled_Storage_Makes_No_Requests_And_Touches_Nothing()
    {
        var db = NewDb();
        db.Titles.Add(new Title { TitleEn = "Some", PosterPath = "https://commons.example/p.jpg", Popularity = 50 });
        await db.SaveChangesAsync();

        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("ЖОДНИХ запитів при Enabled=false"));
        var storage = new FakePosterStorage(enabled: false);
        var svc = NewService(db, storage, handler);

        var result = await svc.RunAsync(null);

        Assert.Equal(new PosterStorageSyncResult(0, 0, 0), result);
        Assert.Empty(storage.Puts);

        var row = await db.Titles.AsNoTracking().SingleAsync();
        Assert.Null(row.PosterLocal);
        // sync_state при вимкненому сховищі НЕ пишеться (прогіну не було)
        Assert.False(await db.SyncStates.AnyAsync(s => s.Source == PosterStorageSyncService.Source));
    }

    [Fact]
    public async Task Sync_Validates_MaxRows_Range()
    {
        var db = NewDb();
        var svc = NewService(db, new FakePosterStorage(true), new StubHttpHandler(_ => null));

        Assert.True(PosterStorageSyncService.IsValidMaxRows(null));
        Assert.True(PosterStorageSyncService.IsValidMaxRows(1));
        Assert.True(PosterStorageSyncService.IsValidMaxRows(500_000));
        Assert.False(PosterStorageSyncService.IsValidMaxRows(0));
        Assert.False(PosterStorageSyncService.IsValidMaxRows(500_001));
        Assert.False(PosterStorageSyncService.IsValidMaxRows(-5));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.RunAsync(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => svc.RunAsync(500_001));
    }

    // ── (d) ретрай на 429 шанує Retry-After (fake HttpMessageHandler) ──

    [Fact]
    public async Task Sync_Retries_On_429_And_Honors_Retry_After_Header()
    {
        var db = NewDb();
        var title = new Title { TitleEn = "Retry", PosterPath = "https://commons.example/r.jpg", Popularity = 7 };
        db.Titles.Add(title);
        await db.SaveChangesAsync();

        var attempts = 0;
        var handler = new StubHttpHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.TryAddWithoutValidation("Retry-After", "0"); // шануй заголовок (тестово 0с)
                return throttled;
            }
            return ImageResponse(JpegBytes, "image/jpeg");
        });
        var storage = new FakePosterStorage(enabled: true);
        var svc = NewService(db, storage, handler, new PosterStorageSyncOptions { RequestDelayMs = 0, MaxAttempts = 4 });

        var result = await svc.RunAsync(null);

        Assert.Equal(2, attempts);                    // 429 → ретрай
        Assert.Equal(1, result.Stored);
        Assert.Equal($"posters/{title.Id:x8}.jpg", Assert.Single(storage.Puts).Key);
    }

    [Fact]
    public async Task Sync_Gives_Up_After_Max_Attempts_With_Honest_Skip()
    {
        var db = NewDb();
        db.Titles.Add(new Title { TitleEn = "Always429", PosterPath = "https://commons.example/q.jpg", Popularity = 3 });
        await db.SaveChangesAsync();

        var attempts = 0;
        var handler = new StubHttpHandler(_ =>
        {
            attempts++;
            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            throttled.Headers.TryAddWithoutValidation("Retry-After", "0");
            return throttled;
        });
        var storage = new FakePosterStorage(enabled: true);
        var svc = NewService(db, storage, handler, new PosterStorageSyncOptions { RequestDelayMs = 0, MaxAttempts = 4 });

        var result = await svc.RunAsync(null);

        Assert.Equal(4, attempts);   // вичерпано спроби
        Assert.Equal(0, result.Stored);
        Assert.Equal(1, result.Skipped); // чесний skip замість падіння джоби
        Assert.Empty(storage.Puts);
    }

    [Fact]
    public void RetryDelay_Honors_Header_Caps_At_Three_Seconds_And_Falls_Back()
    {
        var fallback = TimeSpan.FromSeconds(1);
        var cap = TimeSpan.FromSeconds(3);
        Assert.Equal(TimeSpan.FromSeconds(2), PosterStorageSyncService.RetryDelay(TimeSpan.FromSeconds(2), fallback, cap));
        Assert.Equal(TimeSpan.Zero, PosterStorageSyncService.RetryDelay(TimeSpan.Zero, fallback, cap));
        Assert.Equal(cap, PosterStorageSyncService.RetryDelay(TimeSpan.FromSeconds(10), fallback, cap)); // стеля ≤3с
        Assert.Equal(TimeSpan.FromSeconds(2), PosterStorageSyncService.RetryDelay(null, TimeSpan.FromSeconds(2), cap));
        Assert.Equal(cap, PosterStorageSyncService.RetryDelay(null, TimeSpan.FromSeconds(7), cap));
        // від'ємний/сміттєвий Retry-After → fallback
        Assert.Equal(fallback, PosterStorageSyncService.RetryDelay(TimeSpan.FromSeconds(-1), fallback, cap));
    }

    // ── контролер: стрім з immutable-кешем, 404, метадані ──

    [Fact]
    public async Task PosterImagesController_Streams_Poster_With_Immutable_Cache()
    {
        var storage = new FakePosterStorage(enabled: true);
        storage.Seed("posters/00000001.jpg", JpegBytes);
        var controller = NewController(storage);

        var result = await controller.Get("00000001.jpg", CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("public,max-age=31536000,immutable", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task PosterImagesController_Meta_Returns_Contract_Record()
    {
        var storage = new FakePosterStorage(enabled: true);
        storage.Seed("posters/00000001.jpg", JpegBytes);
        var controller = NewController(storage, queryString: "?meta=1");

        var result = await controller.Get("00000001.jpg", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var meta = Assert.IsType<PosterImageResponse>(ok.Value);
        Assert.Equal("posters/00000001.jpg", meta.Key);
        Assert.Equal("image/jpeg", meta.ContentType);
        Assert.Equal(JpegBytes.Length, meta.SizeBytes);
    }

    [Fact]
    public async Task PosterImagesController_Returns_404_For_Missing_Disabled_And_Evil_Keys()
    {
        var storage = new FakePosterStorage(enabled: true);
        storage.Seed("posters/00000001.jpg", JpegBytes);
        var controller = NewController(storage);

        Assert.IsType<NotFoundObjectResult>(await controller.Get("deadbeef.jpg", CancellationToken.None)); // нема в бакеті
        Assert.IsType<NotFoundObjectResult>(await controller.Get("../evil.jpg", CancellationToken.None)); // path-traversal
        Assert.IsType<NotFoundObjectResult>(await controller.Get("", CancellationToken.None));            // порожній
        Assert.IsType<NotFoundObjectResult>(await controller.Get("in%20valid$.jpg", CancellationToken.None)); // чужий символ

        var disabled = NewController(new FakePosterStorage(enabled: false));
        Assert.IsType<NotFoundObjectResult>(await disabled.Get("00000001.jpg", CancellationToken.None)); // fail-closed
    }

    private static PosterImagesController NewController(
        IPosterStorage storage,
        string queryString = "")
    {
        var controller = new PosterImagesController(storage);
        var http = new DefaultHttpContext();
        if (queryString.Length > 0)
            http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(queryString);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }
}

/// <summary>Фейкове сховище: PUT записує в пам'ять, GET віддає записане (реальної мережі нема).</summary>
internal sealed class FakePosterStorage(bool enabled) : IPosterStorage
{
    public sealed record PutRecord(string Key, byte[] Content, string ContentType);

    public bool Enabled { get; set; } = enabled;
    public List<PutRecord> Puts { get; } = [];
    public Dictionary<string, byte[]> Objects { get; } = [];

    public void Seed(string key, byte[] bytes) => Objects[key] = bytes;

    public Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("fail-closed: PUT при вимкненому сховищі");
        using var ms = new MemoryStream();
        content.CopyTo(ms);
        var record = new PutRecord(key, ms.ToArray(), contentType);
        Puts.Add(record);
        Objects[key] = record.Content;
        return Task.FromResult(key);
    }

    public Task<Stream?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);
}
