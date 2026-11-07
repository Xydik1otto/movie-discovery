using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Tests;

/// <summary>GZ-фікстури TSV-датасетів (реальних HTTP-завантажень нема — стаб-хендлер віддає байти).</summary>
internal static class TsvGz
{
    public static byte[] Build(params string[] lines)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
            foreach (var line in lines)
                w.WriteLine(line);
        ms.Position = 0;
        return ms.ToArray();
    }

    public static HttpResponseMessage Response(byte[] bytes) =>
        new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}

/// <summary>Парсер title.akas.tsv.gz (FR-19): isOriginalTitle, \N, XWW-псевдокоди.</summary>
public class ImdbAkasParserTests
{
    [Fact]
    public void ParseRow_Original_Valid_Region()
    {
        var row = ImdbAkasParser.ParseRow("tt0114369\t1\tSeven\tUS\ten\tIMAX original\t\\N\t1\t");
        Assert.NotNull(row);
        Assert.Equal("tt0114369", row!.ImdbId);
        Assert.Equal("US", row.Region);
    }

    [Theory]
    [InlineData("tt0114369\t2\tSeven\tUS\ten\ttr.attribute\t\\N\t0\t")]  // не оригінальна назва
    [InlineData("tt0133093\t1\tThe Matrix\tXWW\ten\t\\N\t\\N\t1\t")]     // XWW-псевдокод
    [InlineData("tt0133093\t1\tThe Matrix\tXEU\tde\t\\N\t\\N\t1\t")]     // XEU-псевдокод
    [InlineData("tt0133093\t1\tThe Matrix\t\\N\ten\t\\N\t\\N\t1\t")]     // \N-регіон
    [InlineData("tt0133093\t1\tThe Matrix\tus\ten\t\\N\t\\N\t1\t")]      // lowercase — не ISO-2
    [InlineData("tt0133093\t1\tThe Matrix")]                              // обрізаний рядок
    [InlineData("")]
    [InlineData(null)]
    public void ParseRow_Skips_NonOriginal_BadRegion_Malformed(string? line) =>
        Assert.Null(ImdbAkasParser.ParseRow(line));

    [Fact]
    public void IsValidRegion_Only_Iso2_Uppercase()
    {
        Assert.True(ImdbAkasParser.IsValidRegion("UA"));
        Assert.True(ImdbAkasParser.IsValidRegion("KR"));
        Assert.False(ImdbAkasParser.IsValidRegion("XWW"));
        Assert.False(ImdbAkasParser.IsValidRegion("\\N"));
        Assert.False(ImdbAkasParser.IsValidRegion("us"));
        Assert.False(ImdbAkasParser.IsValidRegion(""));
        Assert.False(ImdbAkasParser.IsValidRegion(null));
    }
}

/// <summary>Staging SQL imdb_akas_raw (FR-19): ключові clauses міграції/сервісу.</summary>
public class ImdbAkasStagingTests
{
    [Fact]
    public void UpdateSql_Targets_Titles_By_ImdbId_Idempotent()
    {
        var sql = ImdbAkasStaging.UpdateSql;
        Assert.Contains("UPDATE titles", sql);
        Assert.Contains("FROM imdb_akas_raw", sql);
        Assert.Contains("t.imdb_id = s.imdb_id", sql);
        Assert.Contains("t.country IS NULL OR t.country <> s.region", sql); // лише фактичні зміни
    }

    [Fact]
    public void CreateTableSql_Matches_Copy_Columns()
    {
        Assert.Contains("imdb_akas_raw", ImdbAkasStaging.CreateTableSql);
        Assert.Equal(["imdb_id", "region"], ImdbAkasStaging.Columns);
        Assert.Contains("COPY imdb_akas_raw (imdb_id, region) FROM STDIN", ImdbAkasStaging.CopyFromStdinSql());
    }
}

/// <summary>EF-гілка akas-синку (InMemory): стрім gz-фікстури → titles.country, ідемпотентність.</summary>
public class ImdbAkasServiceTests
{
    private const string Header = "titleId\tordering\ttitle\tregion\tlanguage\ttypes\tattributes\tisOriginalTitle\tcomment";

