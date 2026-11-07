using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Tests;

/// <summary>
/// Перевірка SQL-трансляції операцій над масивами (Npgsql EF 8). ToQueryString() не ходить у БД.
/// </summary>
public class NpgsqlTranslationTests
{
    private static MovieDiscoveryDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=translation_check", npg => npg.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    [Fact]
    public void Genres_Overlap_Translates()
    {
        using var db = CreateContext();
        var genres = new[] { "Drama", "Thriller" };
        var sql = db.Titles.Where(t => t.Genres.Any(g => genres.Contains(g))).ToQueryString();
        // overlap-семантика: INDEX/ANY або && — головне, що без зовнішніх викликів і з параметром-масивом
        Assert.Contains("genres", sql);
    }

    [Fact]
    public void Countries_Contains_Translates()
    {
        using var db = CreateContext();
        var sql = db.Titles.Where(t => t.Countries.Contains("UA")).ToQueryString();
        Assert.Contains("countries", sql);
    }

    [Fact]
    public void Actor_Ilike_Translates()
    {
        using var db = CreateContext();
        var sql = db.Titles.Where(t => t.Actors.Any(a => EF.Functions.ILike(a, "%Nolan%"))).ToQueryString();
        Assert.Contains("ILIKE", sql.ToUpperInvariant());
    }

    [Fact]
    public void ExcludeGenres_Translates()
    {
        using var db = CreateContext();
        var ex = new[] { "horror" };
        var sql = db.Titles.Where(t => !t.Genres.Any(g => ex.Contains(g))).ToQueryString();
        Assert.Contains("genres", sql);
    }

    [Fact]
    public void HasProviders_Exists_Translates()
    {
        using var db = CreateContext();
        var sql = db.Titles.Where(t => db.Providers.Any(p => p.TitleId == t.Id && p.Region == "UA")).ToQueryString();
        Assert.Contains("providers", sql);
    }

    [Fact]
    public void ImdbVotes_Filter_Translates()
    {
        using var db = CreateContext();
        var sql = db.Titles.Where(t => t.VotesImdb >= 10000).ToQueryString();
        Assert.Contains("votes_imdb", sql);
    }

    [Fact]
    public void Seasons_Range_Translates()
    {
        using var db = CreateContext();
        var sql = db.Titles.Where(t => t.Seasons >= 2 && t.Seasons <= 8).ToQueryString();
        Assert.Contains("seasons", sql);
    }

    // ── FR-21: план by-seeds — фільтри type/рік ДО сортування, рік range-умовами (BETWEEN-семантика) ──

    [Fact]
    public void BySeeds_YearAndType_Filters_Before_Sort_Translates()
    {
        using var db = CreateContext();
        var filters = new SimilarFilters { YearFrom = 1990, YearTo = 1999, Types = ["movie"] };
        var pool = db.Titles.AsNoTracking()
            .Where(t => t.Genres.Any(g => new[] { "drama" }.Contains(g)));
        pool = SimilarityService.ApplyCandidateFilters(pool, filters);
        var sql = pool.OrderByDescending(t => t.Popularity).Take(2000).ToQueryString();

        // діапазон року (еквівалент BETWEEN; індекс ix_titles_year) + тип через ANY
        Assert.Contains("year >=", sql);
        Assert.Contains("year <=", sql);
        Assert.Contains("ANY", sql.ToUpperInvariant());
        // hard-фільтри застосовані ДО сортування: WHERE … ORDER BY … LIMIT (індексний план, не post-фільтрація)
        Assert.True(
            sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase) >= 0
            && sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase) > sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase)
            && sql.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) > sql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase),
            $"Очікується WHERE → ORDER BY → LIMIT, отримано: {sql}");
    }

    [Fact]
    public void Preset_WatchLinks_Exists_Ordering_Translates()
    {
        using var db = CreateContext();
        var sql = CollectionsService.BuildPresetQuery(
                db.Titles.AsNoTracking(), db.WatchLinks,
                ["comedy", "romance"], minRating: 7.0, shortRuntime: false)
            .Take(12).ToQueryString();

        var upper = sql.ToUpperInvariant();
        // жанри через GIN-friendly overlap (genres &&), рейтинг через COALESCE
        Assert.Contains("GENRES", upper);
        Assert.Contains("COALESCE", upper);
        // пріоритет watch_links — корельований EXISTS-підзапит (індекс ix_watch_links_titleid)
        Assert.Contains("EXISTS", upper);
        Assert.Contains("WATCH_LINKS", upper);
        // пріоритети в ORDER BY до LIMIT
        Assert.True(
            upper.IndexOf("ORDER BY") > -1 && upper.IndexOf("LIMIT") > upper.IndexOf("ORDER BY"),
            $"Очікується ORDER BY перед LIMIT, отримано: {sql}");
    }
}
