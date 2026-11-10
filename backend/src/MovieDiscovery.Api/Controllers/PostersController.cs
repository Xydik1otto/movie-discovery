using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>Тіло запиту пакетного резолву постерів для сітки каталогу.</summary>
public sealed class BatchResolvePostersRequest
{
    public List<int> TitleIds { get; set; } = [];
}

/// <summary>Резолвнуті URL одного тайтла (абсолютні; backdrop може бути null).</summary>
public sealed record BatchResolvedPosters(string? PosterUrl, string? BackdropUrl);

/// <summary>
/// Пакетний on-demand резолв постерів для сітки каталогу: публічний
/// POST /api/posters/batch-resolve {"titleIds":[1..24 id]}. Для тайтлів без постера —
/// синхронна спроба резолву тим самим ланцюгом, що poster-source-sync (м'який кап 800мс
/// на тайтл, паралельність 6); хто не встиг/не розкрився — точкова фонова джоба в чергу
/// PosterSourceSyncQueue (догорне fill-only), хто встиг — fill-only запис у БД одразу
/// (як TitlesController.PersistResolvedPosterAsync). Тайтли з наявним постером — у
/// відповіді без HTTP. Лімітер 15 req/хв/IP. НІКОЛИ не викликається з масових GET-шляхів
/// каталогу — тільки точкова сітка на екрані.
/// </summary>
[ApiController]
[Route("api/posters")]
public sealed class PostersController(
    PosterSourceSyncService sync,
    PosterSourceSyncQueue queue,
    MovieDiscoveryDbContext db,
    IMemoryCache cache,
    ILogger<PostersController> log) : ControllerBase
{
    /// <summary>Максимальний розмір пакета сітки (1..24 id).</summary>
    internal const int MaxBatchSize = 24;

    /// <summary>Паралельність синхронного резолву — обережно до зовнішніх джерел (тротлінги спільні).</summary>
    internal const int ResolveConcurrency = 6;

    /// <summary>М'який кап очікування резолву одного тайтла (мс) — хто не встиг, той у фонову чергу.</summary>
    internal const int SoftCapMs = 800;

    [HttpPost("batch-resolve")]
    public async Task<IActionResult> BatchResolve([FromBody] BatchResolvePostersRequest request, CancellationToken ct)
    {
        if (request.TitleIds.Count == 0 || request.TitleIds.Count > MaxBatchSize)
            return BadRequest(new { error = "invalid_title_ids", message = $"titleIds: 1..{MaxBatchSize} id" });

        if (!new PosterBatchRateLimiter(cache).TryAcquire(
                HttpContext.Connection.RemoteIpAddress?.ToString(), out var retryAfterMinutes))
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "rate_limited", retryAfterMinutes });

        var ids = request.TitleIds.Distinct().ToList();
        var titles = await db.Titles.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var resolved = new Dictionary<int, BatchResolvedPosters>();
        var misses = new List<int>();
        var pending = new List<Title>();
        foreach (var id in ids)
        {
            if (!titles.TryGetValue(id, out var t))
            {
                misses.Add(id); // неіснуючий id — чесний miss (у чергу не тікаємо: джобі нема чого оновлювати)
                continue;
            }
            if (!string.IsNullOrWhiteSpace(t.PosterPath))
            {
                resolved[id] = new BatchResolvedPosters(
                    TitleMapper.ImgUrl(t.PosterPath, "w500"),
                    TitleMapper.ImgUrl(t.BackdropPath, "w780"));
                continue; // постер уже є — у відповідь без HTTP
            }
            pending.Add(t);
        }

        ct.ThrowIfCancellationRequested();
        var outcomes = await ResolveBatchAsync(pending, ct);

        // Fill-only запис у БД (послідовно, один SaveChanges — DbContext не thread-safe);
        // tmdb_id після imdb-місту бекфілиться самим ResolveTmdbOrImdbAsync у той самий tracked-рядок.
        var changed = 0;
        foreach (var outcome in outcomes)
        {
            var (title, poster, backdrop, timedOut) = outcome;
            if (!timedOut)
            {
                var rowChanged = false;
                if (!string.IsNullOrWhiteSpace(poster) && string.IsNullOrWhiteSpace(title.PosterPath))
                {
                    title.PosterPath = poster;
                    rowChanged = true;
                }
                if (!string.IsNullOrWhiteSpace(backdrop) && string.IsNullOrWhiteSpace(title.BackdropPath))
                {
                    title.BackdropPath = backdrop;
                    rowChanged = true;
                }
                if (rowChanged)
                {
                    title.UpdatedAt = DateTime.UtcNow;
                    changed++;
                }
            }
            if (!string.IsNullOrWhiteSpace(poster))
                resolved[title.Id] = new BatchResolvedPosters(
                    TitleMapper.ImgUrl(title.PosterPath, "w500"),
                    TitleMapper.ImgUrl(title.BackdropPath, "w780"));
            else
            {
                misses.Add(title.Id);
                // Не встигли за кап або джерело не дало постер — догорне фоном (fill-only).
                queue.TryEnqueue(new PosterSourceSyncJob(MaxRows: null, TitleId: title.Id));
            }
        }
        if (changed > 0)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Відповідь уже сформована з резолвнутими URL — збоєний запис доберуть фонові джоби.
                log.LogWarning(ex, "Запис резолвнутих постерів batch-resolve не вдалося — доберуть фонові джоби");
            }
        }

        return Ok(new { resolved, misses });
    }

    /// <summary>
    /// Паралельний резолв з м'яким капом: SemaphoreSlim(6) на одночасні зовнішні дзвінки,
    /// Task.WhenAny із Task.Delay(800мс) на кожен тайтл — хто не встиг, той у відповідь
    /// йде miss'ом і ставиться в фонову чергу (таск лишається дофоново, без blockquote).
    /// </summary>
    private async Task<ResolveOutcome[]> ResolveBatchAsync(List<Title> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return [];
        using var gate = new SemaphoreSlim(ResolveConcurrency, ResolveConcurrency);
        var tasks = pending.Select(t => ResolveOneCappedAsync(t, gate, ct)).ToList();
        var outcomes = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        return outcomes;
    }

    private async Task<ResolveOutcome> ResolveOneCappedAsync(Title t, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var resolve = sync.ResolveOneAsync(t, ct);
            var completed = await Task.WhenAny(resolve, Task.Delay(SoftCapMs, ct));
            if (completed != resolve)
            {
                // Не встигли за м'який кап — таск лишається дофоново (виключення спостерігаємо),
                // БД догорне з черги poster-source-sync.
                _ = resolve.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                return new ResolveOutcome(t, null, null, TimedOut: true);
            }
            try
            {
                var (poster, backdrop) = await resolve;
                return new ResolveOutcome(t, poster, backdrop, TimedOut: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Резолв постера batch-resolve не вдалося (title_id={TitleId})", t.Id);
                return new ResolveOutcome(t, null, null, TimedOut: false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed record ResolveOutcome(Title Title, string? Poster, string? Backdrop, bool TimedOut);
}

/// <summary>
/// Лімітер 15 req/хв/IP для пакетного резолву постерів (той самий патерн, що EmailRateLimiter:
/// спільний IMemoryCache + lock; контролер інстанціює з кешом запиту — окрема DI-реєстрація
/// не обов'язкова; стан переживає запити і скидається з рестартом api).
/// </summary>
internal sealed class PosterBatchRateLimiter(IMemoryCache cache)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Ліміт пакетів на IP — 15/хв (сітка ходить ендпоінтом на кожен скрол/фільтр).</summary>
    internal const int RateLimitPerMinute = 15;

    /// <summary>true → дозвіл витрачено; false → перевищено ліміт, retryAfterMinutes ≥ 1.</summary>
    public bool TryAcquire(string? ip, out int retryAfterMinutes)
    {
        var key = $"poster-batch-rate:{ip ?? "unknown"}";
        var now = DateTimeOffset.UtcNow;
        var bucket = cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Window;
            return new List<DateTimeOffset>();
        })!;

        lock (bucket)
        {
            bucket.RemoveAll(t => now - t >= Window);
            if (bucket.Count >= RateLimitPerMinute)
            {
                // найдавніша витрата + вікно = коли звільниться слот
                retryAfterMinutes = Math.Max(1, (int)Math.Ceiling((bucket.Min() + Window - now).TotalMinutes));
                return false;
            }
            bucket.Add(now);
            retryAfterMinutes = 0;
            return true;
        }
    }
}
