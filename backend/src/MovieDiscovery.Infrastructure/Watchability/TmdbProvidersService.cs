using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Watchability;

public sealed record TmdbProvidersSyncResult(int TitlesChecked, int Links);

/// <summary>JustWatch availability by country, supplied by the official TMDB API.</summary>
public sealed class TmdbProvidersService(
    TmdbClient tmdb,
    MovieDiscoveryDbContext db,
    ILogger<TmdbProvidersService> log,
    IOptions<TmdbOptions>? options = null)
{
    public const string Source = "tmdb-providers";
    public const string LegacyProviderName = "tmdb-streaming";
    public const int DefaultLimit = 500;
    internal static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(250);

    // The admin jobs and title requests share one external-request worker.
    public async Task<TmdbProvidersSyncResult> SyncAsync(int limit, CancellationToken ct = default)
    {
        if (!tmdb.IsConfigured)
            throw new SyncSkipException(
                "TMDB_API_KEY не задано: зареєструй безкоштовний ключ на themoviedb.org (Settings → API) і пропиши TMDB_API_KEY у .env");

        var titles = await EligibleTitles()
            .OrderByDescending(t => t.Popularity)
            .ThenBy(t => t.Id)
            .Take(Math.Clamp(limit, 1, 10_000))
            .ToListAsync(ct);

        var (result, _) = await SyncTitlesAsync(titles, ct);
        await SaveStateAsync("providers", result.TitlesChecked, null, ct);
        return result;
    }

    public async Task SweepAsync(int limit, CancellationToken ct)
    {
        if (!tmdb.IsConfigured) return;

        var cursor = await db.SyncStates.AsNoTracking()
            .Where(s => s.Source == Source && s.Kind == "watch-sweep")
            .Select(s => s.LastPages)
            .SingleOrDefaultAsync(ct) ?? 0;
        var activeLimit = Math.Clamp(options?.Value.WatchActiveTitleLimit ?? 12000, 1, 100_000);
        var recent = DateTime.UtcNow.AddDays(-30);
        var fresh = DateTime.UtcNow.AddHours(-6);
        var popular = EligibleTitles().OrderByDescending(t => t.Popularity).ThenBy(t => t.Id).Take(activeLimit * 3 / 4)
            .Select(t => t.Id);
        var recentlyUpdated = EligibleTitles().Where(t => t.UpdatedAt >= recent)
            .OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id)
            .Take(activeLimit - activeLimit * 3 / 4).Select(t => t.Id);
        var activeIds = popular.Union(recentlyUpdated);
        var titles = await EligibleTitles().Where(t => t.Id > cursor && activeIds.Contains(t.Id) &&
                !db.Providers.Any(p => p.TitleId == t.Id && p.Source == Source && p.UpdatedAt > fresh))
            .OrderBy(t => t.Id).Take(limit).ToListAsync(ct);
        var (result, lastTitleId) = await SyncTitlesAsync(titles, ct);
        await SaveStateAsync("watch-sweep", result.TitlesChecked, titles.Count == 0 ? 0 : lastTitleId ?? cursor, ct);
    }

    private IQueryable<Title> EligibleTitles() => db.Titles.AsNoTracking().Where(t =>
        (t.TmdbId > 0 || (t.ImdbId != null && t.ImdbId.StartsWith("tt"))) && t.Type != "manga");

    private async Task SaveStateAsync(string kind, int checkedCount, int? cursor, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == kind, ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = kind };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = checkedCount;
        state.LastPages = cursor;
        await db.SaveChangesAsync(ct);
    }

    private async Task<(TmdbProvidersSyncResult Result, int? LastTitleId)> SyncTitlesAsync(
        List<Title> titles, CancellationToken ct)
    {
        var links = 0;
        var checkedCount = 0;
        var consecutiveFailures = 0;
        int? lastTitleId = null;
        foreach (var title in titles)
        {
            ct.ThrowIfCancellationRequested();
            var count = await SyncTitleAsync(title, ct);
            if (count is { } rows)
            {
                checkedCount++;
                links += rows;
                consecutiveFailures = 0;
                lastTitleId = title.Id;
            }
            else if (++consecutiveFailures >= 3)
            {
                log.LogWarning("TMDB watch providers returned three consecutive failures; pausing the batch");
                break;
            }
            await Task.Delay(RequestSpacing, ct);
        }
        log.LogInformation("TMDB providers sync: titles={Checked}, offers={Links}", checkedCount, links);
        return (new TmdbProvidersSyncResult(checkedCount, links), lastTitleId);
    }

    public async Task<int?> SyncTitleAsync(int titleId, CancellationToken ct)
    {
        var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == titleId, ct);
        return title is null ? 0 : await SyncTitleAsync(title, ct);
    }

    public static bool CanDiscover(string type, int? tmdbId, string? imdbId) =>
        type != "manga" && (tmdbId is > 0 || IsImdbTitleId(imdbId));

    private static bool IsImdbTitleId(string? id) => id is { Length: > 2 } &&
        id.StartsWith("tt", StringComparison.Ordinal) && id.Skip(2).All(char.IsAsciiDigit);

    // Null represents an upstream failure. A valid empty result clears the old TMDB snapshot.
    private async Task<int?> SyncTitleAsync(Title title, CancellationToken ct)
    {
        if (!tmdb.IsConfigured || !CanDiscover(title.Type, title.TmdbId, title.ImdbId)) return 0;

        var isMovie = title.Type == "movie" || (title.Type == "anime" && title.Format == "movie");
        var tmdbId = title.TmdbId;
        if (tmdbId is null or <= 0)
        {
            var find = await tmdb.FindByImdbAsync(title.ImdbId!, ct);
            if (find is null) return null;
            var movies = find.MovieResults;
            var series = find.TvResults;
            // IMDb identifiers should be unique. Avoid linking an ambiguous external match.
            if (movies.Count + series.Count != 1) return 0;
            isMovie = movies.Count == 1;
            tmdbId = isMovie ? movies[0].Id : series[0].Id;
            if (tmdbId <= 0) return 0;
        }

        var snapshot = isMovie
            ? await tmdb.MovieWatchProvidersAsync(tmdbId.Value, ct)
            : await tmdb.TvWatchProvidersAsync(tmdbId.Value, ct);
        if (snapshot?.Results is null) return null;

        var now = DateTime.UtcNow;
        var fresh = snapshot.Results.Keys
            .Where(region => region.Length == 2 && region.All(char.IsAsciiLetterUpper))
            .SelectMany(region => TmdbMapper.ExtractProviders(title.Id, snapshot, region, now))
            .ToDictionary(p => (p.ProviderName, p.Kind, p.Region));

        var existing = await db.Providers
            .Where(p => p.TitleId == title.Id && (p.Source == Source ||
                (p.Source == "justwatch" && p.Url != null &&
                    (p.Url.StartsWith("https://www.themoviedb.org/") || p.Url.StartsWith("https://themoviedb.org/")))))
            .ToListAsync(ct);
        var count = fresh.Count;
        foreach (var row in existing)
        {
            if (row.Source != Source || !fresh.Remove((row.ProviderName, row.Kind, row.Region), out var offer))
            {
                db.Providers.Remove(row);
                continue;
            }

            row.Url = offer.Url;
            row.UpdatedAt = now;
        }

        db.Providers.AddRange(fresh.Values);
        await db.SaveChangesAsync(ct);
        foreach (var row in existing.Concat(fresh.Values))
            db.Entry(row).State = EntityState.Detached;
        return count;
    }
}

public static class TmdbProviderKindMapper
{
    public static string? Map(string list) => list switch
    {
        "flatrate" => "subscription",
        "free" or "ads" or "rent" or "buy" => list,
        _ => null,
    };

    public static IEnumerable<(List<TmdbProviderEntry> Entries, string Kind)> Lists(TmdbWatchRegion region)
    {
        foreach (var (name, entries) in new (string, List<TmdbProviderEntry>?)[]
                 {
                     ("free", region.Free),
                     ("ads", region.Ads),
                     ("flatrate", region.Flatrate),
                     ("rent", region.Rent),
                     ("buy", region.Buy),
                 })
        {
            if (entries is null) continue;
            var ordered = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.ProviderName))
                .OrderBy(e => e.DisplayPriority)
                .ToList();
            if (ordered.Count > 0) yield return (ordered, Map(name)!);
        }
    }
}
