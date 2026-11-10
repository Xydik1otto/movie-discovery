using System.Text;
using MovieDiscovery.Api.Controllers;
using MovieDiscovery.Infrastructure.Series;

namespace MovieDiscovery.Tests;

/// <summary>
/// Тести title-series синку (Wikidata P179 «part of the series» + P155/P156 prev/next):
/// CSV-парсер нових колонок, SPARQL VALUES-запит, порядок перегляду, upsert-SQL.
/// </summary>
public class TitleSeriesTests
{
    private static List<TitleSeriesRow> ParseCsv(string csv) =>
        TitleSeriesSyncService.ParseCsv(new MemoryStream(Encoding.UTF8.GetBytes(csv)), maxRows: null);

    [Fact]
    public void ParseCsv_Simple_Row_With_Prev_Next()
    {
        var csv = "imdb,series,luk,lru,len,prev,next\n" +
                  "tt0114369,http://www.wikidata.org/entity/Q128817,Сім,Семь,Se7en,tt0091139,tt0158753";
        var rows = ParseCsv(csv);
        var row = Assert.Single(rows);
        Assert.Equal("tt0114369", row.ImdbId);
        Assert.Equal("Q128817", row.SeriesQid);
        Assert.Equal("Сім", row.LabelUk);
        Assert.Equal("Семь", row.LabelRu);
        Assert.Equal("Se7en", row.LabelEn);
        Assert.Equal("tt0091139", row.PrevImdb);
        Assert.Equal("tt0158753", row.NextImdb);
    }

    [Fact]
    public void ParseCsv_Quoted_Comma_And_Newline_In_Label()
    {
        var csv = "imdb,series,luk,lru,len,prev,next\n" +
                  "tt1,http://www.wikidata.org/entity/Q1,\"Сага, частина перша\",\"Сага,\nз переносом\",,\"tt0\",\"tt1\"\n" +
                  "tt2,http://www.wikidata.org/entity/Q1,Просто,Теж,Plain,,";
        var rows = ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Сага, частина перша", rows[0].LabelUk);
        Assert.Contains("\n", rows[0].LabelRu!); // перенос усередині quoted-поля не ріже рядок
        Assert.Null(rows[0].LabelEn);
        Assert.Equal("tt0", rows[0].PrevImdb);
        Assert.Equal("tt2", rows[1].ImdbId);
        Assert.Null(rows[1].NextImdb);
    }

    [Fact]
    public void ParseCsv_Duplicate_Imdb_First_Row_Wins()
    {
        // Тайтл у кількох серіях (2 значення P179): у one-row-per-imdb моделі
        // два конфліктні рядки в одному INSERT дають 21000 — перший виграє.
        var csv = "imdb,series,luk,lru,len,prev,next\n" +
                  "tt1,http://www.wikidata.org/entity/Q111,Перша,Первая,First,,\n" +
                  "tt1,http://www.wikidata.org/entity/Q222,Друга,Вторая,Second,,\n" +
                  "tt2,http://www.wikidata.org/entity/Q111,Інший,,,," ;
        var rows = ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Q111", rows[0].SeriesQid);
        Assert.Equal("Перша", rows[0].LabelUk);
        Assert.Equal("tt2", rows[1].ImdbId);
    }

    [Fact]
    public void ParseCsv_Skips_Rows_Without_Valid_Series()
    {
        // без ?series (не член франшизи — відфільтровано SPARQL, але CSV без BOUND теоретично можливий)
        var csv = "imdb,series,luk,lru,len,prev,next\n" +
                  "tt1,,,,,,\n" +
                  "tt2,http://www.wikidata.org/entity/Q333,Є,,,," ;
        var rows = ParseCsv(csv);
        var row = Assert.Single(rows);
        Assert.Equal("tt2", row.ImdbId);
        Assert.Equal("Q333", row.SeriesQid);
    }