    private static byte[] AkasGz() => TsvGz.Build(
        Header,
        "tt0114369\t1\tSeven\tUS\ten\tIMAX original\t\\N\t1\t",
        "tt0114369\t2\tSeven\tUS\ten\ttr.attribute\t\\N\t0\t",   // не оригінальна — SKIP
        "tt0133093\t1\tThe Matrix\tXWW\ten\t\\N\t\\N\t1\t",       // XWW — SKIP
        "tt0903747\t1\tBreaking Bad\tUA\tuk\t\\N\t\\N\t1\t",      // ок
        "tt0133093\t2\tThe Matrix\t\\N\ten\t\\N\t1\t\\N\t");      // \N-регіон — SKIP

    private static (ImdbAkasService Svc, MovieDiscoveryDbContext Db, TestDb.CacheStub Cache) Make(
        List<string> received, int maxRows = 0)
    {
        var db = TestDb.Create("akas_" + Guid.NewGuid().ToString("N"));
        var cache = new TestDb.CacheStub();
        var opts = Options.Create(new ImdbAkasOptions
        {
            CacheDir = Path.Combine(Path.GetTempPath(), "md-test-akas-" + Guid.NewGuid().ToString("N")),
            BatchSize = 2, // сило-батчинг, щоб перевірити багатобатчевий шлях
        });
        var handler = new StubHttpHandler(req =>
        {
            received.Add(req.RequestUri?.ToString() ?? "");
            return req.RequestUri?.ToString().Contains("title.akas.tsv.gz") == true
                ? TsvGz.Response(AkasGz())
                : null;
        });
        var svc = new ImdbAkasService(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") },
            db, cache, opts, Logs.Null<ImdbAkasService>());
        return (svc, db, cache);
    }

    private static async Task<(MovieDiscoveryDbContext Db, ImdbAkasService Svc, TestDb.CacheStub Cache)> SeedAsync(
        List<string> received)
    {
        var (svc, db, cache) = Make(received);
        db.Titles.AddRange(
            new Title { Type = "movie", TitleEn = "Se7en", ImdbId = "tt0114369" },
            new Title { Type = "series", TitleEn = "Breaking Bad", ImdbId = "tt0903747" },
            new Title { Type = "movie", TitleEn = "The Matrix", ImdbId = "tt0133093" },   // лише XWW/\N → country лишається NULL
            new Title { Type = "movie", TitleEn = "Sweet", TitleUk = "Солодкий" });        // без imdb_id — не чіпається
        await db.SaveChangesAsync();
        return (db, svc, cache);
    }

    [Fact]
    public async Task RunAsync_Fills_Country_From_Original_Akas()
    {
        var received = new List<string>();
        var (db, svc, _) = await SeedAsync(received);

        var result = await svc.RunAsync(maxRows: null);

        Assert.Equal(2, result.Staged); // лише валідні original+ISO-2 рядки
        Assert.Equal(2, result.Updated);

        var rows = await db.Titles.OrderBy(t => t.ImdbId!).ToListAsync();
        Assert.Equal("US", rows.Single(t => t.ImdbId == "tt0114369").Country);
        Assert.Equal("UA", rows.Single(t => t.ImdbId == "tt0903747").Country);
        Assert.Null(rows.Single(t => t.ImdbId == "tt0133093").Country);   // XWW/\N відкинуті
        Assert.Null(rows.Single(t => t.ImdbId == null).Country);

        // sync_state source=imdb-akas
        var state = await db.SyncStates.SingleAsync(s => s.Source == ImdbAkasService.Source);
        Assert.Equal(2, state.LastCount);

        Assert.Single(received); // одне завантаження
    }

    [Fact]
    public async Task Second_Run_Is_Idempotent_No_Rewrites()
    {
        var received = new List<string>();
        var (db, svc, _) = await SeedAsync(received);

        await svc.RunAsync(null);
        var second = await svc.RunAsync(null); // той самий (кешований) файл

        Assert.Equal(2, second.Staged);
        Assert.Equal(0, second.Updated); // country вже така сама — нуль змін

        var rows = await db.Titles.OrderBy(t => t.ImdbId!).ToListAsync();
        Assert.Equal("US", rows.Single(t => t.ImdbId == "tt0114369").Country);
        Assert.Equal("UA", rows.Single(t => t.ImdbId == "tt0903747").Country);
    }

    [Fact]
    public async Task MaxRows_Limits_Staged_Rows()
    {
        var received = new List<string>();
        var (db, svc, _) = await SeedAsync(received);

        var result = await svc.RunAsync(maxRows: 1);

        Assert.Equal(1, result.Staged);
        Assert.Equal(1, result.Updated); // лише tt0114369 (перший валідний)
        var rows = await db.Titles.OrderBy(t => t.ImdbId!).ToListAsync();
        Assert.Equal("US", rows.Single(t => t.ImdbId == "tt0114369").Country);
        Assert.Null(rows.Single(t => t.ImdbId == "tt0903747").Country);
    }

