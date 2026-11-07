using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Search;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

public sealed record SearchQuery(
    string? Q,
    string? Type,
    string[] Genres,
    int? YearFrom,
    int? YearTo,
    string? Country,
    double? MinRating,
    double? MaxRating,
    int? MinRuntime,
    int? MaxRuntime,
    string? Status,
    string Sort,
    int Page,
    int PageSize,
    // ── Розширені фільтри ──
    string? Actor = null,
    string? Director = null,
    string? Keyword = null,
    string? Language = null,
    int? MinVotes = null,
    int? SeasonsFrom = null,
    int? SeasonsTo = null,
    string[]? ExcludeGenres = null,
    bool? HasTrailer = null,
    bool? HasProviders = null,
    string? RatingSource = null,
    string ProviderRegion = "UA",
    bool? Watchable = null,
    // ── Віртуальні категорії та озвучки (FR-19) ──
    string? Category = null,
    string? AudioLang = null,
    int? MinQuality = null);

public sealed record SearchResult(IReadOnlyList<Title> Items, int Total, int Page, int PageSize);

/// <summary>
/// Пошук/фільтри ЛИШЕ по локальній БД (NFR-1: жодних зовнішніх викликів у hot path).
/// q шукає в назвах uk/en, акторах і режисері (ILIKE). Окремі фільтри: actor/director/
/// keyword/language/minVotes/seasons/excludeGenres/hasTrailer/hasProviders/ratingSource.
/// </summary>
public sealed class SearchService(MovieDiscoveryDbContext db)
{
    public async Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 50);

        var query = BuildQuery(q);
        var total = await query.CountAsync(ct);

        query = Sort(query, q.Sort);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new SearchResult(items, total, page, pageSize);
    }

    /// <summary>Побудова запиту з усіма фільтрами (public — тест трансляції без БД, як NpgsqlTranslationTests).</summary>
    public IQueryable<Title> BuildQuery(SearchQuery q)
    {
        IQueryable<Title> query;
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var pat = "%" + EscapeLike(q.Q.Trim()) + "%";
            query = db.Titles.FromSqlInterpolated($@"
SELECT * FROM titles
WHERE title_uk ILIKE {pat}
   OR title_en ILIKE {pat}
   OR director ILIKE {pat}
   OR EXISTS (SELECT 1 FROM unnest(actors) a WHERE a ILIKE {pat})");
        }
        else
        {
            query = db.Titles;
        }

        if (!string.IsNullOrWhiteSpace(q.Type))
            query = query.Where(t => t.Type == q.Type);

        if (q.Genres is { Length: > 0 })
            query = query.Where(t => t.Genres.Any(g => q.Genres.Contains(g)));

        if (q.ExcludeGenres is { Length: > 0 })
            query = query.Where(t => !t.Genres.Any(g => q.ExcludeGenres.Contains(g)));

        if (q.YearFrom is { } yFrom)
            query = query.Where(t => t.Year >= yFrom);
        if (q.YearTo is { } yTo)
            query = query.Where(t => t.Year <= yTo);

        query = ApplyRatingFilter(query, q);

        query = ApplyRuntimeFilter(query, q);

        if (!string.IsNullOrWhiteSpace(q.Status))
            query = query.Where(t => t.Status == q.Status);

        // ── Розширені фільтри ──

        if (!string.IsNullOrWhiteSpace(q.Actor))
        {
            var pat = "%" + EscapeLike(q.Actor.Trim()) + "%";
            query = query.Where(t => t.Actors.Any(a => EF.Functions.ILike(a, pat)));
        }

        if (!string.IsNullOrWhiteSpace(q.Director))
        {
            var pat = "%" + EscapeLike(q.Director.Trim()) + "%";
            query = query.Where(t => t.Director != null && EF.Functions.ILike(t.Director, pat));
        }

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var pat = "%" + EscapeLike(q.Keyword.Trim()) + "%";
            query = query.Where(t => t.Keywords.Any(k => EF.Functions.ILike(k, pat)));
        }

        if (!string.IsNullOrWhiteSpace(q.Language))
            query = query.Where(t => t.OriginalLanguage == q.Language.ToLowerInvariant());

        if (q.MinVotes is { } minV)
            query = query.Where(t => t.VotesImdb >= minV);

        if (q.SeasonsFrom is { } sFrom)
            query = query.Where(t => t.Seasons >= sFrom);
        if (q.SeasonsTo is { } sTo)
            query = query.Where(t => t.Seasons <= sTo);

        if (q.HasTrailer == true)
            query = query.Where(t => t.TrailerYtId != null && t.TrailerYtId != "");

        if (q.HasProviders == true)
        {
            var region = string.IsNullOrWhiteSpace(q.ProviderRegion) ? "UA" : q.ProviderRegion.Trim().ToUpperInvariant();
            query = query.Where(t => db.Providers.Any(p => p.TitleId == t.Id && p.Region == region));
        }

        // FR-14: «лише те, що можна подивитись» — EXISTS watch_links is_available
        if (q.Watchable == true)
            query = query.Where(t => db.WatchLinks.Any(w => w.TitleId == t.Id && w.IsAvailable));

        // ── FR-19: віртуальні категорії, країна-виробник, озвучки, якість ──

        // country: кома-список кодів — збіг з будь-яким; title.country (IMDb akas, FR-19)
        // або countries[] (TMDB) — працює до і після akas-синку
        if (!string.IsNullOrWhiteSpace(q.Country))
        {
            var countryCodes = q.Country.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (countryCodes.Length > 0)
                query = query.Where(CountryAnyPredicate(countryCodes));
        }

        // category: movie|series|anime|manga → type; tvshow/cartoon/dorama → SQL-умови
        // по genres/country (GENRE_SETS у Core — контрактна константа)
        if (!string.IsNullOrWhiteSpace(q.Category))
        {
            var category = q.Category.Trim().ToLowerInvariant();
            switch (category)
            {
                case "movie" or "series" or "anime" or "manga":
                    query = query.Where(t => t.Type == category);
                    break;
                case "tvshow":
                    // genres && ARRAY['reality-tv','talk-show','game-show','variety']
                    query = query.Where(GenresAnyPredicate(VirtualCategories.GenreSets["tvshow"]));
                    break;
                case "cartoon":
                    // genres @> ARRAY['animation'] AND type IN ('movie','series'); аніме НЕ дублюється
                    query = query.Where(t =>
                        (t.Type == "movie" || t.Type == "series"))
                        .Where(GenresAnyPredicate(VirtualCategories.GenreSets["cartoon"]));
                    break;
                case "dorama":
                    // country IN ('KR','CN','JP') AND genres @> ARRAY['drama'] (both type)
                    var doramaCountries = VirtualCategories.DoramaCountries;
                    query = query.Where(t =>
                            t.Country != null &&
                            doramaCountries.Contains(t.Country))
                        .Where(GenresAnyPredicate([VirtualCategories.DoramaGenre]));
                    break;
            }
        }

        // audioLang: EXISTS watch_links (is_available, audio_lang == X)
        if (!string.IsNullOrWhiteSpace(q.AudioLang))
        {
            var audioLang = q.AudioLang.Trim().ToLowerInvariant();
            query = query.Where(t => db.WatchLinks
                .Any(w => w.TitleId == t.Id && w.IsAvailable && w.AudioLang == audioLang));
        }

        // minQuality: EXISTS watch_links (is_available, quality >= X)
        if (q.MinQuality is { } minQuality)
            query = query.Where(t => db.WatchLinks
                .Any(w => w.TitleId == t.Id && w.IsAvailable && w.Quality >= minQuality));

        return query;
    }

    /// <summary>Рейтинг: ratingSource обирає, до якого джерела застосувати мінімум/максимум.</summary>
    private static IQueryable<Title> ApplyRatingFilter(IQueryable<Title> query, SearchQuery q)
    {
        if (q.MinRating is null && q.MaxRating is null) return query;

        if (q.MinRating is { } minR)
            query = q.RatingSource switch
            {
                "tmdb" => query.Where(t => t.RatingTmdb >= minR),
                "imdb" => query.Where(t => t.RatingImdb >= minR),
                _ => query.Where(t => (t.RatingTmdb ?? t.RatingImdb) >= minR),
            };
        if (q.MaxRating is { } maxR)
            query = q.RatingSource switch
            {
                "tmdb" => query.Where(t => t.RatingTmdb <= maxR),
                "imdb" => query.Where(t => t.RatingImdb <= maxR),
                _ => query.Where(t => (t.RatingTmdb ?? t.RatingImdb) <= maxR),
            };
        return query;
    }

    /// <summary>Тривалість: фільм → runtime_min; серіал → episode_runtime_min; без типу — будь-яке з двох.</summary>
    private static IQueryable<Title> ApplyRuntimeFilter(IQueryable<Title> query, SearchQuery q)
    {
        var min = q.MinRuntime;
        var max = q.MaxRuntime;
        if (min is null && max is null) return query;

        return q.Type switch
        {
            "movie" => query.Where(t =>
                (min == null || t.RuntimeMin >= min) && (max == null || t.RuntimeMin <= max)),
            "series" or "show" => query.Where(t =>
                (min == null || t.EpisodeRuntimeMin >= min) && (max == null || t.EpisodeRuntimeMin <= max)),
            _ => query.Where(t =>
                ((min == null || t.RuntimeMin >= min) && (max == null || t.RuntimeMin <= max)) ||
                ((min == null || t.EpisodeRuntimeMin >= min) && (max == null || t.EpisodeRuntimeMin <= max))),
        };
    }

    /// <summary>
    /// country: title.country == X АБО countries[] містить X — для кожного коду (OR-ланцюг).
    /// Явні equality/Contains-вирази: перекладаються і Npgsql (country = X / countries @> ...),
    /// і тестовим InMemory (parameter-list.Contains всередині Any над масивною колонкою він не перекладає).
    /// </summary>
    internal static Expression<Func<Title, bool>> CountryAnyPredicate(IReadOnlyList<string> codes)
    {
        var t = Expression.Parameter(typeof(Title), "t");
        Expression? body = null;
        foreach (var code in codes)
        {
            var eqCountry = Expression.Equal(
                Expression.Property(t, nameof(Title.Country)),
                Expression.Constant(code, typeof(string)));
            var inArray = Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)],
                Expression.Property(t, nameof(Title.Countries)),
                Expression.Constant(code, typeof(string)));
            var perCode = Expression.OrElse(eqCountry, inArray);
            body = body is null ? perCode : Expression.OrElse(body, perCode);
        }
        return Expression.Lambda<Func<Title, bool>>(body!, t);
    }

    /// <summary>
    /// Жанрова overlap-умова: OR-ланцюг Any(g == g_i) — семантика && (перетин), побудована
    /// з GENRE_SETS-константи. Форма OR-of-single-Any — єдина, яку перекладають І Npgsql
    /// (EXISTS unnest), І тестовий InMemory (мульти-OR усередині одного Any InMemory не їсть).
    /// </summary>
    internal static Expression<Func<Title, bool>> GenresAnyPredicate(IReadOnlyList<string> genres)
    {
        var t = Expression.Parameter(typeof(Title), "t");
        Expression? body = null;
        foreach (var genre in genres)
        {
            var g = Expression.Parameter(typeof(string), "g");
            var any = Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Any), [typeof(string)],
                Expression.Property(t, nameof(Title.Genres)),
                Expression.Lambda<Func<string, bool>>(
                    Expression.Equal(g, Expression.Constant(genre, typeof(string))), g));
            body = body is null ? any : Expression.OrElse(body, any);
        }
        return Expression.Lambda<Func<Title, bool>>(body!, t);
    }

    private static IQueryable<Title> Sort(IQueryable<Title> query, string sort) => sort switch
    {
        "rating" => query.OrderByDescending(t => t.RatingTmdb ?? t.RatingImdb ?? 0)
            .ThenByDescending(t => t.Popularity),
        "imdb" => query.OrderByDescending(t => t.RatingImdb ?? 0).ThenByDescending(t => t.VotesImdb ?? 0),
        "votes" => query.OrderByDescending(t => t.VotesImdb ?? 0).ThenByDescending(t => t.RatingImdb ?? 0),
        "year" => query.OrderByDescending(t => t.Year ?? 0).ThenByDescending(t => t.Popularity),
        "popularity" => query.OrderByDescending(t => t.Popularity),
        // relevance: локальна релевантність — популярність як проксі (повнотекстовий рейтинг — Е2)
        _ => query.OrderByDescending(t => t.Popularity).ThenByDescending(t => t.Year ?? 0),
    };

    public static string EscapeLike(string s) => s
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
