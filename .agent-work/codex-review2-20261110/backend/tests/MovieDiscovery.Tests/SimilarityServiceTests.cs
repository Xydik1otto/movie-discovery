using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Tests;

/// <summary>
/// FR-21: by-seeds фільтри (type/рік — hard, жанри — soft-boost), reasons-коди, парсинг GET-csv.
/// Сервіс гоняється на InMemory; SQL-план пулу/пресетів (масив-предикати) пінять NpgsqlTranslationTests.
/// </summary>
public class SimilarityServiceTests
{
    private static (MovieDiscoveryDbContext Db, SimilarityService Svc) Build(string dbName)
    {
        var db = TestDb.Create(dbName);
        return (db, new SimilarityService(db, new SimilarityEngine(), new TestDb.CacheStub(), Logs.Null<SimilarityService>()));
    }

    private static Title T(int id, string type = "movie", string[]? genres = null, int? year = null,
        double popularity = 10, string[]? actors = null) => new()
    {
        Id = id,
        TmdbId = id,
        Type = type,
        TitleEn = $"t{id}",
        Genres = genres ?? [],
        Actors = actors ?? [],
        Year = year,
        Popularity = popularity,
    };

    private static SimilarFilters F(int? from = null, int? to = null, string[]? types = null, string[]? genres = null) =>
        new() { YearFrom = from, YearTo = to, Types = types ?? [], Genres = genres ?? [] };

    [Fact]
    public async Task BySeeds_YearFilter_HardFilters_BeforeRanking()
    {
        var (db, svc) = Build($"similar-{Guid.NewGuid():N}");
        var seed = T(1, genres: ["drama"], year: 1994, popularity: 100);
        db.Titles.AddRange(
            seed,
            T(2, genres: ["drama"], year: 1996, popularity: 1),
            T(3, genres: ["drama"], year: 2005, popularity: 999));
        await db.SaveChangesAsync();

        // без фільтра — обидва кандидати в результаті
        var all = await svc.SimilarBySeedsAsync([seed], 20, "uk", filters: null);
        Assert.Equal(2, all.Count);

        // з фільтром 1990–1999 — 2005-й відсіканий ДО скорингу (hard-фільтр пулу)
        var filtered = await svc.SimilarBySeedsAsync([seed], 20, "uk", F(1990, 1999));
        Assert.Equal([2], filtered.Select(s => s.Title.Id).ToList());
    }

    [Fact]
    public async Task BySeeds_TypeFilter_HardFilters()
    {
        var (db, svc) = Build($"similar-{Guid.NewGuid():N}");
        var seed = T(1, genres: ["drama"], year: 1996, popularity: 100);
        db.Titles.AddRange(
            seed,
            T(2, type: "series", genres: ["drama"], year: 1996, popularity: 999),
            T(3, genres: ["drama"], year: 1996, popularity: 1));
        await db.SaveChangesAsync();

        var withTypes = await svc.SimilarBySeedsAsync([seed], 20, "uk", F(types: ["movie"]));
        Assert.Equal([3], withTypes.Select(s => s.Title.Id).ToList());

        // тип не переданий — серіал не відсіканий
        var noFilter = await svc.SimilarBySeedsAsync([seed], 20, "uk", null);
        Assert.Contains(noFilter, s => s.Title.Id == 2);
    }

    [Fact]
    public async Task BySeeds_GenreFilter_SoftBoost_NotCut()
    {
        var (db, svc) = Build($"similar-{Guid.NewGuid():N}");
        // однакова базова схожість (однакові Δyear/type/жанровий Жаккар); різниця лише popularity
        var seed = T(1, genres: ["drama"], year: 1996, popularity: 100);
        db.Titles.AddRange(
            seed,
            T(2, genres: ["drama", "comedy"], year: 1996, popularity: 1),     // матчить акцент
            T(3, genres: ["drama", "western"], year: 1996, popularity: 999)); // не матчить
        await db.SaveChangesAsync();

        // без акценту: popularity вирішує (3 перший)
        var plain = await svc.SimilarBySeedsAsync([seed], 20, "uk", null);
        Assert.Equal(3, plain[0].Title.Id);

        // з акцентом comedy: soft-boost підіймає 2 НАД більш популярний 3, але 3 НЕ відсіканий
        var boosted = await svc.SimilarBySeedsAsync([seed], 20, "uk", F(genres: ["comedy"]));
        Assert.Equal(2, boosted.Count);
        Assert.Equal(2, boosted[0].Title.Id);
        Assert.Equal(3, boosted[1].Title.Id);
        Assert.True(boosted[0].Score.Value > boosted[1].Score.Value);
    }