    [Fact]
    public async Task Cached_File_Skips_Download()
    {
        var received = new List<string>();
        var (db, svc, cache) = await SeedAsync(received);

        await svc.RunAsync(null);
        Assert.Single(received);

        // другий прогін: мету кешу записано, файл на місці → без HTTP
        await svc.RunAsync(null);
        Assert.Single(received);
        Assert.Contains(cache.Items, kv => kv.Key.StartsWith($"{ImdbAkasService.Source}|file:"));
    }
}

/// <summary>Парсери crew/principals/name.basics + SelectCast (категорія-фільтр, перші ~8 по ordering).</summary>
public class ImdbCrewParserTests
{
    [Fact]
    public void ParseCrew_Valid_And_NullDirectors()
    {
        var row = ImdbCrewParser.ParseCrew("tt0114369\tnm0000093,nm0000093,nm0005690\t\\N");
        Assert.NotNull(row);
        Assert.Equal("tt0114369", row!.Tconst);
        Assert.Equal(["nm0000093", "nm0005690"], row.Directors); // дублікат прибрано

        var none = ImdbCrewParser.ParseCrew("tt0903747\t\\N\t\\N");
        Assert.NotNull(none);
        Assert.Empty(none!.Directors);

        Assert.Null(ImdbCrewParser.ParseCrew(null));
        Assert.Null(ImdbCrewParser.ParseCrew("\\N\t\\N\t\\N"));
    }

    [Fact]
    public void ParsePrincipal_Valid_And_Malformed()
    {
        var row = ImdbCrewParser.ParsePrincipal("tt0114369\t1\tnm0000138\tactor\t\\N\t[\"Brad Pitt\"]");
        Assert.NotNull(row);
        Assert.Equal("tt0114369", row!.Tconst);
        Assert.Equal(1, row.Ordering);
        Assert.Equal("nm0000138", row.Nconst);
        Assert.Equal("actor", row.Category);

        Assert.Null(ImdbCrewParser.ParsePrincipal("tt0114369\t\\N\tnm0000138\tactor\t\\N\t[]"));  // ordering \N
        Assert.Null(ImdbCrewParser.ParsePrincipal("tt0114369\t1\tnmX\tactor\t\\N\t[]"));          // кривий nconst
        Assert.Null(ImdbCrewParser.ParsePrincipal("tt0114369\t1\tnm0000138\t\\N\t\\N\t[]"));      // категорія \N
        Assert.Null(ImdbCrewParser.ParsePrincipal("tt0114369\t1\tnm0000138"));                    // обрізаний
    }

    [Fact]
    public void IsCastCategory_Only_Actor_Actress_Self()
    {
        Assert.True(ImdbCrewParser.IsCastCategory("actor"));
        Assert.True(ImdbCrewParser.IsCastCategory("actress"));
        Assert.True(ImdbCrewParser.IsCastCategory("self"));
        Assert.False(ImdbCrewParser.IsCastCategory("director"));
        Assert.False(ImdbCrewParser.IsCastCategory("writer"));
        Assert.False(ImdbCrewParser.IsCastCategory("producer"));
        Assert.False(ImdbCrewParser.IsCastCategory("composer"));
    }

    [Fact]
    public void SelectCast_Takes_First_Eight_By_Ordering_Filters_Categories()
    {
        var rows = new List<ImdbPrincipalRow>
        {
            new("tt1", 1, "nm1", "self"),
            new("tt1", 2, "nm2", "director"),   // не cast
            new("tt1", 3, "nm3", "actor"),
            new("tt1", 4, "nm1", "actress"),    // дублікат nconst
            new("tt1", 5, "nm5", "actor"),
            new("tt1", 6, "nm6", "actor"),
            new("tt1", 7, "nm7", "actor"),
            new("tt1", 8, "nm8", "actor"),
            new("tt1", 9, "nm9", "actor"),
            new("tt1", 10, "nm10", "actor"),    // за межею 8-и
        };

        var cast = ImdbCrewParser.SelectCast(rows, max: 8);

        Assert.Equal(["nm1", "nm3", "nm5", "nm6", "nm7", "nm8", "nm9"], cast); // ordering-порядок, без дублікатів, ≤8
    }

