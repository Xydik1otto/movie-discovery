using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// UX-05: трейлери тайтла. GET /api/titles/{id}/trailers — кандидати з title_trailers
/// (ok + не-верифіковані; верифікація при синку), кеш відповіді 24 год у source_cache;
/// on-demand 1 search ТІЛЬКИ якщо збережених кандидатів нема взагалі.
/// POST /api/admin/sync/trailers {limit} — батч-синк (лимит обов'язковий за концепцією,
/// дефолт 20; кап 200 — дисципліна квоти: 1 search ≈ 100 units на тайтл).
/// </summary>
[ApiController]
public sealed class TrailersController(
    MovieDiscoveryDbContext db,
    TitleTrailerSyncService sync,
    TitleTrailerSyncQueue trailerSyncQueue,
    TrailerVerifier verifier,
    ICacheStore cache,
    IConfiguration configuration,
    ILogger<TrailersController> log) : ControllerBase
{
    private const string CacheSource = "trailers";
    private const int CacheTtlSeconds = 86_400; // 24 год — кеш ВІДПОВІДІ (окремо від search-кешу)

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Single-flight (рев'ю Codex): паралельні cache-miss GET на той самий тайтл —
    /// ОДИН on-demand search (інакше — подвійна витрата квоти 2×100 units) і ризик
    /// UNIQUE(title_id, yt_id)-краху на паралельному fast-path insert.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    [HttpGet("api/titles/{id:int}/trailers")]
    [EnableRateLimiting(YouTubeRatePolicy.PolicyName)] // 10/хв/IP (RateLimitPolicies.cs): 1 on-demand search ≈ 100 units квоти
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        // Single-flight: gate на titleId — повторний вхід після звільнення перевіряє кеш заново
        var gate = Gates.GetOrAdd($"title:{id}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var cached = await cache.GetAsync(CacheSource, $"response:{id}", ct);
            if (cached is not null)
                return Content(cached, "application/json");
            return await ComputeAsync(id, ct);
        }
        finally
        {
            // Семафор НЕ диспозимо і НЕ прибираємо зі словника: колишній патерн
            // «CurrentCount == 1 → TryRemove + Dispose» має гонку — між перевіркою
            // CurrentCount і Dispose інший запит міг уже зайти в WaitAsync (GetOrAdd
            // вернув той самий семафор), і Release по диспознутому семафору давав
            // ObjectDisposedException під навантаженням. Семафори дешеві, а словник
            // обмежений id тайтлів за життя процесу — витік тут не проблема.
            gate.Release();
        }
    }

    /// <summary>Тіло GET після single-flight gate і re-check кешу (важке, одноразове на cache-miss).</summary>
    private async Task<IActionResult> ComputeAsync(int id, CancellationToken ct)
    {
        var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (title is null)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        var rows = await LoadRowsAsync(db, id, ct);

        // Швидкий шлях titles.trailer_yt_id: рядків нема → верифікуємо oEmbed (безкоштовно,
        // без квоти) і зберігаємо як рядок (source=titles). Навіть gone/nonembeddable зберігається
        // СВІДОМО: фронт показує чесне повідомлення/безпечне зовнішнє посилання замість мертвого
        // iframe (UX-05 f) — замість мовчки зниклого трейлера.
        if (rows.Count == 0 && !string.IsNullOrWhiteSpace(title.TrailerYtId))
        {
            var oembed = await verifier.OembedStatusAsync(title.TrailerYtId, ct);
            var row = new TitleTrailer
            {
                TitleId = id,
                YtId = title.TrailerYtId,
                Lang = null, // швидкий шлях не знає мови — чесний unknown
                Official = false,
                Source = "titles",
                VerifiedAt = oembed is null ? null : DateTime.UtcNow,
                VerifiedStatus = oembed,
            };
            db.TitleTrailers.Add(row);
            // UNIQUE(title_id, yt_id) — гонка з конкурентним запитом/синком не падає:
            // 23505 / unique-повідомлення трактуємо як «рядок уже записали» (рев'ю Codex).
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (TitleTrailerSyncService.IsUniqueViolation(ex))
            {
                db.Entry(row).State = EntityState.Detached;
                log.LogDebug("Fast-path трейлер уже записано конкурентно: title={TitleId}, video={YtId}", id, title.TrailerYtId);
            }
            rows = await LoadRowsAsync(db, id, ct);
            log.LogInformation("Fast-path трейлер верифіковано: title={TitleId}, video={YtId}, status={Status}",
                id, title.TrailerYtId, oembed ?? "uncertain");
        }

        // On-demand: 1 search — ЛИШЕ коли кандидатів нема взагалі (нема рядків і нема fast-path).
        // Під single-flight gate — максимум один search на cache-miss (квота 100 units не подвоюється).
        if (rows.Count == 0)
        {
            try
            {
                await sync.SyncTitleAsync(id, ct);
            }
            catch (YouTubeQuotaExceededException ex)
            {
                // Денний глобальний ліміт search-викликів (90/день, YouTubeDailySearchQuota) вичерпано —
                // чесна 429 замість порожньої відповіді (без gate анонім випалив би денну квоту API).
                return StatusCode(429, new { error = "quota_exceeded", message = ex.Message });
            }
            rows = await LoadRowsAsync(db, id, ct);
        }

        var response = new TitleTrailersResponse(id, ToInfos(rows));
        var json = JsonSerializer.Serialize(response, WebJson);
        await cache.SetAsync(CacheSource, $"response:{id}", json, CacheTtlSeconds, ct);
        return Ok(response);
    }

    /// <summary>Postgres 23505 (unique_violation) або duplicate/unique у повідомленні (InMemory тощо).</summary>
    internal static bool IsUniqueViolation(DbUpdateException ex) => TitleTrailerSyncService.IsUniqueViolation(ex);

    /// <summary>Батч-синк трейлерів: РЕАЛЬНА постановка джоби в чергу (рев'ю Codex) — 202 | 503 queue_full.</summary>
    [HttpPost("api/admin/sync/trailers")]
    public IActionResult SyncTrailers([FromBody] TrailerSyncRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var limit = req?.Limit ?? 20;
        if (limit is < 1 or > 200)
            return BadRequest(new
            {
                error = "invalid_request",
                message = "limit має бути 1–200 (обов'язковий ліміт прогону; 1 тайтл ≈ 100 units)",
            });

        if (!trailerSyncQueue.TryEnqueue(new TitleTrailerSyncJob(limit)))
            return StatusCode(503, new { error = "queue_full", message = "Черга трейлер-синхронізації переповнена" });

        return Accepted(new { queued = true, limit, source = "youtube-trailers" });
    }

    public sealed record TitleTrailersResponse(int TitleId, IReadOnlyList<TrailerInfo> Trailers);

    public sealed class TrailerSyncRequest
    {
        public int? Limit { get; set; } // ОБОВ'ЯЗКОВИЙ ліміт прогону; порожнє тіло → дефолт 20
    }

    /// <summary>Перевірка X-Admin-Key — спільний AdminGate (логіка колись була продубльована тут).</summary>
    private IActionResult? CheckAdmin() => AdminGate.Verify(HttpContext, configuration);

    /// <summary>Рядки трейлерів тайтла (усі статуси — фільтрація показу в ToInfos).</summary>
    public static Task<List<TitleTrailer>> LoadRowsAsync(MovieDiscoveryDbContext dbContext, int titleId, CancellationToken ct) =>
        dbContext.TitleTrailers.AsNoTracking()
            .Where(x => x.TitleId == titleId)
            .ToListAsync(ct);

    /// <summary>
    /// Відповідь: програвані = verified_status ok|NULL; синк-рядки з не-ok статусом НЕ показуються
    /// («не-ok не показувати»). Виняток — швидкий шлях (source=titles): його не-ok рядок віддається
    /// зі статусом, щоб фронт чесно показав «недоступний» + безпечне зовнішнє посилання (UX-05 f).
    /// Порядок: official → uk → ru → en → multi → інші мови → unknown.
    /// </summary>
    public static List<TrailerInfo> ToInfos(IReadOnlyList<TitleTrailer> rows) => rows
        .Where(r => r.VerifiedStatus is "ok" or null || r.Source == "titles")
        .Select(r => new TrailerInfo(r.YtId, r.Lang, r.Official, r.Source, r.VerifiedAt, r.VerifiedStatus))
        .OrderByDescending(i => i.Official)
        .ThenBy(i => LangRank(i.Lang))
        .ThenBy(i => i.VerifiedAt == null ? 1 : 0)
        .ToList();

    private static int LangRank(string? lang) => lang switch
    {
        "uk" => 0,
        "ru" => 1,
        "en" => 2,
        "multi" => 3,
        null => 9,
        _ => 4,
    };
}
