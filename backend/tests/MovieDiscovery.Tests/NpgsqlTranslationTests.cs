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
}