    [Fact]
    public void ParseName_Valid_And_Null()
    {
        var row = ImdbCrewParser.ParseName("nm0000138\tBrad Pitt\t1963\t\\N\tactor\ttt0114369");
        Assert.NotNull(row);
        Assert.Equal("nm0000138", row!.Nconst);
        Assert.Equal("Brad Pitt", row.PrimaryName);

        Assert.Null(ImdbCrewParser.ParseName("nm0000138\t\\N\t1963\t\\N\tactor\ttt0114369"));
        Assert.Null(ImdbCrewParser.ParseName("nmX\tBrad\t1963\t\\N\tactor\ttt0114369"));
        Assert.Null(ImdbCrewParser.ParseName(null));
    }
}

/// <summary>Ідемпотентне злиття crew-записів (InMemory-логіка): guard-и і повторюваність.</summary>
public class ImdbCrewMergeCoreTests
{
    private static ImdbCrewRecord Record(string type = "movie", string[]? directors = null, string[]? actors = null) =>
        new("tt0114369", type, directors ?? ["David Fincher"], actors ?? ["Brad Pitt"]);

    [Fact]
    public void Apply_Fills_Directors_And_Actors()
    {
        var title = new Title { Id = 1, Type = "movie", TitleEn = "Se7en" };
        Assert.True(ImdbCrewMergeCore.Apply(title, Record()));
        Assert.Equal(["David Fincher"], title.Directors);
        Assert.Equal(["Brad Pitt"], title.Actors);
    }

    [Fact]
    public void Apply_Twice_Is_Stable_Not_Concatenated()
    {
        var title = new Title { Id = 1, Type = "movie", TitleEn = "Se7en" };
        ImdbCrewMergeCore.Apply(title, Record());
        var d1 = title.Directors; var a1 = title.Actors;
        ImdbCrewMergeCore.Apply(title, Record());
        Assert.Equal(d1, title.Directors);
        Assert.Equal(a1, title.Actors);
    }

    [Theory]
    [InlineData("anime")]
    [InlineData("manga")]
    [InlineData("show")]
    public void Apply_Skips_Non_Movie_Series_Types(string type)
    {
        var title = new Title { Id = 1, Type = type, TitleEn = "X" };
        Assert.False(ImdbCrewMergeCore.CanApply(title, Record(type: "movie")));
    }

    [Fact]
    public void Apply_Skips_Type_Mismatch()
    {
        var title = new Title { Id = 1, Type = "series", TitleEn = "X" };
        Assert.False(ImdbCrewMergeCore.CanApply(title, Record(type: "movie")));
    }

    [Fact]
    public void Apply_Skips_Uk_Source_Titles() // sweet.tv/TMDB-укр — не чіпати
    {
        var title = new Title { Id = 1, Type = "movie", TitleEn = "Se7en", TitleUk = "Сім" };
        Assert.False(ImdbCrewMergeCore.CanApply(title, Record()));
    }

    [Fact]
    public void Apply_Skips_Empty_TitleEn() // sweettv-рядки (title_en='')
    {
        var title = new Title { Id = 1, Type = "movie", TitleEn = "" };
        Assert.False(ImdbCrewMergeCore.CanApply(title, Record()));
    }

    [Fact]
    public void Apply_Partial_Record_Keeps_Other_Side()
    {
        var title = new Title { Id = 1, Type = "movie", TitleEn = "X", Directors = ["Old Director"] };
        ImdbCrewMergeCore.Apply(title, Record(directors: [], actors: ["Someone"]));
        Assert.Equal(["Old Director"], title.Directors); // порожня сторона не затирає
        Assert.Equal(["Someone"], title.Actors);
    }
}

/// <summary>EF-гілка crew-синку (InMemory): стрім 4 gz-фікстур → titles.directors/actors іменами.</summary>
public class ImdbCrewServiceTests
{
    private const string BasicsHeader = "tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\truntimeMinutes\tgenres";
    private const string CrewHeader = "tconst\tdirectors\twriters";
    private const string PrincipalsHeader = "tconst\tordering\tnconst\tcategory\tjob\tcharacters";
    private const string NamesHeader = "nconst\tprimaryName\tbirthYear\tdeathYear\tprimaryProfession\tknownForTitles";

