using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Services;

public sealed record TitleDetail(Title Title, List<Provider> Providers);

/// <summary>
/// Деталі тайтла з on-demand збагаченням: якщо в БД нема credits/keywords/трейлера/провайдерів —
/// перший запит тягне TMDB (через безстроковий кеш) і догортає запис (NFR-3).
/// Зовнішня недоступність не ламає відповідь — віддаємо те, що в БД.
/// </summary>
public sealed class TitleService(
    MovieDiscoveryDbContext db,
    TmdbClient tmdb,
    OmdbClient omdb,
    ILogger<TitleService> log)
{
    public async Task<Title?> ResolveAsync(int id, CancellationToken ct = default)
    {
        // спершу локальний id, потім tmdb id (взаємна сумісність для клієнтів)
        var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
                    ?? await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.TmdbId == id, ct);
        return title;
    }

    public async Task<TitleDetail?> GetDetailAsync(int id, string lang, CancellationToken ct = default)
    {
        var title = await ResolveAsync(id, ct);
        if (title is null) return null;

        var providers = await LoadProvidersAsync(title.Id, "UA", ct);

        if (NeedsEnrichment(title) && tmdb.IsConfigured)
        {
            await EnrichAsync(title, ct);
            providers = await LoadProvidersAsync(title.Id, "UA", ct);

            // OMDb (опційно): рейтинг IMDb для тайтла, якого ще нема
            if (title.RatingImdb is null && omdb.IsConfigured && !string.IsNullOrWhiteSpace(title.TitleEn))
            {
                try
                {
                    var omdbResult = await omdb.GetByTitleAsync(title.TitleEn, title.Year, ct);
                    if (omdbResult is not null)
                    {
                        var tracked = await db.Titles.FirstAsync(t => t.Id == title.Id, ct);
                        tracked.RatingImdb = omdbResult.ImdbRating;
                        tracked.VotesImdb = omdbResult.ImdbVotes;
                        tracked.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "OMDb збагачення не вдалося для title_id={Id}", title.Id);
                }
            }

            title = await db.Titles.AsNoTracking().FirstAsync(t => t.Id == title.Id, ct);
        }

        return new TitleDetail(title, providers);
    }

    public async Task<List<Provider>> GetProvidersAsync(Title title, string region, CancellationToken ct = default)
    {
        var list = await LoadProvidersAsync(title.Id, region, ct);
        if (list.Count > 0 || !tmdb.IsConfigured || title.TmdbId is not int tmdbId) return list;

        // on-demand: перший запит тягне watch/providers (через кеш), зберігає назавжди
        try
        {
            var isMovie = title.Type == "movie";
            var en = isMovie
                ? await tmdb.MovieDetailsAsync(tmdbId, "en-US", ct)
                : null;
            var tv = !isMovie ? await tmdb.TvDetailsAsync(tmdbId, "en-US", ct) : null;
            var wp = en?.WatchProviders ?? tv?.WatchProviders;
            if (wp is null) return list;

            var fresh = TmdbMapper.ExtractProviders(title.Id, wp, region);
            if (fresh.Count > 0)
            {
                db.Providers.AddRange(fresh);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Провайдери on-demand не вдалося (title_id={Id}, region={Region})", title.Id, region);
        }

        return await LoadProvidersAsync(title.Id, region, ct);
    }

    private Task<List<Provider>> LoadProvidersAsync(int titleId, string region, CancellationToken ct) =>
        db.Providers.AsNoTracking()
            .Where(p => p.TitleId == titleId && p.Region == region)
            .OrderBy(p => p.ProviderName)
            .ToListAsync(ct);

    private static bool NeedsEnrichment(Title t) =>
        t.TmdbId is not null && // аніме/манга (без tmdb_id) не збагачуються з TMDB
        (t.Actors.Length == 0 || t.Keywords.Length == 0 || t.TrailerYtId is null);

    private async Task EnrichAsync(Title title, CancellationToken ct)
    {
        if (title.TmdbId is not int tmdbId) return;
        try
        {
            var tracked = await db.Titles.FirstAsync(t => t.Id == title.Id, ct);
            var isMovie = title.Type == "movie";
            var uk = "uk-UA";
            var en = "en-US";

            List<Provider> freshProviders = [];
            if (isMovie)
            {
                var enD = await tmdb.MovieDetailsAsync(tmdbId, en, ct);
                var ukD = await tmdb.MovieDetailsAsync(tmdbId, uk, ct);
                if (enD is null) return;
                TmdbMapper.ApplyMovieDetails(tracked, enD, ukD);
                freshProviders = TmdbMapper.ExtractProviders(title.Id, enD.WatchProviders, "UA");
            }
            else
            {
                var enD = await tmdb.TvDetailsAsync(tmdbId, en, ct);
                var ukD = await tmdb.TvDetailsAsync(tmdbId, uk, ct);
                if (enD is null) return;
                TmdbMapper.ApplyTvDetails(tracked, enD, ukD);
                freshProviders = TmdbMapper.ExtractProviders(title.Id, enD.WatchProviders, "UA");
            }

            if (freshProviders.Count > 0)
            {
                var existing = await db.Providers
                    .Where(p => p.TitleId == title.Id && p.Region == "UA")
                    .Select(p => new { p.ProviderName, p.Kind })
                    .ToListAsync(ct);
                var known = existing.Select(p => (p.ProviderName, p.Kind)).ToHashSet();
                db.Providers.AddRange(freshProviders
                    .Where(p => !known.Contains((p.ProviderName, p.Kind))));
            }

            tracked.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "On-demand збагачення не вдалося для title_id={Id} — віддаю дані з БД", title.Id);
        }
    }
}