    [Fact]
    public void ParseCsv_Empty_Or_Header_Only_Or_No_Columns()
    {
        Assert.Empty(ParseCsv(""));
        Assert.Empty(ParseCsv("imdb,series,luk,lru,len,prev,next\n"));
        // немає колонки imdb/series → порожньо, без винятку
        Assert.Empty(ParseCsv("a,b\n1,2"));
        Assert.Empty(ParseCsv("imdb,luk\ntt1,Сім"));
    }

    [Theory]
    [InlineData("http://www.wikidata.org/entity/Q128817", "Q128817")]
    [InlineData("https://www.wikidata.org/entity/Q207328", "Q207328")]
    [InlineData("Q123", null)] // без IRI-маркера /entity/ — не валідний рядок CSV
    [InlineData("", null)]
    [InlineData("http://example.com/other/Q1", null)]
    [InlineData("http://www.wikidata.org/entity/Q12abc", null)] // не Q+цифри
    public void ExtractQid_Parses_Entity_Iri(string iri, string? expected) =>
        Assert.Equal(expected, TitleSeriesSyncService.ExtractQid(iri));

    [Fact]
    public void SparqlValues_Contains_P179_P155_P156_And_Lang_Filters()
    {
        var q = TitleSeriesSyncService.SparqlValues(["tt0114369", "tt0133093"]);
        Assert.Contains("VALUES ?imdb { \"tt0114369\" \"tt0133093\" }", q);
        Assert.Contains("?item wdt:P345 ?imdb .", q);
        Assert.Contains("wdt:P179", q);  // серія (part of the series)
        Assert.Contains("wdt:P155", q);  // previous part
        Assert.Contains("wdt:P156", q);  // next part
        Assert.Contains("wdt:P345 ?prev", q);
        Assert.Contains("wdt:P345 ?next", q);
        Assert.Contains("LANG(?luk) = \"uk\"", q);
        Assert.Contains("LANG(?lru) = \"ru\"", q);
        Assert.Contains("LANG(?len) = \"en\"", q);
        Assert.Contains("FILTER(BOUND(?series))", q); // лише члени франшиз
    }

    [Fact]
    public void SparqlValues_Escapes_Quotes_In_Ids()
    {
        var evil = TitleSeriesSyncService.SparqlValues(["tt0\"0"]);
        Assert.DoesNotContain("tt0\"0", evil);
        Assert.Contains("tt00", evil);
    }

    [Fact]
    public void Batch_Splits_Evenly_With_Tail()
    {
        var ids = Enumerable.Range(1, 10).Select(i => $"tt{i:0000000}").ToList();
        var batches = TitleSeriesSyncService.Batch(ids, 4).ToList();
        Assert.Equal(3, batches.Count);
        Assert.Equal(4, batches[0].Count);
        Assert.Equal(4, batches[1].Count);
        Assert.Equal(2, batches[2].Count);
        Assert.Empty(TitleSeriesSyncService.Batch([], 4));
    }

    [Fact]
    public void Staging_Columns_Match_Copy_And_Ddl()
    {
        Assert.Equal(
            new[] { "imdb_id", "series_qid", "label_uk", "label_ru", "label_en", "prev_imdb", "next_imdb" },
            TitleSeriesStaging.Columns);
        Assert.Contains(
            "title_series_raw (imdb_id, series_qid, label_uk, label_ru, label_en, prev_imdb, next_imdb)",
            TitleSeriesStaging.CopyFromStdinSql());
        foreach (var col in TitleSeriesStaging.Columns)
            Assert.Contains($"{col} text", TitleSeriesStaging.CreateTableSql);
        // DDL: imdb_id/series_qid NOT NULL
        Assert.Contains("imdb_id text NOT NULL", TitleSeriesStaging.CreateTableSql);
        Assert.Contains("series_qid text NOT NULL", TitleSeriesStaging.CreateTableSql);
    }

