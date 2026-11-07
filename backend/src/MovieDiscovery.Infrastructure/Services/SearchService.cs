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
    int PageSize);

public sealed record SearchResult(IReadOnlyList<Title> Items, int Total, int Page, int PageSize);

/// <summary>
/// Пошук/фільтри ЛИШЕ по локальній БД (NFR-1: жодних зовнішніх викликів у hot path).
/// q шукає в назвах uk/en, акторах і режисері (ILIKE).
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

        if (q.YearFrom is { } yFrom)
            query = query.Where(t => t.Year >= yFrom);
        if (q.YearTo is { } yTo)
            query = query.Where(t => t.Year <= yTo);

        if (!string.IsNullOrWhiteSpace(q.Country))
            query = query.Where(t => t.Countries.Contains(q.Country));

        if (q.MinRating is { } minR)
            query = query.Where(t => (t.RatingTmdb ?? t.RatingImdb) >= minR);
        if (q.MaxRating is { } maxR)
            query = query.Where(t => (t.RatingTmdb ?? t.RatingImdb) <= maxR);

        query = ApplyRuntimeFilter(query, q);

        if (!string.IsNullOrWhiteSpace(q.Status))
            query = query.Where(t => t.Status == q.Status);

        var total = await query.CountAsync(ct);

        query = Sort(query, q.Sort);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new SearchResult(items, total, page, pageSize);
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
