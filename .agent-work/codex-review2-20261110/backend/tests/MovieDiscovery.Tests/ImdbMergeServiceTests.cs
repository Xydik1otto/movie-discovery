using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Tests;

/// <summary>
/// Staging imdb_raw → MERGE у titles (FR-15): SQL-контракт + ідемпотентність злиття
/// на InMemory-провайдері (EF-шлях ImdbBulkService.ApplyPatchesAsync → ImdbUpsertCore).
/// </summary>
public class ImdbMergeServiceTests
{
    // ── SQL-контракт staging/MERGE (PostgreSQL-шлях) ──

    [Fact]
    public void MergeSql_Is_Idempotent_Upsert_By_ImdbId()
    {
        var sql = ImdbStaging.MergeSql;
        Assert.Contains("INSERT INTO titles", sql);
        Assert.Contains("FROM imdb_raw", sql);
        Assert.Contains("ON CONFLICT (imdb_id) WHERE imdb_id IS NOT NULL DO UPDATE SET", sql);
    }

    [Fact]
    public void MergeSql_Does_Not_Touch_TitleUk_Or_Type()
    {
        var sql = ImdbStaging.MergeSql;
        Assert.DoesNotContain("title_uk =", sql);           // uk-назви — sweet.tv/TMDB
        Assert.DoesNotContain("type = EXCLUDED.type", sql); // type наявних не змінюється
        Assert.Contains("title_en = EXCLUDED.title_en", sql);
        Assert.Contains("votes_imdb = COALESCE(EXCLUDED.votes_imdb, titles.votes_imdb)", sql);
    }

    [Fact]
    public void Staging_Columns_Match_Copy_And_Ddl()
    {
        Assert.Equal(
            new[] { "tconst", "title_type", "primary_title", "original_title", "is_adult", "start_year", "runtime_min", "genres", "average_rating", "num_votes", "popularity" },
            ImdbStaging.Columns);
        Assert.Equal(
            "COPY imdb_raw (tconst, title_type, primary_title, original_title, is_adult, start_year, runtime_min, genres, average_rating, num_votes, popularity) FROM STDIN (FORMAT BINARY)",
            ImdbStaging.CopyFromStdinSql());
        Assert.Contains("genres text[]", ImdbStaging.CreateTableSql);
        Assert.Contains("TRUNCATE TABLE imdb_raw", ImdbStaging.TruncateSql);
    }

    [Fact]
    public void ToStagingRow_Maps_Patch()
    {
        var patch = new ImdbTitlePatch("tt1", "movie", "Test", 2001, 100, ["action"], 7.5, 1000, 30, true);
        var row = ImdbBulkService.ToStagingRow(patch);

        Assert.Equal("tt1", row.Tconst);
        Assert.Equal("movie", row.TitleType);
        Assert.Equal("Test", row.PrimaryTitle);
        Assert.Equal("Test", row.OriginalTitle); // original_title дублює primary (окремої колонки в titles нема)
        Assert.True(row.IsAdult);
        Assert.Equal(["action"], row.Genres);
        Assert.Equal(7.5, row.AverageRating);
        Assert.Equal(1000, row.NumVotes);
        Assert.Equal(30, row.Popularity);
    }

    // ── EF-шлях злиття (InMemory): повторний sync НЕ дублює ──

    [Fact]
    public async Task ApplyPatches_Creates_Then_Updates_Without_Duplicates()
    {
        await using var db = TestDb.Create("imdb-merge-idempotent");
        var service = NewService(db);

        var batch1 = new List<ImdbTitlePatch>
        {
            Patch("tt0114369", "Se7en", rating: 8.6),
            Patch("tt0151804", "The Matrix", type: "movie"),
        };
        Assert.Equal(2, await service.ApplyPatchesAsync(batch1));

        var countAfterFirst = await db.Titles.CountAsync();
        Assert.Equal(2, countAfterFirst);

        // повторний sync: ті самі imdb_id, оновлений рейтинг/жанри
        var batch2 = new List<ImdbTitlePatch>
        {
            Patch("tt0114369", "Se7en", rating: 8.7, genres: ["crime", "drama", "mystery"]),
            Patch("tt0151804", "The Matrix"),
        };
        Assert.Equal(2, await service.ApplyPatchesAsync(batch2));

        Assert.Equal(2, await db.Titles.CountAsync()); // НЕ дублює
        var seven = await db.Titles.SingleAsync(t => t.ImdbId == "tt0114369");
        Assert.Equal(8.7, seven.RatingImdb ?? 0, 6);
        Assert.Equal(["crime", "drama", "mystery"], seven.Genres);
    }

    [Fact]
    public async Task ApplyPatches_Mixed_Types_And_Dupes_In_One_Batch()
    {
        await using var db = TestDb.Create("imdb-merge-mixed");
        var service = NewService(db);

        var patches = new List<ImdbTitlePatch>
        {
            Patch("tt0903747", "Breaking Bad", type: "series"),
            Patch("tt0903747", "Breaking Bad", type: "series"), // дублікат tconst у батчі
        };
        Assert.Equal(2, await service.ApplyPatchesAsync(patches));

        Assert.Equal(1, await db.Titles.CountAsync()); // злиття за imdb_id
        var t = await db.Titles.SingleAsync();
        Assert.Equal("series", t.Type);
        Assert.Equal("Breaking Bad", t.TitleEn);
    }

    [Fact]
    public async Task ApplyPatches_Does_Not_Wipe_Existing_Uk_Title()
    {
        await using var db = TestDb.Create("imdb-merge-uk");
        db.Titles.Add(new Title { Type = "movie", ImdbId = "tt35769", TitleEn = "U Are the Universe", TitleUk = "Ти — космос", Year = 2025 });
        await db.SaveChangesAsync();

        var service = NewService(db);
        await service.ApplyPatchesAsync([Patch("tt35769", "U Are the Universe")]);

        var t = await db.Titles.SingleAsync(x => x.ImdbId == "tt35769");
        Assert.Equal("Ти — космос", t.TitleUk);       // uk — sweet.tv, не IMDb
        Assert.Equal("U Are the Universe", t.TitleEn); // en оновлено з IMDb
    }

    private static ImdbBulkService NewService(MovieDiscoveryDbContext db) =>
        new(StubHttp.Client(_ => null), db, new TestDb.CacheStub(),
            Options.Create(new ImdbOptions()), Logs.Null<ImdbBulkService>());

    private static ImdbTitlePatch Patch(
        string imdbId, string titleEn, string type = "movie", double rating = 8.0, string[]? genres = null) =>
        new(imdbId, type, titleEn, 2000, 100, genres ?? ["drama"], rating, 100_000,
            Math.Log10(100_001) * 10, false);
}
