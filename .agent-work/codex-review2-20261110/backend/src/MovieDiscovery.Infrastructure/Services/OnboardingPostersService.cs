using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>
/// Постери для варіантів онбординг-питань (FR-18): для кожного жанру/десятиліття —
/// найпопулярніший тайтл з непорожнім постером. IMemoryCache 10 хв (питання однакові
/// для всіх користувачів; ендпоінт /questions за [Authorize] викликається на кожен онбординг).
/// Запити дешеві: GIN ix_titles_genres_gin / ix_titles_year + LIMIT 1.
/// </summary>
public sealed class OnboardingPostersService(MovieDiscoveryDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Постер жанру: топ-популярний тайтл з genres @&gt; {key} і poster_path.</summary>
    public async Task<string?> GenrePosterAsync(string genreKey, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync($"onb:poster:g:{genreKey}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheTtl;
            var url = await db.Titles
                .Where(t => t.Genres.Any(g => g == genreKey) && t.PosterPath != null && t.PosterPath != "")
                .OrderByDescending(t => t.Popularity)
                .Select(t => t.PosterPath)
                .FirstOrDefaultAsync(ct);
            return Url(url);
        }) ?? null;

    /// <summary>Постер десятиліття: топ-популярний тайтл з year у [from, from+9] і постером.</summary>
    public async Task<string?> EraPosterAsync(string decade, CancellationToken ct = default)
    {
        if (!int.TryParse(decade.AsSpan(0, 4), out var from)) return null;
        var to = from + 9;
        return await cache.GetOrCreateAsync($"onb:poster:e:{decade}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheTtl;
            var url = await db.Titles
                .Where(t => t.Year >= from && t.Year <= to && t.PosterPath != null && t.PosterPath != "")
                .OrderByDescending(t => t.Popularity)
                .Select(t => t.PosterPath)
                .FirstOrDefaultAsync(ct);
            return Url(url);
        }) ?? null;
    }

    /// <summary>Заповнити posterUrl усіх варіантів усіх питань (posters/mood — жанр, era — десятиліття).</summary>
    public async Task EnrichAsync(List<OnboardingQuestionDto> questions, CancellationToken ct = default)
    {
        foreach (var q in questions)
        {
            foreach (var o in q.Options)
            {
                o.PosterUrl = q.Type == "era"
                    ? await EraPosterAsync(o.Key, ct)
                    : await GenrePosterAsync(o.Key, ct);
            }
        }
    }

    private static string? Url(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path
                : $"https://image.tmdb.org/t/p/w300{path}";
}
