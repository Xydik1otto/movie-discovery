using System.Data;
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
using Npgsql;

namespace MovieDiscovery.Infrastructure.Sync;

public sealed class CatalogSyncOptions
{
    public int TmdbIntervalHours { get; set; }
    public int TmdbPages { get; set; } = 5;
}

/// <summary>
/// Фонова синхронізація: тягне TMDB popular + discover батчами (uk + en), upsert у titles,
/// ставить тайтли у чергу на embeddings (якщо Ollama жива), оновлює sync_state.
/// </summary>
public sealed class SyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<CatalogSyncOptions> options,
    ILogger<SyncBackgroundService> log)
    : BackgroundService
{
    internal const string ScheduledKind = "scheduled-popular";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var startupScope = scopeFactory.CreateScope();
        var queue = startupScope.ServiceProvider.GetRequiredService<SyncQueue>();
        await Task.WhenAll(ConsumeAsync(queue, stoppingToken), ScheduleAsync(queue, stoppingToken));
    }

    private async Task ConsumeAsync(SyncQueue queue, CancellationToken stoppingToken)
    {
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

    private async Task ScheduleAsync(SyncQueue queue, CancellationToken ct)
    {
        var config = options.Value;
        if (config.TmdbIntervalHours <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            if (queue.Count == 0)
                queue.TryEnqueue(new SyncJob(config.TmdbPages, "all", Automatic: true));
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    internal async Task RunJobAsync(SyncJob job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var tmdb = scope.ServiceProvider.GetRequiredService<TmdbClient>();
        var embeddingQueue = scope.ServiceProvider.GetRequiredService<EmbeddingQueue>();
        var embedder = scope.ServiceProvider.GetRequiredService<IEmbedder>();

        if (!tmdb.IsConfigured)
        {
            if (!job.Automatic)
                log.LogWarning("TMDB_API_KEY не задано — синхронізація неможлива");
            return;
        }

        var pages = Math.Clamp(job.Pages, 1, 500);
        SyncState? scheduledState = null;
        if (job.Automatic)
        {
            scheduledState = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == TmdbCacheKeys.Source && s.Kind == ScheduledKind, ct);
            if (scheduledState?.LastRunAt is { } lastRun && scheduledState.LastPages == pages &&
                DateTime.UtcNow - lastRun < TimeSpan.FromHours(options.Value.TmdbIntervalHours))
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
        var totalUpserted = 0;
        var complete = true;

        foreach (var type in types)
        {
            var seenIds = new HashSet<int>();
            for (var page = 1; page <= pages; page++)
            {
                ct.ThrowIfCancellationRequested();

                if (type == "movie")
                {
                    var ukPop = await tmdb.MoviePopularAsync(page, uk, ct);
                    var enPop = await tmdb.MoviePopularAsync(page, en, ct);
                    var ukDisc = await tmdb.DiscoverMoviesAsync(page, uk, ct);
                    var enDisc = await tmdb.DiscoverMoviesAsync(page, en, ct);
                    complete &= enPop is not null && enDisc is not null && ukPop is not null && ukDisc is not null;

                    var baseSummaries = new Dictionary<int, TmdbMovieSummary>();
                    var ukSummaries = new Dictionary<int, TmdbMovieSummary>();
                    foreach (var s in Concat(enPop?.Results, enDisc?.Results))
                        if (s.Id > 0 && seenIds.Add(s.Id)) baseSummaries.Add(s.Id, s);
                    foreach (var s in Concat(ukPop?.Results, ukDisc?.Results))
                        ukSummaries.TryAdd(s.Id, s);

                    var (localIds, identitiesComplete) = await UpsertSummariesAsync(
                        tmdb, type, baseSummaries, ukSummaries, TmdbMapper.ApplyMovieSummary, ct);
                    complete &= identitiesComplete;
                    totalUpserted += localIds.Count;
                    if (ollamaAlive)
                        foreach (var localId in localIds) embeddingQueue.TryEnqueue(localId);
                }
                else
                {
                    var ukPop = await tmdb.TvPopularAsync(page, uk, ct);
                    var enPop = await tmdb.TvPopularAsync(page, en, ct);
                    var ukDisc = await tmdb.DiscoverTvAsync(page, uk, ct);
                    var enDisc = await tmdb.DiscoverTvAsync(page, en, ct);
                    complete &= enPop is not null && enDisc is not null && ukPop is not null && ukDisc is not null;

                    var baseSummaries = new Dictionary<int, TmdbTvSummary>();
                    var ukSummaries = new Dictionary<int, TmdbTvSummary>();
                    foreach (var s in Concat(enPop?.Results, enDisc?.Results))
                        if (s.Id > 0 && seenIds.Add(s.Id)) baseSummaries.Add(s.Id, s);
                    foreach (var s in Concat(ukPop?.Results, ukDisc?.Results))
                        ukSummaries.TryAdd(s.Id, s);

                    var (localIds, identitiesComplete) = await UpsertSummariesAsync(
                        tmdb, type, baseSummaries, ukSummaries, TmdbMapper.ApplyTvSummary, ct);
                    complete &= identitiesComplete;
                    totalUpserted += localIds.Count;
                    if (ollamaAlive)
                        foreach (var localId in localIds) embeddingQueue.TryEnqueue(localId);
                }
            }
            complete &= seenIds.Count > 0;
        }

        if (job.Automatic && (!complete || totalUpserted == 0))
            throw new InvalidDataException("Запланована синхронізація TMDB отримала неповну або порожню відповідь.");

        var kind = job.Automatic ? ScheduledKind : "popular";
        var state = job.Automatic ? scheduledState :
            await db.SyncStates.FirstOrDefaultAsync(s => s.Source == TmdbCacheKeys.Source && s.Kind == kind, ct);
        if (state is null)
        {
            state = new SyncState { Source = TmdbCacheKeys.Source, Kind = kind };
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

    private async Task<(List<int> LocalIds, bool IdentitiesComplete)> UpsertSummariesAsync<TSummary>(
        TmdbClient tmdb,
        string type,
        Dictionary<int, TSummary> baseSummaries,
        Dictionary<int, TSummary> ukSummaries,
        Action<Title, TSummary, TSummary?> applySummary,
        CancellationToken ct) where TSummary : class
    {
        if (baseSummaries.Count == 0) return ([], true);

        var externalIds = new Dictionary<int, string?>();
        foreach (var tmdbId in baseSummaries.Keys)
        {
            var external = await tmdb.ExternalIdsAsync(type, tmdbId, ct);
            if (external?.Id != tmdbId || external.ImdbId is { Length: > 0 } rawId && !IsImdbId(rawId))
            {
                log.LogWarning("TMDB external ID unavailable for {Type} {TmdbId}", type, tmdbId);
                continue;
            }
            externalIds.Add(tmdbId, string.IsNullOrEmpty(external.ImdbId) ? null : external.ImdbId);
        }

        if (externalIds.Count == 0) return ([], false);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var localIds = await SaveSummariesAsync(type, baseSummaries, ukSummaries, externalIds, applySummary, ct);
                return (localIds, externalIds.Count == baseSummaries.Count);
            }
            catch (Exception ex) when (attempt < 3 && IsConcurrentCatalogWrite(ex.GetBaseException()))
            {
                log.LogWarning("Concurrent {Type} catalog write during TMDB sync; retrying batch ({Attempt}/3)", type, attempt);
            }
        }
    }

    private async Task<List<int>> SaveSummariesAsync<TSummary>(
        string type,
        Dictionary<int, TSummary> baseSummaries,
        Dictionary<int, TSummary> ukSummaries,
        Dictionary<int, string?> externalIds,
        Action<Title, TSummary, TSummary?> applySummary,
        CancellationToken ct) where TSummary : class
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        // IMDb may reclassify an unlinked row while TMDB is reading its identity.
        await using var transaction = db.Database.IsNpgsql()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;
        var tmdbIds = externalIds.Keys.ToArray();
        var byTmdbId = await db.Titles
            .Where(t => t.TmdbId.HasValue && tmdbIds.Contains(t.TmdbId.Value) && t.Type == type)
            .ToDictionaryAsync(t => t.TmdbId!.Value, ct);
        var imdbIds = externalIds.Values.OfType<string>().Distinct().ToArray();
        var byImdbId = imdbIds.Length == 0 ? new Dictionary<string, Title>(StringComparer.Ordinal) : await db.Titles
            .Where(t => t.ImdbId != null && imdbIds.Contains(t.ImdbId))
            .ToDictionaryAsync(t => t.ImdbId!, StringComparer.Ordinal, ct);

        var touched = new List<Title>(baseSummaries.Count);
        foreach (var (tmdbId, summary) in baseSummaries)
        {
            if (!externalIds.TryGetValue(tmdbId, out var imdbId)) continue;

            var tmdbTitle = byTmdbId.GetValueOrDefault(tmdbId);
            var imdbTitle = imdbId is null ? null : byImdbId.GetValueOrDefault(imdbId);
            if ((imdbTitle is not null && (imdbTitle.Type != type ||
                    (imdbTitle.TmdbId is { } existingId && existingId != tmdbId) ||
                    (tmdbTitle is not null && tmdbTitle.Id != imdbTitle.Id))) ||
                (imdbId is not null && tmdbTitle?.ImdbId is { } existingImdbId && existingImdbId != imdbId))
            {
                log.LogWarning("TMDB catalog identity conflict for {Type} {TmdbId}, IMDb {ImdbId}, TMDB row {TmdbRow}, IMDb row {ImdbRow}",
                    type, tmdbId, imdbId, tmdbTitle?.Id, imdbTitle?.Id);
                continue;
            }

            var title = tmdbTitle ?? imdbTitle;
            if (title is null)
            {
                // Новий запис без спільного ID може дублювати запис з IMDb.
                if (imdbId is null) continue;
                title = new Title { Type = type, ImdbId = imdbId };
                db.Titles.Add(title);
            }
            if (imdbId is not null)
            {
                title.ImdbId = imdbId;
                byImdbId[imdbId] = title;
            }
            applySummary(title, summary, ukSummaries.GetValueOrDefault(tmdbId));
            touched.Add(title);
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return touched.Select(t => t.Id).ToList();
    }

    private static bool IsConcurrentCatalogWrite(Exception ex) => ex is PostgresException pg &&
        (pg.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected ||
         pg.SqlState == PostgresErrorCodes.UniqueViolation &&
         pg.ConstraintName is "ix_titles_imdb_id" or "ix_titles_tmdb_id_type");

    private static bool IsImdbId(string value) => value.Length is >= 3 and <= 16 &&
        value.StartsWith("tt", StringComparison.Ordinal) && value.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;
}
