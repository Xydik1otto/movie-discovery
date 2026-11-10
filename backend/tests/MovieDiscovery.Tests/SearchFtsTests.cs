using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// Двофазний текстовий пошук (Фаза A FTS → Фаза B ILIKE-фолбек) на InMemory-провайдері.
/// Гейт db.Database.IsNpgsql() зобов'язаний тримати InMemory (WAF-тести) на легаси-шляху:
/// InMemory НЕ транслює ані title_search_match (HasDbFunction), ані EF.Functions.ILike —
/// це ДОПРОМІЖНА межа провайдера (жоден доіснуючий тест не виконував непорожній q
/// на InMemory; q живуть лише у ToQueryString-перевірках трансляції). Тест фіксує,
/// що з непорожнім q InMemory падає саме на ILIKE client-eval, а НЕ на FTS-функцію —
/// тобто гейт спрацював і Фаза A не запускалась.
/// </summary>
public class SearchFtsTests
{
    private static MovieDiscoveryDbContext Create(string name) =>
        new(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static SearchQuery Q(string? q, bool omitTotal = false) =>
        new(q, null, [], null, null, null, null, null, null, null, null,
            "popularity", 1, 20, OmitTotal: omitTotal);

    private static async Task<MovieDiscoveryDbContext> SeedAsync()
    {
        var db = Create("fts_" + Guid.NewGuid().ToString("N"));
        db.Titles.Add(new Title { Type = "movie", TitleEn = "The Matrix", Popularity = 90, Year = 1999 });
        db.Titles.Add(new Title { Type = "movie", TitleEn = "Dune", Popularity = 80, Year = 2021 });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task InMemory_Q_GoesLegacyPath_IlikeClientEval_AsBefore()
    {
        // Гейт IsNpgsql(): Фаза A (title_search_match) НЕ будує запит — інакше InMemory
        // впав би на незнаний HasDbFunction. Падає на ДОПРОМІЖНОМУ обмеженні InMemory —
        // EF.Functions.ILike (client-eval), тобто поведінка ідентична до двофазного рефактору.
        var db = await SeedAsync();
        Assert.False(db.Database.IsNpgsql());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SearchService(db).SearchAsync(Q("matrix")));
        Assert.Contains("ILike", ex.Message);
        Assert.DoesNotContain("title_search_match", ex.Message);
        Assert.DoesNotContain("TitleSearchMatch", ex.Message);
    }

    [Fact]
    public async Task InMemory_UnusableQ_AlsoLegacyPath_AsBefore()
    {
        // "!!!" → FtsQueryBuilder.Build → null → Фаза A не запустилась би навіть на Npgsql;
        // на InMemory — той самий допроміжний ILIKE client-eval, як і до рефактору.
        var db = await SeedAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SearchService(db).SearchAsync(Q("!!!")));
        Assert.Contains("ILike", ex.Message);
    }

    [Fact]
    public async Task InMemory_NoQ_OmitTotal_ReturnsMinusOne_ItemsIntact()
    {
        // Без q (каталог/фільтри) ILIKE не з'являється — InMemory працює як раніше.
        var db = await SeedAsync();
        var r = await new SearchService(db).SearchAsync(Q(null, omitTotal: true));
        Assert.Equal(-1, r.Total);
        Assert.Equal(2, r.Items.Count);
        Assert.False(r.TotalCapped);
    }

    [Fact]
    public async Task InMemory_NoQ_CountIsReal_AndFiltersApply()
    {
        var db = await SeedAsync();
        var r = await new SearchService(db).SearchAsync(new SearchQuery(
            null, "movie", [], YearFrom: 2020, YearTo: null, Country: null,
            MinRating: null, MaxRating: null, MinRuntime: null, MaxRuntime: null,
            Status: null, Sort: "popularity", Page: 1, PageSize: 20));
        Assert.Equal(1, r.Total); // лише Dune (2021) проходить YearFrom=2020
        Assert.Equal("Dune", r.Items.Single().TitleEn);
    }

    [Fact]
    public void TitleSearchMatch_Stub_NeverRunsInMemory()
    {
        // Тіло стаба — тільки InvalidOperationException: EF перекладає виклик у SQL,
        // будь-який запуск у пам'яті означав би промах повз гейт IsNpgsql().
        Assert.Throws<InvalidOperationException>(() =>
            MovieDbFunctions.TitleSearchMatch("a", "b", "c", "d", ["e"], "a:*"));
    }
}
