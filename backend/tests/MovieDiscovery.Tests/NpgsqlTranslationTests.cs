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
                db.Titles.AsNoTracking(),
                ["comedy", "romance"], minRating: 7.0, shortRuntime: false)
            .ToQueryString();

        var upper = sql.ToUpperInvariant();
        // жанри через GIN-friendly overlap (genres &&), рейтинг через COALESCE
        Assert.Contains("GENRES", upper);
        Assert.Contains("COALESCE", upper);
        // фаза 1 (FR-21): індексний ORDER BY popularity до LIMIT пулу кандидатів;
        // ранжування watch_links/uk/рейтингом — фаза 2 в пам'яті (33с → <1с).
        Assert.Contains("POPULARITY", upper);
        Assert.True(
            upper.IndexOf("ORDER BY") > -1 && upper.IndexOf("LIMIT") > upper.IndexOf("ORDER BY"),
            $"Очікується ORDER BY перед LIMIT, отримано: {sql}");
    }

    [Fact]
    public void Search_Q_Is_Composable_No_Raw_Subquery_Wrap()
    {
        // Регрес 09.11: FromSqlInterpolated + OrderBy/Skip/Take генерували
        // SELECT * FROM (SELECT * FROM titles WHERE ILIKE...) ORDER BY LIMIT —
        // внутрішній підзапит materializував УСІ збіги (104k × TOAST) → 15–30 с.
        // Composable LINQ має дати ОДИН рівень з ранньою термінацією по індексу.
        using var db = CreateContext();
        var svc = new SearchService(db);
        var query = svc.BuildQuery(new SearchQuery(
            Q: "x", Type: "", Genres: [], YearFrom: null, YearTo: null, Country: null,
            MinRating: null, MaxRating: null, MinRuntime: null, MaxRuntime: null,
            Status: null, Sort: "popularity", Page: 1, PageSize: 24));
        var sorted = query
            .OrderByDescending(t => t.Popularity)
            .Skip(0).Take(24);
        var sql = sorted.ToQueryString();
        var upper = sql.ToUpperInvariant();

        Assert.Contains("ACTORS_TRGM_TEXT", upper);          // функція транслюється (HasDbFunction)
        Assert.Contains("TITLE_RU", upper);                  // ru-гілка пошуку на місці
        Assert.DoesNotContain("FROM (SELECT", upper);        // БЕЗ raw-підзапиту-матеріалізації
        Assert.Contains("ILIKE", upper);
    }

    // ── Двофазний текстовий пошук: Фаза A (FTS) + EscapeLike-фікс ──

    [Fact]
    public void Search_Fts_PhaseA_Translates_TitleSearchMatch()
    {
        using var db = CreateContext();
        var svc = new SearchService(db);
        var query = svc.BuildFtsQuery(new SearchQuery(
            Q: "dune", Type: "", Genres: [], YearFrom: null, YearTo: null, Country: null,
            MinRating: null, MaxRating: null, MinRuntime: null, MaxRuntime: null,
            Status: null, Sort: "popularity", Page: 1, PageSize: 24), tsQuery: "dun:*");
        var sql = query.ToQueryString();
        var upper = sql.ToUpperInvariant();

        // HasDbFunction транслюється у public.title_search_match(...) — на виразі
        // title_search_fts(...) у БД стоїть GIN ix_titles_search_fts
        Assert.Contains("TITLE_SEARCH_MATCH", upper);
        // composable, без raw-підзапиту-матеріалізації
        Assert.DoesNotContain("FROM (SELECT", upper);
    }

    [Fact]
    public void Search_Ilike_UsesEscapeCharacter()
    {
        // EscapeLike-фікс: 3-арг ILIKE(col, pat, "\\") → SQL ESCAPE '\'. 2-аргна форма
        // давала ESCAPE '' — бекслеші EscapeLike (\%, \_, \\) були мертві.
        using var db = CreateContext();
        var svc = new SearchService(db);
        var sql = svc.BuildQuery(new SearchQuery(
            Q: "x", Type: "", Genres: [], YearFrom: null, YearTo: null, Country: null,
            MinRating: null, MaxRating: null, MinRuntime: null, MaxRuntime: null,
            Status: null, Sort: "popularity", Page: 1, PageSize: 24)).ToQueryString();

        Assert.Contains("ESCAPE", sql.ToUpperInvariant());
    }

    // ── Ревізія Фази A: walk-безпечний popularity-фетч (дефолтна селективність FTS ~2%
    // з ORDER BY popularity дає popularity-walk = 109 с на рідкісних запитах) ──

    [Fact]
    public void Search_Fts_PopularityFetch_TopN_NonSargableOrderBy_Translates()
    {
        using var db = CreateContext();
        var svc = new SearchService(db);
        var q = new SearchQuery(
            Q: "dune", Type: "", Genres: [], YearFrom: null, YearTo: null, Country: null,
            MinRating: null, MaxRating: null, MinRuntime: null, MaxRuntime: null,
            Status: null, Sort: "relevance", Page: 1, PageSize: 24);
        var sql = svc.BuildFtsPopularityFetch(q, "dun:*", topN: true).Take(2002).ToQueryString();
        var upper = sql.ToUpperInvariant();

        Assert.Contains("TITLE_SEARCH_MATCH", upper);
        // арифметика popularity + 0.0 ламає sargability ORDER BY → walk неможливий
        Assert.Contains("+ 0.0", upper);
        Assert.Contains("COALESCE", upper); // ThenByDescending(year ?? 0)
        Assert.Contains("LIMIT", upper);
    }

    [Fact]
    public void Search_Fts_PopularityFetch_Unsorted_HasNoOrderBy()
    {
        // total ≤ FetchCap → фетчиться ВСІ збіги БЕЗ ORDER BY → Bitmap GIN без
        // сортування; порядок сторінки будує C# — walk неможливий у принципі.
        using var db = CreateContext();
        var svc = new SearchService(db);
        var q = new SearchQuery(
            Q: "dune", Type: "", Genres: [], YearFrom: null, YearTo: null, Country: null,
            MinRating: null, MaxRating: null, MinRuntime: null, MaxRuntime: null,
            Status: null, Sort: "relevance", Page: 1, PageSize: 24);
        var sql = svc.BuildFtsPopularityFetch(q, "dun:*", topN: false).Take(2002).ToQueryString();

        Assert.Contains("TITLE_SEARCH_MATCH", sql.ToUpperInvariant());
        Assert.DoesNotContain("ORDER", sql.ToUpperInvariant());
    }
}
