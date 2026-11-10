using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Embeddings;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Sync;

/// <summary>
/// Фонова синхронізація: тягне TMDB popular + discover батчами (uk + en), upsert у titles,
/// ставить тайтли у чергу на embeddings (якщо Ollama жива), оновлює sync_state.
/// </summary>
public sealed class SyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<SyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<SyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка синхронізації (pages={Pages}, type={Type})", job.Pages, job.Type);
            }
        }
    }

    private async Task RunJobAsync(SyncJob job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var tmdb = scope.ServiceProvider.GetRequiredService<TmdbClient>();
        var cache = scope.ServiceProvider.GetRequiredService<ICacheStore>();
        var embeddingQueue = scope.ServiceProvider.GetRequiredService<EmbeddingQueue>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbedder>();

        if (!tmdb.IsConfigured)
        {
            log.LogWarning("TMDB_API_KEY не задано — синхронізація неможлива");
            return;
        }

        var types = job.Type switch
        {
            "movie" => ["movie"],
            "series" => ["series"],
            _ => new[] { "movie", "series" },
        };

        var ollamaAlive = await embedder.IsAvailableAsync(ct);
        if (!ollamaAlive)
            log.LogInformation("Ollama недоступна — вектори не ставляться у чергу (метадані все одно імпортуються)");

        var uk = "uk-UA";
        var en = "en-US";
        var pages = Math.Clamp(job.Pages, 1, 500);
        var totalUpserted = 0;

        foreach (var type in types)
        {
            for (var page = 1; page <= pages; page++)
            {
                ct.ThrowIfCancellationRequested();

                if (type == "movie")
                {
                    var ukPop = await tmdb.MoviePopularAsync(page, uk, ct);
                    var enPop = await tmdb.MoviePopularAsync(page, en, ct);
                    var ukDisc = await tmdb.DiscoverMoviesAsync(page, uk, ct);
                    var enDisc = await tmdb.DiscoverMoviesAsync(page, en, ct);

                    var baseSummaries = new Dictionary<int, TmdbMovieSummary>();
                    var ukSummaries = new Dictionary<int, TmdbMovieSummary>();
                    foreach (var s in Concat(enPop?.Results, enDisc?.Results))
                        baseSummaries.TryAdd(s.Id, s);
                    foreach (var s in Concat(ukPop?.Results, ukDisc?.Results))
                        ukSummaries.TryAdd(s.Id, s);

                    var (upserted, localIds) = await UpsertMovieSummariesAsync(db, baseSummaries, ukSummaries, ct);
                    totalUpserted += upserted;
                    if (ollamaAlive)
                        foreach (var localId in localIds) embeddingQueue.TryEnqueue(localId);
                }
                else
                {
                    var ukPop = await tmdb.TvPopularAsync(page, uk, ct);
                    var enPop = await tmdb.TvPopularAsync(page, en, ct);
                    var ukDisc = await tmdb.DiscoverTvAsync(page, uk, ct);
                    var enDisc = await tmdb.DiscoverTvAsync(page, en, ct);

                    var baseSummaries = new Dictionary<int, TmdbTvSummary>();
                    var ukSummaries = new Dictionary<int, TmdbTvSummary>();
                    foreach (var s in Concat(enPop?.Results, enDisc?.Results))
                        baseSummaries.TryAdd(s.Id, s);
                    foreach (var s in Concat(ukPop?.Results, ukDisc?.Results))
                        ukSummaries.TryAdd(s.Id, s);

                    var (upserted, localIds) = await UpsertTvSummariesAsync(db, baseSummaries, ukSummaries, ct);
                    totalUpserted += upserted;
                    if (ollamaAlive)
                        foreach (var localId in localIds) embeddingQueue.TryEnqueue(localId);
                }
            }
        }

        // sync_state upsert
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == TmdbCacheKeys.Source && s.Kind == "popular", ct);
        if (state is null)
        {
            state = new SyncState { Source = TmdbCacheKeys.Source, Kind = "popular" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastPages = pages;
        state.LastCount = totalUpserted;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Синхронізація завершена: type={Type}, pages={Pages}, тайтлів оброблено {Count}",
            job.Type, pages, totalUpserted);
    }

    private static IEnumerable<T> Concat<T>(params IEnumerable<T>?[] lists)
    {
        foreach (var list in lists)
        {
            if (list is null) continue;
            foreach (var item in list) yield return item;
        }
    }

    /// <summary>Upsert фільмів: en — база, uk — оверлей. Повертає (кількість, локальні id).</summary>
    private static async Task<(int Upserted, List<int> LocalIds)> UpsertMovieSummariesAsync(
        MovieDiscoveryDbContext db,
        Dictionary<int, TmdbMovieSummary> baseSummaries,
        Dictionary<int, TmdbMovieSummary> ukSummaries,
        CancellationToken ct)
    {
        if (baseSummaries.Count == 0) return (0, []);
        var tmdbIds = baseSummaries.Keys.ToList();
        var existing = await db.Titles
            .Where(t => tmdbIds.Contains(t.TmdbId!.Value) && t.Type == "movie")
            .ToDictionaryAsync(t => t.TmdbId!.Value, ct);

        var touched = new List<Title>();
        foreach (var (tmdbId, summary) in baseSummaries)
        {
            if (!existing.TryGetValue(tmdbId, out var title))
            {
                title = new Title { TmdbId = tmdbId, Type = "movie", CreatedAt = DateTime.UtcNow };
                db.Titles.Add(title);
                existing[tmdbId] = title;
            }
            TmdbMapper.ApplyMovieSummary(title, summary, ukSummaries.GetValueOrDefault(tmdbId));
            touched.Add(title);
        }

        await db.SaveChangesAsync(ct);
        return (touched.Count, touched.Select(t => t.Id).ToList());
    }

    /// <summary>Upsert серіалів: en — база, uk — оверлей. Повертає (кількість, локальні id).</summary>
    private static async Task<(int Upserted, List<int> LocalIds)> UpsertTvSummariesAsync(
        MovieDiscoveryDbContext db,
        Dictionary<int, TmdbTvSummary> baseSummaries,
        Dictionary<int, TmdbTvSummary> ukSummaries,
        CancellationToken ct)
    {
        if (baseSummaries.Count == 0) return (0, []);
        var tmdbIds = baseSummaries.Keys.ToList();
        var existing = await db.Titles
            .Where(t => tmdbIds.Contains(t.TmdbId!.Value) && t.Type == "series")
            .ToDictionaryAsync(t => t.TmdbId!.Value, ct);

        var touched = new List<Title>();
        foreach (var (tmdbId, summary) in baseSummaries)
        {
            if (!existing.TryGetValue(tmdbId, out var title))
            {
                title = new Title { TmdbId = tmdbId, Type = "series", CreatedAt = DateTime.UtcNow };
                db.Titles.Add(title);
                existing[tmdbId] = title;
            }
            TmdbMapper.ApplyTvSummary(title, summary, ukSummaries.GetValueOrDefault(tmdbId));
            touched.Add(title);
        }

        await db.SaveChangesAsync(ct);
        return (touched.Count, touched.Select(t => t.Id).ToList());
    }
}
