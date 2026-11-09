using System.Text;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Wikidata;

namespace MovieDiscovery.Tests;

/// <summary>
/// Тести Wikidata-синку (uk + ru-фолбек за рішенням власника 09.11): CSV-парсер SPARQL,
/// ExtractCommonsFile, staging SQL-клаузи, бакети, ланцюжок назв uk → ru → en у TitleMapper.
/// </summary>
public class WikidataNamesTests
{
    private static List<WikidataNameRow> ParseCsv(string csv) =>
        WikidataNamesService.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)), maxRows: null);

    [Fact]
    public void ParseCsv_Simple_Row_With_Image()
    {
        var csv = "imdb,uk,ru,img\n" +
                  "tt0114369,Сім,Семь,http://commons.wikimedia.org/wiki/Special:FilePath/Se7en_poster.jpg";
        var rows = ParseCsv(csv);
        var row = Assert.Single(rows);
        Assert.Equal("tt0114369", row.ImdbId);
        Assert.Equal("Сім", row.Uk);
        Assert.Equal("Семь", row.Ru);
        Assert.Equal("Se7en poster.jpg", row.Image); // underscores → пробіли
    }

    [Fact]
    public void ParseCsv_With_Descriptions_Maps_Desc_Columns()
    {
        // Порядок колонок = порядок SELECT у SparqlValues: imdb,uk,ru,img,dukk,dr
        var csv = "imdb,uk,ru,img,dukk,dr\n" +
                  "tt0114369,Сім,Семь,,\"Фільм про семеро\",\"Фильм о семи\"\n" +
                  "tt0133093,Матриця,Матрица,,,";
        var rows = ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Фільм про семеро", rows[0].DescUk);
        Assert.Equal("Фильм о семи", rows[0].DescRu);
        Assert.Null(rows[0].Image);
        Assert.Null(rows[1].DescUk);
        Assert.Null(rows[1].DescRu);
    }

    [Fact]
    public void ParseCsv_Old_Header_Without_Desc_Columns_Yields_Nulls()
    {
        // Захист від старого формату CSV (без dukk/dr) — парсер не падає, описи просто NULL
        var csv = "imdb,uk,ru,img\ntt1,Сім,Семь,";
        var row = Assert.Single(ParseCsv(csv));
        Assert.Null(row.DescUk);
        Assert.Null(row.DescRu);
    }

    [Fact]
    public void ParseCsv_Quoted_Comma_And_Newline_In_Label()
    {
        var csv = "imdb,uk,ru,img\n" +
                  "tt1,\"Сім, фільм\",\"Семь,\nс цитатою\",\n" +
                  "tt2,Просто,Теж,\"http://commons.wikimedia.org/wiki/Special:FilePath/x.png?width=9\"";
        var rows = ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Сім, фільм", rows[0].Uk);
        Assert.Contains("\n", rows[0].Ru); // перенос усередині quoted-поля не ріже рядок
        Assert.Null(rows[0].Image);
        Assert.Equal("x.png", rows[1].Image); // ?width відкушено
    }

    [Fact]
    public void ParseCsv_Skips_Rows_Without_Any_Needed_Label()
    {
        var csv = "imdb,uk,ru,img\ntt1,,,\ntt2,Є,,";
        var rows = ParseCsv(csv);
        var row = Assert.Single(rows);
        Assert.Equal("tt2", row.ImdbId);
    }

    [Fact]
    public void ParseCsv_Empty_Or_Header_Only()
    {
        Assert.Empty(ParseCsv(""));
        Assert.Empty(ParseCsv("imdb,uk,ru,img\n"));
        // немає колонки imdb → порожньо, без винятку
        Assert.Empty(ParseCsv("a,b\n1,2"));
    }

    [Theory]
    [InlineData("http://commons.wikimedia.org/wiki/Special:FilePath/A_B.jpg", "A B.jpg")]
    [InlineData("https://commons.wikimedia.org/wiki/Special:FilePath/%D0%9B%D1%8E%D1%86.png?width=500", "Люц.png")]
    [InlineData("http://commons.wikimedia.org/wiki/Special:FilePath/doc.pdf", null)] // не зображення
    [InlineData("https://example.com/other.jpg", null)] // не Commons FilePath
    [InlineData("", null)]
    public void ExtractCommonsFile_Filters_NonRaster_And_Cuts_Query(string iri, string? expected) =>
        Assert.Equal(expected, WikidataNamesService.ExtractCommonsFile(iri));

    [Fact]
    public void Staging_UpdateSql_Ru_Fallback_And_Poster_Only_When_Empty()
    {
        var sql = WikidataNamesStaging.UpdateSql;
        Assert.Contains("title_uk = COALESCE(NULLIF(s.uk, ''), t.title_uk)", sql);
        Assert.Contains("title_ru = COALESCE(NULLIF(s.ru, ''), t.title_ru)", sql);
        // постер лише у порожні (P18 не гірший за наявний TMDB/sweet.tv)
        Assert.Contains("poster_path = COALESCE(t.poster_path, s.poster_url)", sql);
        // ідемпотентність: Predicate перекриває всі три поля
        Assert.Contains("t.title_uk IS NULL OR t.title_uk <> s.uk", sql);
        Assert.Contains("t.title_ru IS NULL OR t.title_ru <> s.ru", sql);
        Assert.Contains("t.poster_path IS NULL", sql);
        Assert.Contains("t.imdb_id = s.imdb_id", sql); // індекс ix_titles_imdb_id
    }

    [Fact]
    public void Staging_UpdateSql_Fills_Only_Empty_Overviews_With_Descriptions()
    {
        var sql = WikidataNamesStaging.UpdateSql;
        // описи — ЛИШЕ заповнення порожніх (не перебиваємо TMDB/Аніліст), '' теж вважаємо порожнім
        Assert.Contains("overview_uk = COALESCE(NULLIF(t.overview_uk, ''), s.desc_uk)", sql);
        Assert.Contains("overview_ru = COALESCE(NULLIF(t.overview_ru, ''), s.desc_ru)", sql);
        // overview_en не чіпаємо (описи аніме — окремий AniList-синк)
        Assert.DoesNotContain("overview_en", sql);
        // ідемпотентність: Predicate перекриває обидва описи
        Assert.Contains("s.desc_uk IS NOT NULL AND (t.overview_uk IS NULL OR t.overview_uk = '')", sql);
        Assert.Contains("s.desc_ru IS NOT NULL AND (t.overview_ru IS NULL OR t.overview_ru = '')", sql);
    }

    [Fact]
    public void Staging_Columns_Match_Copy_And_Ddl()
    {
        Assert.Equal(new[] { "imdb_id", "uk", "ru", "poster_url", "desc_uk", "desc_ru" }, WikidataNamesStaging.Columns);
        Assert.Contains("wikidata_names_raw (imdb_id, uk, ru, poster_url, desc_uk, desc_ru)", WikidataNamesStaging.CopyFromStdinSql());
        Assert.Contains("imdb_id text NOT NULL", WikidataNamesStaging.CreateTableSql);
        Assert.Contains("ru text NULL", WikidataNamesStaging.CreateTableSql);
        Assert.Contains("poster_url text NULL", WikidataNamesStaging.CreateTableSql);
        Assert.Contains("desc_uk text NULL", WikidataNamesStaging.CreateTableSql);
        Assert.Contains("desc_ru text NULL", WikidataNamesStaging.CreateTableSql);
        // UpdateSql читає ту саму колонку, яку пише COPY (регрес на 42703)
        Assert.Contains("s.poster_url", WikidataNamesStaging.UpdateSql);
        Assert.Contains("s.desc_uk", WikidataNamesStaging.UpdateSql);
        Assert.Contains("s.desc_ru", WikidataNamesStaging.UpdateSql);
        Assert.DoesNotContain("s.image", WikidataNamesStaging.UpdateSql);
    }

    [Fact]
    public void PosterUrl_Builds_Thumbnail_With_Width500()
    {
        Assert.Equal(
            "https://commons.wikimedia.org/wiki/Special:FilePath/A_B.jpg?width=500",
            WikidataNamesStaging.PosterUrl("A B.jpg"));
        Assert.Null(WikidataNamesStaging.PosterUrl(null));
        Assert.Null(WikidataNamesStaging.PosterUrl("  "));
    }

    [Fact]
    public void SparqlValues_Batches_Ids_And_Escapes()
    {
        var q = WikidataNamesService.SparqlValues(["tt0114369", "tt0133093"]);
        Assert.Contains("VALUES ?imdb { \"tt0114369\" \"tt0133093\" }", q);
        Assert.Contains("?item wdt:P345 ?imdb .", q);
        Assert.Contains("LANG(?uk) = \"uk\"", q);
        Assert.Contains("LANG(?ru) = \"ru\"", q);
        Assert.Contains("wdt:P18", q);
        // короткі описи: schema:description (дефолтний префікс QS) з фільтрами обох мов
        Assert.Contains("schema:description ?dukk . FILTER(LANG(?dukk) = \"uk\")", q);
        Assert.Contains("schema:description ?dr . FILTER(LANG(?dr) = \"ru\")", q);
        // порядок SELECT = порядок колонок CSV (парсер дивиться за іменами, контракт — у тесті)
        Assert.StartsWith("SELECT ?imdb ?uk ?ru ?img ?dukk ?dr WHERE {", q);
        // лапки в id вирізаються (захист від ін'єкції в VALUES)
        var evil = WikidataNamesService.SparqlValues(["tt0\"0"]);
        Assert.DoesNotContain("tt0\"0", evil);
        Assert.Contains("tt00", evil);
    }

    [Fact]
    public void Batch_Splits_Evenly_With_Tail()
    {
        var ids = Enumerable.Range(1, 10).Select(i => $"tt{i:0000000}").ToList();
        var batches = WikidataNamesService.Batch(ids, 4).ToList();
        Assert.Equal(3, batches.Count);
        Assert.Equal(4, batches[0].Count);
        Assert.Equal(4, batches[1].Count);
        Assert.Equal(2, batches[2].Count);
        Assert.Empty(WikidataNamesService.Batch([], 4));
    }

    [Fact]
    public void TitleMapper_Uk_Missing_Falls_Back_To_Ru_Then_En()
    {
        var t = new Title { TitleEn = "Lucifer", TitleRu = "Люцифер", TitleUk = null };
        Assert.Equal("Люцифер", TitleMapper.ToCard(t, "uk").Title);
        Assert.Equal("Lucifer", TitleMapper.ToCard(t, "en").Title); // en-режим не локалізується

        var tUk = new Title { TitleEn = "Lucifer", TitleRu = "Люцифер", TitleUk = "Люцифер (укр)" };
        Assert.Equal("Люцифер (укр)", TitleMapper.ToCard(tUk, "uk").Title); // uk перемагає

        var tEnOnly = new Title { TitleEn = "Lucifer" };
        Assert.Equal("Lucifer", TitleMapper.ToCard(tEnOnly, "uk").Title);
    }

    [Fact]
    public void OnboardingQuestions_Has_Ten_With_Three_Titles_Steps()
    {
        var qs = OnboardingQuestions.Build();
        Assert.Equal(10, qs.Count);
        Assert.Equal(3, qs.Count(q => q.Type == "titles"));
        // titles-кроки без опцій (вибір через пошук-автокомпліт)
        Assert.All(qs.Where(q => q.Type == "titles"), q => Assert.Empty(q.Options));
        // жанрові кроки мають ключі й uk-назви
        var posters1 = qs[0];
        Assert.Equal(6, posters1.Options.Count);
        Assert.All(posters1.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.NameUk)));
    }
}
