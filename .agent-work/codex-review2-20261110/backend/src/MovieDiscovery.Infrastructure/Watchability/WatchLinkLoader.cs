using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>Компактне посилання для бейджів картки (тільки is_available=true).</summary>
public sealed record CardWatchLink(string Provider, string Kind, string? Url);

/// <summary>
/// Батч-завантаження доступних watch_links для карток (TitleCard.watchProviders, FR-14):
/// один запит на чанк id, лише is_available=true.
/// </summary>
public static class WatchLinkLoader
{
    public static async Task<Dictionary<int, List<CardWatchLink>>> LoadAvailableAsync(
        MovieDiscoveryDbContext db, IEnumerable<int> titleIds, CancellationToken ct = default)
    {
        var ids = titleIds.Distinct().ToList();
        var result = new Dictionary<int, List<CardWatchLink>>();
        foreach (var chunk in ids.Chunk(500))
        {
            var set = chunk.ToHashSet();
            var links = await db.WatchLinks.AsNoTracking()
                .Where(w => w.IsAvailable && set.Contains(w.TitleId))
                .Select(w => new { w.TitleId, w.Provider, w.Kind, w.Url })
                .ToListAsync(ct);
            foreach (var l in links)
            {
                if (!result.TryGetValue(l.TitleId, out var list)) result[l.TitleId] = list = [];
                list.Add(new CardWatchLink(l.Provider, l.Kind, l.Url));
            }
        }
        return result;
    }
}