    [Fact]
    public void UpsertSql_Shape_Full_Overwrite_On_Imdb_Conflict()
    {
        var sql = TitleSeriesStaging.UpsertSql;
        Assert.Contains("INSERT INTO title_series", sql);
        Assert.Contains("FROM title_series_raw s", sql);
        Assert.Contains("ON CONFLICT (imdb_id) DO UPDATE", sql);
        // повний перезапис колонок серії (Wikidata — авторитетне джерело)
        Assert.Contains("series_qid = EXCLUDED.series_qid", sql);
        Assert.Contains("label_uk = EXCLUDED.label_uk", sql);
        Assert.Contains("label_ru = EXCLUDED.label_ru", sql);
        Assert.Contains("label_en = EXCLUDED.label_en", sql);
        Assert.Contains("prev_imdb = EXCLUDED.prev_imdb", sql);
        Assert.Contains("next_imdb = EXCLUDED.next_imdb", sql);
        Assert.Contains("updated_at = now()", sql);
        // COPY і upsert читають ті самі колонки (регрес на 42703)
        Assert.Contains("s.next_imdb", sql);
    }

    [Fact]
    public void Ordering_Year_Asc_Nulls_Last_Then_Title()
    {
        // рік ASC
        Assert.True(SeriesOrdering.Compare(1999, "b", 2001, "a") < 0);
        // NULL-рік — в кінець
        Assert.True(SeriesOrdering.Compare(1999, "b", null, "a") < 0);
        Assert.True(SeriesOrdering.Compare(null, "a", 1999, "b") > 0);
        Assert.Equal(0, SeriesOrdering.Compare(null, "a", null, "a"));
        // той самий рік → назва (ordinal)
        Assert.True(SeriesOrdering.Compare(2001, "A", 2001, "B") < 0);
        Assert.True(SeriesOrdering.Compare(2001, "B", 2001, "A") > 0);
        Assert.Equal(0, SeriesOrdering.Compare(2001, "Same", 2001, "Same"));
    }

    [Fact]
    public void Ordering_Sorts_Watch_Sequence_With_Nulls_Last()
    {
        var items = new List<(int? Year, string Title)>
        {
            (2008, "Iron Man"),
            (null, "Каскад без року"),
            (2010, "Iron Man 2"),
            (2008, "The Incredible Hulk"),
        };
        items.Sort((a, b) => SeriesOrdering.Compare(a.Year, a.Title, b.Year, b.Title));
        Assert.Equal([2008, 2008, 2010, null], items.Select(i => i.Year));
        Assert.Equal("Iron Man", items[0].Title);
        Assert.Equal("The Incredible Hulk", items[1].Title);
        Assert.Equal("Каскад без року", items[3].Title);
    }

    [Fact]
    public void SeriesName_Uk_Chain_Then_En()
    {
        // uk-режим: uk → ru → en
        Assert.Equal("Кінематографічний всесвіт Marvel", SeriesName("uk", "Кінематографічний всесвіт Marvel", "Кинематографическая вселенная Marvel", "Marvel Cinematic Universe"));
        Assert.Equal("Кинематографическая вселенная Marvel", SeriesName("uk", null, "Кинематографическая вселенная Marvel", "Marvel Cinematic Universe"));
        Assert.Equal("Marvel Cinematic Universe", SeriesName("uk", null, null, "Marvel Cinematic Universe"));
        Assert.Null(SeriesName("uk", null, null, null));
        // en-режим: en → uk → ru
        Assert.Equal("Marvel Cinematic Universe", SeriesName("en", "Кінематографічний всесвіт Marvel", null, "Marvel Cinematic Universe"));
        Assert.Equal("Кінематографічний всесвіт Marvel", SeriesName("en", "Кінематографічний всесвіт Marvel", "Кинематографическая вселенная Marvel", null));
        // порожні рядки = null
        Assert.Null(SeriesName("uk", "  ", "", null));

        static string? SeriesName(string lang, string? uk, string? ru, string? en) =>
            SeriesController.SeriesName(lang, uk, ru, en);
    }
}
