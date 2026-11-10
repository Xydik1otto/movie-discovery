using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Tmdb; // ITmdbImagesService

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>Результат прогону poster-source-sync.</summary>
public sealed record PosterSourceSyncResult(int Processed, int Updated);

/// <summary>
/// Джоба poster-source-sync: MaxRows — кап рядків масового прогону (1..2 000 000, null — без ліміту);
/// TitleId — точкове доціплення одного тайтла (on-demand хук /api/title/{id} — дешеве, без сканування каталогу).
/// </summary>
public sealed record PosterSourceSyncJob(int? MaxRows, int? TitleId = null);

/// <summary>Черга poster-source-sync (той самий патерн, що WikipediaPosterSyncQueue).</summary>
public sealed class PosterSourceSyncQueue
{
    private readonly System.Threading.Channels.Channel<PosterSourceSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<PosterSourceSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(PosterSourceSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<PosterSourceSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Автоматичне доціплення постерів у тайтлів, у яких їх нема (99.7% каталогу — poster_path
/// порожній). ОДНА черга, внутрішній branch по type/tmdb_id:
/// ── TMDB (джерело №1, ключ у TmdbOptions): тайтли з tmdb_id NOT NULL і порожнім постером,
///    ordered popularity DESC батчами 100 → TmdbImagesService.ResolveAsync (пауза ≥300мс,
///    429-ретраї з Retry-After ≤3с) → fill-only UPDATE poster_path + backdrop_path;
/// ── аніме/манга (tmdb_id NULL): PosterCoverageService — Shikimori за shikimori_id або
///    Jikan за mal_id → fill-only poster_path.
/// Ідемпотентно: заповнені рядки у вибірку не потрапляють; наявні картинки не перебиваємо.
/// Запускається АДМІН-джобою (POST /api/admin/sync/poster-source) і on-demand точковою
/// джобою з GET /api/title/{id} (TitleId) — НІКОЛИ з GET-шляхів каталогу.
/// </summary>
public sealed class PosterSourceSyncService(
    ITmdbImagesService tmdbImages,
    PosterCoverageService coverage,
    MovieDiscoveryDbContext db,
    ILogger<PosterSourceSyncService> log)
{
    public const string Source = "poster-source-sync";

    /// <summary>Розмір батчу вибірки/збереження.</summary>
    public const int BatchSize = 100;

    /// <summary>Максимальний cap рядків масового прогону.</summary>
    public const int MaxRowsLimit = 2_000_000;

    /// <summary>Повний прогін (масовий синк) або точкове доціплення одного тайтла (TitleId).</summary>
    public async Task<PosterSourceSyncResult> RunAsync(int? maxRows, int? titleId, CancellationToken ct = default)
    {
        if (titleId is int single)
        {
            var (singleProcessed, singleUpdated) = await ProcessSingleAsync(single, ct);
            return new PosterSourceSyncResult(singleProcessed, singleUpdated);
        }

        var capLeft = maxRows is { } requested ? Math.Clamp(requested, 1, MaxRowsLimit) : (int?)null;
        var processedTotal = 0;
        var updatedTotal = 0;

        // Фаза 1: TMDB (фільми/серіали/шоу з tmdb_id) — найпопулярніші перші.
        var tmdbSource = db.Titles.Where(t =>
            (t.PosterPath == null || t.PosterPath == "") && t.TmdbId != null);
        var (tmdbProcessed, tmdbUpdated) = await SweepAsync(
            tmdbSource, ResolveTmdbAsync, capLeft, processedTotal, ct);
        processedTotal += tmdbProcessed;
        updatedTotal += tmdbUpdated;
        if (capLeft is { } leftA) capLeft = Math.Max(0, leftA - tmdbProcessed);

        // Фаза 2: аніме/манга без TMDB — покриття Shikimori/Jikan (залишок капу).
        if (capLeft is not 0)
        {
            var animeSource = db.Titles.Where(t =>
                (t.PosterPath == null || t.PosterPath == "") &&
                t.TmdbId == null &&
                (t.Type == "anime" || t.Type == "manga"));
            var (animeProcessed, animeUpdated) = await SweepAsync(
                animeSource, ResolveAnimeAsync, capLeft, processedTotal, ct);
            processedTotal += animeProcessed;
            updatedTotal += animeUpdated;
        }

        await UpdateSyncStateAsync(updatedTotal, ct);
        log.LogInformation("poster-source-sync завершено: processed={Processed}, updated={Updated}",
            processedTotal, updatedTotal);
        return new PosterSourceSyncResult(processedTotal, updatedTotal);
    }

    /// <summary>Точкове доціплення одного тайтла (on-demand): fill-only, без sync_state.</summary>
    private async Task<(int Processed, int Updated)> ProcessSingleAsync(int titleId, CancellationToken ct)
    {
        var title = await db.Titles.FirstOrDefaultAsync(t => t.Id == titleId, ct);
        if (title is null) return (0, 0);
        if (!string.IsNullOrWhiteSpace(title.PosterPath)) return (0, 0); // уже заповнено — без HTTP

        var (poster, backdrop) = title.TmdbId is not null
            ? await ResolveTmdbAsync(title, ct)
            : await ResolveAnimeAsync(title, ct);

        var updated = 0;
        if (!string.IsNullOrWhiteSpace(poster) && string.IsNullOrWhiteSpace(title.PosterPath))
        {
            title.PosterPath = poster;
            updated++;
        }
        if (!string.IsNullOrWhiteSpace(backdrop) && string.IsNullOrWhiteSpace(title.BackdropPath))
        {
            title.BackdropPath = backdrop;
            updated++;
        }
        if (updated > 0)
        {
            title.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        return (1, updated > 0 ? 1 : 0);
    }

    /// <summary>TMDB-резолв: tmdb_id → сирий poster_path/backdrop_path (кеш деталей → один легкий GET).</summary>
    private async Task<(string? Poster, string? Backdrop)> ResolveTmdbAsync(Title t, CancellationToken ct)
    {
        if (t.TmdbId is not int tmdbId) return (null, null);
        var resolved = await tmdbImages.ResolveAsync(tmdbId, t.Type, ct);
        return (resolved?.PosterPath, resolved?.BackdropPath);
    }

    /// <summary>Аніме/манга-резолв: shikimori_id → Shikimori, mal_id → Jikan (готові абсолютні URL).</summary>
    private async Task<(string? Poster, string? Backdrop)> ResolveAnimeAsync(Title t, CancellationToken ct)
    {
        var poster = await coverage.ResolvePosterAsync(t, ct);
        return (poster, null); // у Shikimori/Jikan бекдропів нема
    }

    /// <summary>
    /// Ключова (keyset) пагінація popularity DESC по фільтрованій вибірці: батчі BatchSize,
    /// курсор (popularity, id) — без OFFSET-пересортувань між батчами. Резолв тротлінгується
    /// самим сервісом-резолвером; збереження — SaveChanges на батч (fill-only семантика).
    /// </summary>
    private async Task<(int Processed, int Updated)> SweepAsync(
        IQueryable<Title> source,
        Func<Title, CancellationToken, Task<(string? Poster, string? Backdrop)>> resolveAsync,
        int? cap,
        int processedBefore,
        CancellationToken ct)
    {
        var processed = 0;
        var updated = 0;
        var lastLogged = 0;
        double cursorPop = double.MaxValue;
        int cursorId = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = cap is { } c ? c - processed : (int?)null;
            if (remaining is <= 0) break;

            var page = source;
            if (cursorId > 0 || cursorPop < double.MaxValue)
                page = page.Where(t => t.Popularity < cursorPop || (t.Popularity == cursorPop && t.Id > cursorId));
            var take = remaining is { } r ? Math.Min(BatchSize, r) : BatchSize;

            var batch = await page
                .OrderByDescending(t => t.Popularity)
                .ThenBy(t => t.Id)
                .Take(take)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            foreach (var title in batch)
            {
                var (poster, backdrop) = await resolveAsync(title, ct);
                var changed = false;
                if (!string.IsNullOrWhiteSpace(poster) && string.IsNullOrWhiteSpace(title.PosterPath))
                {
                    title.PosterPath = poster;
                    changed = true;
                }
                if (!string.IsNullOrWhiteSpace(backdrop) && string.IsNullOrWhiteSpace(title.BackdropPath))
                {
                    title.BackdropPath = backdrop;
                    changed = true;
                }
                if (changed)
                {
                    title.UpdatedAt = DateTime.UtcNow;
                    updated++; // лічильник по тайтлах (не по полях)
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            processed += batch.Count;

            cursorPop = batch[^1].Popularity;
            cursorId = batch[^1].Id;

            if (processed - lastLogged >= 100)
            {
                lastLogged = processed;
                log.LogInformation("poster-source-sync: оброблено={Processed} (загалом з поп. фаз={Total}), заповнено={Updated}",
                    processed, processedBefore + processed, updated);
            }
        }

        return (processed, updated);
    }

    private async Task UpdateSyncStateAsync(int updated, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "original", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "original" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = updated;
        state.LastPages = updated; // кількість рядків, у яких реально з'явився постер
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}

/// <summary>Фонова обробка poster-source-джоб (точкових on-demand і масових адмін-прогонів).</summary>
public sealed class PosterSourceSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<PosterSourceSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<PosterSourceSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PosterSourceSyncService>();
                var result = await service.RunAsync(job.MaxRows, job.TitleId, stoppingToken);
                if (job.TitleId is int titleId)
                    log.LogInformation("poster-source-sync точкова джоба: title_id={TitleId}, updated={Updated}",
                        titleId, result.Updated);
                else
                    log.LogInformation("poster-source-sync джоба завершена: processed={Processed}, updated={Updated}",
                        result.Processed, result.Updated);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка poster-source-sync (maxRows={MaxRows}, titleId={TitleId})",
                    job.MaxRows, job.TitleId);
            }
        }
    }
}
