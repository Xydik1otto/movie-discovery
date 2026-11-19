using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Persistence;
using System.IO.Compression;
using System.Text;

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
    public void MergeSql_Corrects_Imdb_Only_Media_Types_And_Keeps_Localized_Name()
    {
        var sql = ImdbStaging.MergeSql;
        Assert.DoesNotContain("title_uk =", sql);           // uk-назви — sweet.tv/TMDB
        Assert.Contains("type = CASE WHEN titles.tmdb_id IS NULL AND titles.type IN ('movie', 'series')", sql);
        Assert.Contains("THEN EXCLUDED.type ELSE titles.type END", sql);
        Assert.Contains("title_en = EXCLUDED.title_en", sql);
        Assert.Contains("episode_runtime_min = CASE", sql);
        Assert.Contains("THEN COALESCE(titles.episode_runtime_min, EXCLUDED.episode_runtime_min)", sql);
        Assert.Contains("THEN EXCLUDED.genres ELSE titles.genres END", sql);
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
        Assert.Contains("CREATE TEMP TABLE imdb_raw", ImdbStaging.CreateTempTableSql);
        Assert.Contains("ON COMMIT DROP", ImdbStaging.CreateTempTableSql);
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

    [Theory]
    [InlineData("movie", "series", null, "series")]
    [InlineData("series", "movie", null, "movie")]
    [InlineData("movie", "series", 42, "movie")]
    [InlineData("anime", "series", null, "anime")]
    [InlineData("show", "series", null, "show")]
    [InlineData("manga", "series", null, "manga")]
    public async Task ApplyPatches_Reconciles_Type_By_Exact_Imdb_Id(
        string catalogType, string imdbType, int? tmdbId, string expectedType)
    {
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        var title = new Title
        {
            Type = catalogType, ImdbId = "tt0903747", TmdbId = tmdbId, TitleUk = "Пуститися берега",
            RuntimeMin = 95, EpisodeRuntimeMin = 50, Seasons = 5, Status = "ended",
        };
        db.Titles.Add(title);
        await db.SaveChangesAsync();
        var id = title.Id;

        var service = NewService(db);
        await service.ApplyPatchesAsync([Patch("tt0903747", "Breaking Bad", imdbType)]);

        var result = await db.Titles.SingleAsync();
        Assert.Equal(id, result.Id);
        Assert.Equal(expectedType, result.Type);
        Assert.Equal(tmdbId, result.TmdbId);
        Assert.Equal("Пуститися берега", result.TitleUk);
        if (expectedType != catalogType)
        {
            Assert.Null(result.Seasons);
            Assert.Null(result.Status);
            Assert.Equal(expectedType == "movie" ? 100 : (int?)null, result.RuntimeMin);
            Assert.Equal(expectedType == "series" ? 100 : (int?)null, result.EpisodeRuntimeMin);
        }
        else
        {
            Assert.Equal(95, result.RuntimeMin);
            Assert.Equal(50, result.EpisodeRuntimeMin);
        }
    }

    [Theory]
    [InlineData(null, null, null, 37)]
    [InlineData(null, null, 45, 45)]
    [InlineData(12, 53, 45, 53)]
    [InlineData(12, null, 45, 45)]
    public async Task ApplyPatches_Repairs_Legacy_Series_Runtime_And_Preserves_Tmdb_Runtime(
        int? tmdbId, int? currentRuntime, int? sourceRuntime, int expectedEpisodeRuntime)
    {
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Type = "series", ImdbId = "tt0903747", TmdbId = tmdbId,
            RuntimeMin = 37, EpisodeRuntimeMin = currentRuntime });
        await db.SaveChangesAsync();

        await NewService(db).ApplyPatchesAsync([Patch("tt0903747", "Breaking Bad", "series") with { RuntimeMin = sourceRuntime }]);

        var title = await db.Titles.SingleAsync();
        Assert.Equal(expectedEpisodeRuntime, title.EpisodeRuntimeMin);
        Assert.Equal(tmdbId is null ? (int?)null : 37, title.RuntimeMin);
    }

    [Fact]
    public async Task RunAsync_Records_Full_Sync_After_Inserting_And_Correcting_Titles()
    {
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        var existing = new Title { Type = "movie", ImdbId = "tt0903747", TitleUk = "Пуститися берега" };
        db.Titles.Add(existing);
        await db.SaveChangesAsync();

        var dir = Directory.CreateTempSubdirectory("movie-discovery-imdb-tests-");
        try
        {
            const string basics = "tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres\n"
                + "tt0903747\ttvSeries\tBreaking Bad\tBreaking Bad\t0\t2008\t2013\t49\tDrama\n"
                + "tt0114369\tmovie\tSe7en\tSe7en\t0\t1995\t\\N\t127\tThriller\n";
            const string ratings = "tconst\taverageRating\tnumVotes\ntt0903747\t9.5\t200\ntt0114369\t8.6\t100\n";
            using var http = StubHttp.Client(request => new(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Gzip(request.RequestUri!.AbsoluteUri == ImdbBulkService.BasicsUrl ? basics : ratings))
            });
            var service = new ImdbBulkService(http, db, new TestDb.CacheStub(),
                Options.Create(new ImdbOptions { CacheDir = dir.FullName }), Logs.Null<ImdbBulkService>());
            var interval = TimeSpan.FromDays(7);

            await service.RunAsync([], maxRows: 1);
            Assert.True(await service.IsFullSyncDueAsync(interval, default));

            var result = await service.RunAsync([], maxRows: null);
            Assert.Equal(new ImdbSyncResult(1, 1, 0), result);
            Assert.Equal(2, await db.Titles.CountAsync());
            Assert.Equal("series", (await db.Titles.FindAsync(existing.Id))!.Type);
            Assert.Equal(2, (await db.SyncStates.SingleAsync(s => s.Kind == ImdbBulkService.FullSyncKind)).LastCount);
            Assert.False(await service.IsFullSyncDueAsync(interval, default));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileCache_Preserves_Previous_Dataset_If_New_Download_Is_Incomplete()
    {
        var dir = Directory.CreateTempSubdirectory("movie-discovery-imdb-tests-");
        try
        {
            var path = Path.Combine(dir.FullName, "title.basics.tsv.gz");
            var prior = Gzip("previous dataset");
            await File.WriteAllBytesAsync(path, prior);

            using var http = StubHttp.Client(_ => new(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentLength = 4 } }
            });
            var cache = new TestDb.CacheStub();
            var files = new ImdbFileCache(http, cache, Logs.Null<ImdbBulkService>());

            await Assert.ThrowsAsync<IOException>(() => files.DownloadAsync("imdb", Path.GetFileName(path),
                ImdbBulkService.BasicsUrl, "file:basics", dir.FullName, 24, default));
            Assert.Equal(prior, await File.ReadAllBytesAsync(path));
            Assert.Single(dir.GetFiles());
            Assert.Empty(cache.Items);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static byte[] Gzip(string text)
    {
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return bytes.ToArray();
    }

    private static ImdbBulkService NewService(MovieDiscoveryDbContext db) =>
        new(StubHttp.Client(_ => null), db, new TestDb.CacheStub(),
            Options.Create(new ImdbOptions()), Logs.Null<ImdbBulkService>());

    private static ImdbTitlePatch Patch(
        string imdbId, string titleEn, string type = "movie", double rating = 8.0, string[]? genres = null) =>
        new(imdbId, type, titleEn, 2000, 100, genres ?? ["drama"], rating, 100_000,
            Math.Log10(100_001) * 10, false);
}
