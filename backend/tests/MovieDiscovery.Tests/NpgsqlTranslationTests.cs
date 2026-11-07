using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Infrastructure.Persistence;

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
}