    private static byte[] BasicsGz() => TsvGz.Build(
        BasicsHeader,
        "tt0114369\tmovie\tSe7en\tSeven\t0\t1995\t\\N\t127\tCrime,Drama,Mystery",
        "tt0903747\ttvSeries\tBreaking Bad\t\\N\t0\t2008\t\\N\t\\N\tCrime,Drama,Thriller",
        "tt9335494\tmovie\tDemon Slayer\t\\N\t0\t2019\t\\N\t106\tAnimation");

    private static byte[] CrewGz() => TsvGz.Build(
        CrewHeader,
        "tt0114369\tnm0000093,nm0000093\t\\N",
        "tt0903747\t\\N\t\\N",           // без режисерів — лише principals
        "tt1234567\tnm0000001\t\\N",     // uk-тайтл — guard
        "tt7654321\tnm0000002\t\\N");    // sweettv (title_en='') — guard

    private static byte[] PrincipalsGz() => TsvGz.Build(
        PrincipalsHeader,
        "tt0114369\t1\tnm0000138\tactor\t\\N\t[\"Brad Pitt\"]",
        "tt0114369\t2\tnm0000126\tactor\t\\N\t[\"Kevin Spacey\"]",
        "tt0114369\t3\tnm0000134\tactress\t\\N\t[\"Gwyneth Paltrow\"]",
        "tt0114369\t0\tnm0000093\tdirector\t\\N\t\\N",   // не cast — SKIP
        "tt0903747\t1\tnm0180049\tactor\t\\N\t[\"Walter White\"]",
        "tt0903747\t2\tnm0634032\tactress\t\\N\t[\"Skyler White\"]",
        "tt1234567\t1\tnm0000001\tactor\t\\N\t[]",
        "tt7654321\t1\tnm0000002\tactor\t\\N\t[]");

    private static byte[] NamesGz() => TsvGz.Build(
        NamesHeader,
        "nm0000093\tDavid Fincher\t1962\t\\N\tdirector\ttt0114369",
        "nm0000138\tBrad Pitt\t1963\t\\N\tactor\ttt0114369",
        "nm0000126\tKevin Spacey\t1959\t\\N\tactor\ttt0114369",
        "nm0000134\tGwyneth Paltrow\t1972\t\\N\tactress\ttt0114369",
        "nm0180049\tBryan Cranston\t1956\t\\N\tactor\ttt0903747",
        "nm0634032\tAnna Gunn\t1968\t\\N\tactress\ttt0903747",
        "nm0000001\tBlocked Uk Name\t1900\t\\N\tactor\ttt1234567",
        "nm0000002\tBlocked Sweet Name\t1900\t\\N\tactor\ttt7654321");

