using MovieDiscovery.Infrastructure.Persistence;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Wikipedia;

namespace MovieDiscovery.Tests;

/// <summary>
/// Тести UX-02: чистка фейкових постерів (ytimg / missing_original), QID-first SPARQL-ланцюг
/// (P345 → QID + РОЗДІЛЕНІ sitelink-триплети), парсер MediaWiki pageimages, fill-only UPDATE
/// staging wikipedia_posters_raw, ідемпотентність.
/// </summary>
public class WikipediaPosterTests
{
    private static List<WikipediaSparqlRow> ParseSparqlCsv(string csv, int? maxRows = null) =>
        WikipediaPosterService.ParseSparqlCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)), maxRows);

    // ── QID-first SPARQL-ланцюг ──

    [Fact]
    public void SparqlValues_Uses_P345_Qid_And_Separate_Sitelink_Optionals()
    {
        var q = WikipediaPosterService.SparqlValues(["tt0468569", "tt0114369"]);

        // VALUES-батч imdb_id + ідентичність через P345, QID витягується з IRI елемента
        Assert.Contains("VALUES ?imdb { \"tt0468569\" \"tt0114369\" }", q);
        Assert.Contains("?item wdt:P345 ?imdb .", q);
        Assert.Contains("BIND(STRAFTER(STR(?item), \"/entity/\") AS ?qid)", q);

        // sitelinks uk/ru/en — РОЗДІЛЕНІ OPTIONAL-блоки з окремими триплетами
        // (';'-ланцюг schema:about;schema:isPartOf повертає порожні sitelinks — перевірено живцем)
        foreach (var lang in new[] { "en", "uk", "ru" })
        {
            Assert.Contains(
                $"OPTIONAL {{\n    ?{lang}page schema:about ?item .\n" +
                $"    ?{lang}page schema:isPartOf <https://{lang}.wikipedia.org/> .\n" +
                $"    ?{lang}page schema:name ?{lang} .\n  }}", q);
        }
        Assert.DoesNotContain("schema:about ?item ;", q); // жодних ';'-ланцюгів
        Assert.StartsWith("SELECT ?imdb ?qid ?en ?uk ?ru WHERE {", q); // порядок = колонки CSV
    }

    [Fact]
    public void SparqlValues_Escapes_Quotes_In_Ids()
    {
        var evil = WikipediaPosterService.SparqlValues(["tt0\"0"]);
        Assert.DoesNotContain("tt0\"0", evil);
        Assert.Contains("tt00", evil);
    }

    [Fact]
    public void Batch_Splits_Evenly_With_Tail()
    {
        var ids = Enumerable.Range(1, 10).Select(i => $"tt{i:0000000}").ToList();
        var batches = WikipediaPosterService.Batch(ids, 4).ToList();
        Assert.Equal(3, batches.Count);
        Assert.Equal(4, batches[0].Count);
        Assert.Equal(4, batches[1].Count);
        Assert.Equal(2, batches[2].Count);
        Assert.Empty(WikipediaPosterService.Batch([], 4));
    }

    [Fact]
    public void Sitelink_Returns_Per_Lang_Title()
    {
        var row = new WikipediaSparqlRow("tt0468569", "Q123443", "The Dark Knight", null, "Тёмный рыцарь");
        Assert.Equal("The Dark Knight", WikipediaPosterService.Sitelink(row, "en"));
        Assert.Null(WikipediaPosterService.Sitelink(row, "uk"));
        Assert.Equal("Тёмный рыцарь", WikipediaPosterService.Sitelink(row, "ru"));
        Assert.Null(WikipediaPosterService.Sitelink(row, "de"));
    }

    // ── Парсер SPARQL CSV (imdb,qid,en,uk,ru) ──

    [Fact]
    public void ParseSparqlCsv_Simple_Row()
    {
        var csv = "imdb,qid,en,uk,ru\n" +
                  "tt0468569,Q123443,The Dark Knight,Темний лицар,Тёмный рыцарь";
        var row = Assert.Single(ParseSparqlCsv(csv));
        Assert.Equal("tt0468569", row.ImdbId);
        Assert.Equal("Q123443", row.Qid);
        Assert.Equal("The Dark Knight", row.En);
        Assert.Equal("Темний лицар", row.Uk);
        Assert.Equal("Тёмный рыцарь", row.Ru);
    }

    [Fact]
    public void ParseSparqlCsv_Skips_Rows_Without_Qid_Or_Any_Sitelink()
    {
        var csv = "imdb,qid,en,uk,ru\n" +
                  "tt1,,Title,,\n" +        // без QID — немає перевірної ідентичності
                  "tt2,Q2,,,\n" +           // елемент є, sitelink-ів немає — постера не буде
                  "tt3,Q3,,Титул,";
        var rows = ParseSparqlCsv(csv);
        var row = Assert.Single(rows);
        Assert.Equal("tt3", row.ImdbId);
        Assert.Equal("Титул", row.Uk);
        Assert.Null(row.En);
    }

    [Fact]
    public void ParseSparqlCsv_Quoted_Comma_And_MaxRows_Cap()
    {
        var csv = "imdb,qid,en,uk,ru\n" +
                  "tt1,Q1,\"Bat, man\",,\n" +
                  "tt2,Q2,,Титул,";
        var rows = ParseSparqlCsv(csv, maxRows: 1);
        var row = Assert.Single(rows);
        Assert.Equal("Bat, man", row.En); // quoted-кома не ріже поле
        Assert.Equal("tt1", row.ImdbId);
    }

    [Fact]
    public void ParseSparqlCsv_Empty_Or_Header_Only_Or_No_Imdb_Column()
    {
        Assert.Empty(ParseSparqlCsv(""));
        Assert.Empty(ParseSparqlCsv("imdb,qid,en,uk,ru\n"));
        Assert.Empty(ParseSparqlCsv("a,b\n1,2")); // немає колонки imdb → порожньо, без винятку
    }

    // ── Парсер MediaWiki pageimages ──

    private const string PageImagesJson = """
        {
          "batchcomplete": true,
          "query": {
            "normalized": [ { "from": "темний лицар", "to": "Темний лицар" } ],
            "redirects": [ { "from": "Темний лицар", "to": "Темний лицар (фільм)" } ],
            "pages": [
              {
                "title": "Темний лицар (фільм)",
                "pageimage": "DarkKnight.jpg",
                "thumbnail": { "source": "https://upload.wikimedia.org/.../500px-DarkKnight.jpg", "width": 500, "height": 750 },
                "original": { "source": "https://upload.wikimedia.org/.../DarkKnight.jpg" }
              },
              { "title": "Немає статті", "missing": "" },
              { "title": "Лише оригінал", "pageimage": "Logo.png", "original": { "source": "https://upload.wikimedia.org/.../Logo.png" } }
            ]
          }
        }
        """;

    [Fact]
    public void ParsePageImagesJson_Prefers_Thumbnail_Falls_Back_To_Original()
    {
        var pages = WikipediaPosterService.ParsePageImagesJson(PageImagesJson);
        Assert.Equal(3, pages.Count);

        var withThumb = pages.Single(p => p.Title == "Темний лицар (фільм)");
        Assert.False(withThumb.Missing);
        Assert.Equal("https://upload.wikimedia.org/.../500px-DarkKnight.jpg", WikipediaPosterService.PickPoster(withThumb));

        var originalOnly = pages.Single(p => p.Title == "Лише оригінал");
        Assert.Null(originalOnly.Thumbnail);
        Assert.Equal("https://upload.wikimedia.org/.../Logo.png", WikipediaPosterService.PickPoster(originalOnly));

        var missing = pages.Single(p => p.Title == "Немає статті");
        Assert.True(missing.Missing);
        Assert.Null(WikipediaPosterService.PickPoster(missing));
    }

    [Fact]
    public void ParsePageImagesJson_Empty_Or_Broken_Json()
    {
        Assert.Empty(WikipediaPosterService.ParsePageImagesJson("{}"));
        Assert.Empty(WikipediaPosterService.ParsePageImagesJson("{\"query\":{}}"));
    }

    [Fact]
    public void ResolveTitle_Walks_Normalized_Then_Redirect_Chain()
    {
        var renames = WikipediaPosterService.ParseRenamesJson(PageImagesJson);
        // "темний лицар" → normalized "Темний лицар" → redirect "Темний лицар (фільм)"
        Assert.Equal("Темний лицар (фільм)", WikipediaPosterService.ResolveTitle("темний лицар", renames));
        Assert.Equal("Темний лицар (фільм)", WikipediaPosterService.ResolveTitle("Темний лицар", renames));
        Assert.Equal("Немає статті", WikipediaPosterService.ResolveTitle("Немає статті", renames)); // без перейменувань
    }

    [Fact]
    public void MapRequestedToUrls_Maps_Through_Redirects_Skips_Missing()
    {
        var renames = WikipediaPosterService.ParseRenamesJson(PageImagesJson);
        var urlByPage = new Dictionary<string, string>
        {
            ["Темний лицар (фільм)"] = "https://upload.wikimedia.org/.../500px-DarkKnight.jpg",
            ["Лише оригінал"] = "https://upload.wikimedia.org/.../Logo.png",
        };
        var map = WikipediaPosterService.MapRequestedToUrls(
            ["темний лицар", "Лише оригінал", "Немає статті"], renames, urlByPage);

        Assert.Equal(2, map.Count);
        Assert.Equal("https://upload.wikimedia.org/.../500px-DarkKnight.jpg", map["темний лицар"]);
        Assert.Equal("https://upload.wikimedia.org/.../Logo.png", map["Лише оригінал"]);
        Assert.False(map.ContainsKey("Немає статті")); // missing сторінка не мапиться
    }

    [Fact]
    public void BuildPageImagesUrl_Encodes_Titles_And_Joins_With_Pipes()
    {
        var url = WikipediaPosterService.BuildPageImagesUrl(
            "https://{0}.wikipedia.org/w/api.php", "uk", ["Темний лицар (фільм)", "Se7en"]);

        Assert.StartsWith("https://uk.wikipedia.org/w/api.php?", url);
        Assert.Contains("action=query", url);
        Assert.Contains("prop=pageimages", url);
        Assert.Contains("piprop=thumbnail%7Coriginal", url);
        Assert.Contains("pithumbsize=500", url);
        Assert.Contains("redirects=1", url);
        Assert.Contains("titles=", url);
        // пробіли/спецсимволи закодовані, роздільник %7C
        Assert.Contains(Uri.EscapeDataString("Темний лицар (фільм)"), url);
        Assert.Contains("%7C", url);
    }

    // ── Staging SQL: fill-only + ідемпотентність ──

    [Fact]
    public void Staging_Columns_Match_Copy_And_Ddl()
    {
        Assert.Equal(new[] { "imdb_id", "poster_url", "lang", "qid" }, WikipediaPosterStaging.Columns);
        Assert.Contains("wikipedia_posters_raw (imdb_id, poster_url, lang, qid)", WikipediaPosterStaging.CopyFromStdinSql());
        Assert.Contains("imdb_id text NOT NULL", WikipediaPosterStaging.CreateTableSql);
        Assert.Contains("poster_url text NULL", WikipediaPosterStaging.CreateTableSql);
        Assert.Contains("lang text NULL", WikipediaPosterStaging.CreateTableSql);
        Assert.Contains("qid text NULL", WikipediaPosterStaging.CreateTableSql);
        // UpdateSql читає ту саму колонку, яку пише COPY (регрес на 42703)
        Assert.Contains("s.poster_url", WikipediaPosterStaging.UpdateSql);
        // lang/qid у UPDATE не потрібні (тільки COPY/стaging) — в UPDATE лише poster_path
        Assert.DoesNotContain("s.lang", WikipediaPosterStaging.UpdateSql);
        Assert.DoesNotContain("s.qid", WikipediaPosterStaging.UpdateSql);
    }

    [Fact]
    public void Staging_UpdateSql_Is_Fill_Only_And_Idempotent()
    {
        var sql = WikipediaPosterStaging.UpdateSql;
        // fill-only: постер ЛИШЕ у порожні (NULL або ''); наявні TMDB/sweet.tv/shikimori не перебиваємо
        Assert.Contains("poster_path = COALESCE(t.poster_path, s.poster_url)", sql);
        Assert.Contains("(t.poster_path IS NULL OR t.poster_path = '')", sql);
        Assert.Contains("s.poster_url IS NOT NULL AND s.poster_url <> ''", sql);
        // ідентичність через imdb (індекс ix_titles_imdb_id)
        Assert.Contains("t.imdb_id = s.imdb_id", sql);
        // ідемпотентність: після заповнення poster_path вже не NULL → повторний UPDATE не чіпає рядок
        Assert.DoesNotContain("title_uk", sql);
        Assert.DoesNotContain("overview", sql);
    }

    // ── Чистка фейкових постерів (адмін-джоба) ──

    private static async Task<List<Title>> SeedFakePostersAsync(MovieDiscoveryDbContext db)
    {
        var titles = new List<Title>
        {
            new() { Type = "movie", TitleEn = "Dark Fallback", Year = 2008, ImdbId = "tt0468569",
                PosterPath = "https://i.ytimg.com/vi/abc/hqdefault.jpg" },
            new() { Type = "anime", TitleEn = "Missing Orig", Year = 2024,
                PosterPath = "https://shikimori.one/assets/globals/missing_original.jpg?1711947840" }, // з хвостом-запитом
            new() { Type = "movie", TitleEn = "Real Poster", Year = 1995, ImdbId = "tt0114369",
                PosterPath = "/tmdb_poster.jpg" }, // справжній — НЕ чіпаємо
            new() { Type = "movie", TitleEn = "Already Null", Year = 2000 },
        };
        db.Titles.AddRange(titles);
        await db.SaveChangesAsync();
        return titles;
    }

    [Fact]
    public async Task FakePosterCleanup_Nulls_Fakes_Keeps_Real_And_Null()
    {
        var db = TestDb.Create("wpc_" + Guid.NewGuid().ToString("N"));
        var titles = await SeedFakePostersAsync(db);
        var svc = new PosterFakeCleanupService(db, Logs.Null<PosterFakeCleanupService>());

        var result = await svc.RunAsync();

        Assert.Equal(2, result.Cleaned);
        Assert.Equal(1, result.Ytimg);
        Assert.Equal(1, result.MissingOriginal);
        var stored = await db.Titles.OrderBy(t => t.Id).ToListAsync();
        Assert.Null(stored[0].PosterPath); // ytimg → NULL
        Assert.Null(stored[1].PosterPath); // missing_original (з хвостом ?ts) → NULL
        Assert.Equal("/tmdb_poster.jpg", stored[2].PosterPath); // справжній не зачеплений
        Assert.Null(stored[3].PosterPath); // і так NULL — лишиться NULL
    }

    [Fact]
    public async Task FakePosterCleanup_Is_Idempotent_Second_Run_Zero()
    {
        var db = TestDb.Create("wpc_" + Guid.NewGuid().ToString("N"));
        await SeedFakePostersAsync(db);
        var svc = new PosterFakeCleanupService(db, Logs.Null<PosterFakeCleanupService>());

        var first = await svc.RunAsync();
        var second = await svc.RunAsync();

        Assert.Equal(2, first.Cleaned);
        Assert.Equal(0, second.Cleaned); // повторний прогін: фейків немає → 0 (безпомилково)
        Assert.Equal(0, second.Ytimg);
        Assert.Equal(0, second.MissingOriginal);
    }
}
