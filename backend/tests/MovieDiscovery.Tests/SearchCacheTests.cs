using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieDiscovery.Api.Controllers;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// TTL-кеш дефолтного пошуку (10.11): /api/search без q/actor/director/keyword кешується
/// на 90 с (IMemoryCache у SearchController). Без HTTP: контролер викликається напряму
/// на InMemory-контексті з реальним MemoryCache. Перевіряємо:
/// 1) другий однаковий виклик повертає ТОЙ САМИЙ об'єкт відповіді з кешу і не ходить
///    у SearchService/БД (контекст disposed між викликами — кеш-промах упав би
///    ObjectDisposedException);
/// 2) кеш-ключ стабільний для еквівалентних (переставлених/дефолтних) параметрів
///    і розрізняє sort/page/lang;
/// 3) умова кешування — лише «каталожні» видачі (q/actor/director/keyword порожні).
/// </summary>
public class SearchCacheTests
{
    private static MovieDiscoveryDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase("search_cache_" + Guid.NewGuid().ToString("N"))
            .Options);

    private static SearchController CreateController(MovieDiscoveryDbContext db) =>
        new(new SearchService(db), db, new MemoryCache(new MemoryCacheOptions()));

    private static async Task SeedAsync(MovieDiscoveryDbContext db, int n)
    {
        for (var i = 1; i <= n; i++)
        {
            db.Titles.Add(new Title
            {
                Type = "movie",
                TitleEn = $"Cache {i:D3}",
                Popularity = 10 - i * 0.1,
                Year = 2000,
            });
        }
        await db.SaveChangesAsync();
    }

    private static object ResponseOf(IActionResult r) => Assert.IsType<OkObjectResult>(r).Value!;

    [Fact]
    public async Task DefaultView_SecondCall_ReturnsSameObjectFromCache_WithoutDb()
    {
        var db = CreateDb();
        await SeedAsync(db, 25);
        var controller = CreateController(db);

        var first = await controller.Get(
            q: null, type: null, genres: null, yearFrom: null, yearTo: null, country: null,
            lang: "uk", minRating: null, maxRating: null, minRuntime: null, maxRuntime: null,
            status: null, sort: "popularity", page: 1, pageSize: 20);
        await db.DisposeAsync(); // другий виклик має бути ПОВНІСТЮ з кешу

        var second = await controller.Get(
            q: null, type: null, genres: null, yearFrom: null, yearTo: null, country: null,
            lang: "uk", minRating: null, maxRating: null, minRuntime: null, maxRuntime: null,
            status: null, sort: "popularity", page: 1, pageSize: 20);

        // Той самий екземпляр відповіді = кеш-хіт (свіжий білд дав би новий об'єкт).
        Assert.Same(ResponseOf(first), ResponseOf(second));
    }

    [Fact]
    public void CacheKey_Stable_ForEquivalentPermutedParams()
    {
        // Позиційні vs іменовані аргументи з тими самими значеннями → той самий ключ.
        var positional = new SearchQuery(
            null, null, [], null, null, null, null, null, null, null, null,
            "popularity", 1, 20);
        var named = new SearchQuery(
            Q: null, Type: null, Genres: [], YearFrom: null, YearTo: null, Country: null,
            MinRating: null, MaxRating: null, MinRuntime: null, MaxRuntime: null, Status: null,
            Sort: "popularity", Page: 1, PageSize: 20,
            Actor: null, Director: null, Keyword: null, Language: null, MinVotes: null,
            SeasonsFrom: null, SeasonsTo: null, ExcludeGenres: null, HasTrailer: null,
            HasProviders: null, RatingSource: null, ProviderRegion: "UA", Watchable: null,
            Category: null, AudioLang: null, MinQuality: null, OmitTotal: false);

        Assert.Equal(
            SearchController.BuildCacheKey(positional, "uk"),
            SearchController.BuildCacheKey(named, "uk"));

        // Інші sort/page → інший ключ (вигляди каталогу не перетинаються).
        Assert.NotEqual(
            SearchController.BuildCacheKey(positional, "uk"),
            SearchController.BuildCacheKey(positional with { Page = 2 }, "uk"));
        Assert.NotEqual(
            SearchController.BuildCacheKey(positional, "uk"),
            SearchController.BuildCacheKey(positional with { Sort = "rating" }, "uk"));
        // Мова входить у ключ: назви карток мовозалежні.
        Assert.NotEqual(
            SearchController.BuildCacheKey(positional, "uk"),
            SearchController.BuildCacheKey(positional, "en"));
    }

    [Fact]
    public void IsCacheable_OnlyDefaultCatalogViews()
    {
        Assert.True(SearchController.IsCacheable(null, null, null, null));
        Assert.True(SearchController.IsCacheable("  ", string.Empty, null, string.Empty));
        Assert.False(SearchController.IsCacheable("matrix", null, null, null));
        Assert.False(SearchController.IsCacheable(null, "DiCaprio", null, null));
        Assert.False(SearchController.IsCacheable(null, null, "Nolan", null));
        Assert.False(SearchController.IsCacheable(null, null, null, "heist"));
    }
}