    private static ImdbCrewService Make(MovieDiscoveryDbContext db, List<string> received, int maxRows = 0)
    {
        var cache = new TestDb.CacheStub();
        var opts = Options.Create(new ImdbCrewOptions
        {
            CacheDir = Path.Combine(Path.GetTempPath(), "md-test-crew-" + Guid.NewGuid().ToString("N")),
            BatchSize = 1, // примусово багатобатчевий шлях
        });
        var handler = new StubHttpHandler(req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            received.Add(url);
            return url switch
            {
                var u when u.Contains("title.basics") => TsvGz.Response(BasicsGz()),
                var u when u.Contains("title.crew") => TsvGz.Response(CrewGz()),
                var u when u.Contains("title.principals") => TsvGz.Response(PrincipalsGz()),
                var u when u.Contains("name.basics") => TsvGz.Response(NamesGz()),
                _ => null,
            };
        });
        return new ImdbCrewService(
            new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") },
            db, cache, opts, Logs.Null<ImdbCrewService>());
    }

    private static async Task<MovieDiscoveryDbContext> SeedAsync()
    {
        var db = TestDb.Create("crew_" + Guid.NewGuid().ToString("N"));
        db.Titles.AddRange(
            new Title { Type = "movie", TitleEn = "Se7en", ImdbId = "tt0114369" },
            new Title { Type = "series", TitleEn = "Breaking Bad", ImdbId = "tt0903747" },
            new Title { Type = "anime", TitleEn = "Demon Slayer", ImdbId = "tt9335494", Genres = ["animation"] },
            new Title { Type = "movie", TitleEn = "Sweet", TitleUk = "Солодкий", ImdbId = "tt1234567" },
            new Title { Type = "movie", TitleEn = "", TitleUk = "Без англ назви", ImdbId = "tt7654321" });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task RunAsync_Fills_Directors_And_Actors_With_Names()
    {
        var received = new List<string>();
        var db = await SeedAsync();
        var svc = Make(db, received);

        var result = await svc.RunAsync(null);

        var se7en = await db.Titles.SingleAsync(t => t.ImdbId == "tt0114369");
        Assert.Equal(["David Fincher"], se7en.Directors); // дублікат nm0000093 прибрано
        Assert.Equal(["Brad Pitt", "Kevin Spacey", "Gwyneth Paltrow"], se7en.Actors);

        var bb = await db.Titles.SingleAsync(t => t.ImdbId == "tt0903747");
        Assert.Null(bb.Directors); // crew \N → не чіпається
        Assert.Equal(["Bryan Cranston", "Anna Gunn"], bb.Actors);

        var anime = await db.Titles.SingleAsync(t => t.ImdbId == "tt9335494");
        Assert.Null(anime.Directors); // anime/manga — guard
        Assert.Empty(anime.Actors);

        var uk = await db.Titles.SingleAsync(t => t.ImdbId == "tt1234567");
        Assert.Null(uk.Directors); // title_uk (uk-джерело) — guard
        Assert.Empty(uk.Actors);

        var sweet = await db.Titles.SingleAsync(t => t.ImdbId == "tt7654321");
        Assert.Null(sweet.Directors); // title_en='' (sweettv) — guard
        Assert.Empty(sweet.Actors);

        // sync_state source=imdb-crew kind=movie|series
        var states = await db.SyncStates.Where(s => s.Source == ImdbCrewService.Source).ToListAsync();
        Assert.Contains(states, s => s.Kind == "movie");
        Assert.Contains(states, s => s.Kind == "series");

        // 4 завантаження (basics + crew + principals + names)
        Assert.Equal(4, received.Count);
    }

    [Fact]
    public async Task Second_Run_Is_Idempotent()
    {
        var received = new List<string>();
        var db = await SeedAsync();
        var svc = Make(db, received);

        await svc.RunAsync(null);
        var before = (await db.Titles.OrderBy(t => t.ImdbId!).ToListAsync())
            .Select(t => (t.ImdbId, D: string.Join('|', t.Directors ?? []), A: string.Join('|', t.Actors)))
            .ToList();

        await svc.RunAsync(null);
        var after = (await db.Titles.OrderBy(t => t.ImdbId!).ToListAsync())
            .Select(t => (t.ImdbId, D: string.Join('|', t.Directors ?? []), A: string.Join('|', t.Actors)))
            .ToList();

        Assert.Equal(before, after); // ті самі масиви — не конкатенація, не дублювання
    }

    [Fact]
    public async Task MaxRows_Limits_Records()
    {
        var received = new List<string>();
        var db = await SeedAsync();
        var svc = Make(db, received);

        var result = await svc.RunAsync(maxRows: 1);

        var se7en = await db.Titles.SingleAsync(t => t.ImdbId == "tt0114369");
        Assert.Equal(["David Fincher"], se7en.Directors);
        var bb = await db.Titles.SingleAsync(t => t.ImdbId == "tt0903747");
        Assert.Null(bb.Directors);
        Assert.Empty(bb.Actors); // maxRows=1 обрізав crew-пас; principals-пас теж обрізаний
    }

    [Fact]
    public async Task Staging_Sql_Has_Guards_And_Resolution()
    {
        var sql = ImdbCrewStaging.MergeSql;
        Assert.Contains("UPDATE titles", sql);
        Assert.Contains("imdb_names_raw", sql);        // резолв імен у БД
        Assert.Contains("t.type IN ('movie', 'series')", sql);
        Assert.Contains("t.title_en <> ''", sql);      // sweettv-рядки не чіпаються
        Assert.Contains("t.title_uk IS NULL OR t.title_uk = ''", sql); // uk-джерела не чіпаються
        Assert.Contains("IS DISTINCT FROM", sql);      // ідемпотентність — лише фактичні зміни
        Assert.Contains("COALESCE", sql);              // відсутність імен не затирає наявне
        Assert.Equal(["imdb_id", "directors"], ImdbCrewStaging.CrewColumns);
        Assert.Equal(["imdb_id", "actors"], ImdbCrewStaging.PrincipalsColumns);
        Assert.Equal(["nconst", "primary_name"], ImdbCrewStaging.NamesColumns);
    }
}
