using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>Результат sync TMDB watch/providers.</summary>
public sealed record TmdbProvidersSyncResult(int TitlesChecked, int Links);

/// <summary>
/// TMDB watch/providers — dormant до ключа (FR-14): env TMDB_API_KEY; дані JustWatch через
/// санкціонований канал TMDB (пряме парсення justwatch.com ЗАБОРОНЕНО).
/// find/{imdb_id}?external_source=imdb_id → /movie|tv/{id}/watch/providers, region=UA →
/// watch_links(provider=tmdb-streaming): flatrate→subscription, free→free, rent→rent, buy→buy.
/// Без ключа — SyncSkipException → контролер віддає 400 з підказкою; нічого не ламається.
/// </summary>
public sealed class TmdbProvidersService(
    TmdbClient tmdb,
    MovieDiscoveryDbContext db,
    ILogger<TmdbProvidersService> log)
{
    public const string Source = "tmdb-providers";
    public const string ProviderName = "tmdb-streaming";
    public const string Region = "UA";
    public const int DefaultLimit = 500;

    public async Task<TmdbProvidersSyncResult> SyncAsync(int limit, CancellationToken ct = default)
    {
        if (!tmdb.IsConfigured)
            throw new SyncSkipException(
                "TMDB_API_KEY не задано: зареєструй безкоштовний ключ на themoviedb.org (Settings → API) і пропиши TMDB_API_KEY у .env");

        var titles = await db.Titles.AsNoTracking()
            .Where(t => t.ImdbId != null && (t.Type == "movie" || t.Type == "series"))
            .OrderByDescending(t => t.Popularity)
            .Take(Math.Clamp(limit, 1, 10_000))
            .ToListAsync(ct);

        var existingLinks = await db.WatchLinks
            .Where(w => w.Provider == ProviderName)
            .ToListAsync(ct);

        var links = 0;
        var checkedCount = 0;
        foreach (var title in titles)
        {
            ct.ThrowIfCancellationRequested();
            var find = await tmdb.FindByImdbAsync(title.ImdbId!, ct);
            var tmdbId = find?.FirstId(title.Type);
            if (tmdbId is null) continue;

            var wp = title.Type == "movie"
                ? await tmdb.MovieWatchProvidersAsync(tmdbId.Value, ct)
                : await tmdb.TvWatchProvidersAsync(tmdbId.Value, ct);
            if (wp?.Results is null || !wp.Results.TryGetValue(Region, out var region) || region is null) continue;

            checkedCount++;
            foreach (var (list, kind) in TmdbProviderKindMapper.Lists(region))
            {
                foreach (var entry in list)
                {
                    var link = new WatchLink
                    {
                        TitleId = title.Id,
                        Provider = ProviderName,
                        Kind = kind,
                        Region = Region,
                        Url = region.Link,
                        IsAvailable = true,
                        CheckedAt = DateTime.UtcNow,
                    };
                    var (resolved, isNew) = WatchLinkUpsertCore.Resolve(
                        existingLinks.Where(w => w.TitleId == title.Id), link);
                    if (isNew)
                    {
                        db.WatchLinks.Add(resolved);
                        existingLinks.Add(resolved);
                    }
                    else WatchLinkUpsertCore.Merge(resolved, link);
                    links++;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await UpdateSyncStateAsync(checkedCount, ct);
        log.LogInformation("TMDB providers sync: тайтлів={Checked}, посилань={Links}", checkedCount, links);
        return new TmdbProvidersSyncResult(checkedCount, links);
    }

    private async Task UpdateSyncStateAsync(int checkedCount, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "providers", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "providers" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = checkedCount;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Мапінг списків провайдерів TMDB → kind watch_links (чиста функція — тести).</summary>
public static class TmdbProviderKindMapper
{
    /// <summary>flatrate → subscription, free → free, rent → rent, buy → buy.</summary>
    public static string? Map(string list) => list switch
    {
        "flatrate" => "subscription",
        "free" => "free",
        "rent" => "rent",
        "buy" => "buy",
        _ => null,
    };

    /// <summary>Списки регіону у стабільному порядку з kind за мапою.</summary>
    public static IEnumerable<(List<TmdbProviderEntry> Entries, string Kind)> Lists(TmdbWatchRegion region)
    {
        foreach (var (name, entries) in new (string, List<TmdbProviderEntry>?)[]
                 {
                     ("free", region.Free),
                     ("flatrate", region.Flatrate),
                     ("rent", region.Rent),
                     ("buy", region.Buy),
                 })
        {
            var kind = Map(name);
            if (kind is null || entries is null) continue;
            var ordered = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.ProviderName))
                .OrderBy(e => e.DisplayPriority)
                .ToList();
            if (ordered.Count > 0) yield return (ordered, kind);
        }
    }
}
