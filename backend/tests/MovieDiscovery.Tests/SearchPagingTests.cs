using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// UX-01 (10.11): стабільна пагінація пошуку.
/// 1) Tie-breaker ThenBy(Id) в усіх гілках сортування — сусідні сторінки без дублів
///    і пропусків на рівних ключах, порядок відтворюваний між запусками.
/// 2) omitTotal — пропуск COUNT на перегортаннях (total = -1).
/// 3) Depth-cap — page кліпається до maxPage за total (або MaxPages, коли total невідомий).
/// InMemory-провайдер: чиста LINQ-семантика сортування без БД.
/// </summary>
public class SearchPagingTests
{
    private static MovieDiscoveryDbContext Create(string name) =>
        new(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static SearchQuery Q(
        string sort = "popularity",
        int page = 1,
        int pageSize = 20,
        bool omitTotal = false) =>
        new(null, null, [], null, null, null, null, null, null, null, null,
            sort, page, pageSize, OmitTotal: omitTotal);

    /// <summary>N тайтлів з ІДЕНТИЧНИМИ ключами сортування (всі popularity=7.5, рейтинг null).</summary>
    private static async Task<MovieDiscoveryDbContext> SeedTiesAsync(int n)
    {
        var db = Create("paging_" + Guid.NewGuid().ToString("N"));
        for (var i = 1; i <= n; i++)
        {
            db.Titles.Add(new Title
            {
                Type = "movie",
                TitleEn = $"Tie {i:D3}",
                Popularity = 7.5,
                Year = 2000,
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Sort_Popularity_TieBreaker_NoDuplicatesAcrossPages()
    {
        var db = await SeedTiesAsync(45); // 3 сторінки по 20: 20+20+5
        var svc = new SearchService(db);

        var p1 = await svc.SearchAsync(Q(page: 1));
        var p2 = await svc.SearchAsync(Q(page: 2));
        var p3 = await svc.SearchAsync(Q(page: 3));

        Assert.Equal(45, p1.Total);
        Assert.Equal(20, p1.Items.Count);
        Assert.Equal(20, p2.Items.Count);
        Assert.Equal(5, p3.Items.Count);

        var all = p1.Items.Concat(p2.Items).Concat(p3.Items).Select(t => t.Id).ToList();
        Assert.Equal(45, all.Distinct().Count()); // жодного дубля між сторінками
    }

    [Fact]
    public async Task Sort_IsDeterministic_AcrossRepeatedRuns_EverySortBranch()
    {
        var db = await SeedTiesAsync(45);
        var svc = new SearchService(db);

        foreach (var sort in new[] { "popularity", "rating", "imdb", "votes", "year", "relevance" })
        {
            var first = await svc.SearchAsync(Q(sort: sort, page: 1));
            var second = await svc.SearchAsync(Q(sort: sort, page: 1));
            var ids1 = first.Items.Select(t => t.Id).ToArray();
            var ids2 = second.Items.Select(t => t.Id).ToArray();
            Assert.Equal(ids1, ids2); // той самий запит → той самий порядок

            var page2 = await svc.SearchAsync(Q(sort: sort, page: 2));
            Assert.Empty(ids1.Intersect(page2.Items.Select(t => t.Id))); // сторінки не перетинаються
        }
    }

    [Fact]
    public async Task Sort_Rating_TieBreaker_IsAscendingId()
    {
        var db = await SeedTiesAsync(10); // рейтинги всі null → ключ 0 у всіх
        var r = await new SearchService(db).SearchAsync(Q(sort: "rating", pageSize: 10));
        var ids = r.Items.Select(t => t.Id).ToList();
        Assert.Equal(ids.OrderBy(x => x).ToList(), ids); // порядок = зростання Id
    }

    [Fact]
    public async Task OmitTotal_SkipsCount_ReturnsMinusOne_WithItemsIntact()
    {
        var db = await SeedTiesAsync(45);
        var r = await new SearchService(db).SearchAsync(Q(page: 2, omitTotal: true));
        Assert.Equal(-1, r.Total);
        Assert.False(r.TotalCapped);
        Assert.Equal(20, r.Items.Count); // самі items не постраждали
    }

    [Fact]
    public async Task WithoutOmitTotal_CountIsReal()
    {
        var db = await SeedTiesAsync(45);
        var r = await new SearchService(db).SearchAsync(Q(page: 2));
        Assert.Equal(45, r.Total);
    }

    [Fact]
    public async Task DepthCap_ClampsPageToMaxPageByTotal()
    {
        var db = await SeedTiesAsync(45); // maxPage = ceil(45/20) = 3
        var svc = new SearchService(db);

        var r = await svc.SearchAsync(Q(page: 99));
        Assert.Equal(3, r.Page);           // кліп до останньої реальної сторінки
        Assert.Equal(45, r.Total);
        Assert.Equal(5, r.Items.Count);    // хвіст, а не порожня відповідь
    }

    [Fact]
    public async Task DepthCap_UnknownTotal_ClampsToHardLimit()
    {
        var db = await SeedTiesAsync(45);
        var r = await new SearchService(db).SearchAsync(Q(page: 999_999, omitTotal: true));
        Assert.Equal(SearchService.MaxPages, r.Page);
        Assert.Empty(r.Items);
    }

    [Fact]
    public async Task EmptyResult_ClampsToPageOne_NoNegativeOffset()
    {
        var db = Create("paging_empty_" + Guid.NewGuid().ToString("N"));
        var r = await new SearchService(db).SearchAsync(Q(page: 5));
        Assert.Equal(0, r.Total);
        Assert.Equal(1, r.Page);
        Assert.Empty(r.Items);
    }
}
