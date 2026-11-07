using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
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
    string ProviderRegion = "UA");

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

        // country: одна або кілька кодів через кому — збіг з будь-яким
        if (!string.IsNullOrWhiteSpace(q.Country))
        {
            var countries = q.Country.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (countries.Length == 1)
                query = query.Where(t => t.Countries.Contains(countries[0]));
            else if (countries.Length > 1)
                query = query.Where(t => t.Countries.Any(c => countries.Contains(c)));
        }

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

        var total = await query.CountAsync(ct);

        query = Sort(query, q.Sort);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new SearchResult(items, total, page, pageSize);
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