    [Fact]
    public async Task BySeeds_Reasons_Codes_SingleSeed()
    {
        var (db, svc) = Build($"similar-{Guid.NewGuid():N}");
        var seed = T(1, genres: ["drama"], year: 1994, popularity: 100, actors: ["Tom Hanks"]);
        db.Titles.AddRange(
            seed,
            T(2, genres: ["drama", "comedy"], year: 1996, popularity: 1, actors: ["Tom Hanks", "Robin Wright"]));
        await db.SaveChangesAsync();

        var top = (await svc.SimilarBySeedsAsync([seed], 20, "uk", null))[0];
        // композиція ідентична ToSimilarItem на контролері (FR-21: коди — фронт локалізує)
        var reasons = SimilarityReasons.Build(top.Score, top.Title.Year, top.SeedYear);

        Assert.Equal(1994, top.SeedYear); // рік еталона прокидається у ScoredTitle для коду декади
        Assert.Contains("shared_genres:drama", reasons);
        Assert.Contains("shared_actor:Tom Hanks", reasons);
        Assert.Equal("decade:1990s", reasons.Single(r => r.StartsWith("decade:", StringComparison.Ordinal)));
        Assert.True(reasons.Count(r => r.StartsWith("shared_genres:", StringComparison.Ordinal)) <= 3);
        Assert.True(reasons.Count(r => r.StartsWith("shared_actor:", StringComparison.Ordinal)) <= 2);
    }

    [Fact]
    public async Task BySeeds_Reasons_Codes_MultiSeed()
    {
        var (db, svc) = Build($"similar-{Guid.NewGuid():N}");
        db.Titles.AddRange(
            T(1, genres: ["drama"], year: 1993, popularity: 100),
            T(2, genres: ["comedy"], year: 1995, popularity: 100),
            T(3, genres: ["drama", "comedy"], year: 1996, popularity: 1));
        await db.SaveChangesAsync();
        var seeds = db.Titles.Where(t => t.Id == 1 || t.Id == 2).ToList();

        var top = (await svc.SimilarBySeedsAsync(seeds, 20, "uk", null))[0];
        var reasons = SimilarityReasons.Build(top.Score, top.Title.Year, top.SeedYear);

        // усереднений рік еталонів 1994 → декада 1990s; обидва жанри мульти-сіду присутні
        Assert.Equal(1994, top.SeedYear);
        Assert.Contains("shared_genres:drama", reasons);
        Assert.Contains("shared_genres:comedy", reasons);
        Assert.Equal("decade:1990s", reasons.Single(r => r.StartsWith("decade:", StringComparison.Ordinal)));
    }

    [Fact]
    public void SimilarFilters_Csv_Parsing_And_Normalization()
    {
        Assert.Empty(SimilarFilters.ParseCsv(null));
        Assert.Empty(SimilarFilters.ParseCsv("  "));
        Assert.Equal(["movie", "series"], SimilarFilters.ParseCsv(" movie , series ,, "));

        var filters = new SimilarFilters
        {
            Types = SimilarFilters.NormalizeTokens(SimilarFilters.ParseCsv(" Movie , Series ")),
            Genres = SimilarFilters.NormalizeGenres(SimilarFilters.ParseCsv(" комедія , ROMANCE , noir "),
                g => g switch { "комедія" => "comedy", "noir" => null, _ => g }),
        };
        Assert.Equal(["movie", "series"], filters.Types);
        // нормалізоване uk → en; невідоме (noir) лишається lowercase як є; дублікати зливаються
        Assert.Equal(["comedy", "romance", "noir"], filters.Genres);
        Assert.True(filters.HasPoolFilters);
        Assert.False(new SimilarFilters().HasPoolFilters); // жоден фільтр не обов'язковий
    }
}
