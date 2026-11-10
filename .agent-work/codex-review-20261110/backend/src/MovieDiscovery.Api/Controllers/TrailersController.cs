using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
    TrailerVerifier verifier,
    ICacheStore cache,
    IOptions<AdminOptions> adminOptions,
    ILogger<TrailersController> log) : ControllerBase
{
    private const string CacheSource = "trailers";
    private const int CacheTtlSeconds = 86_400; // 24 год — кеш ВІДПОВІДІ (окремо від search-кешу)

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [HttpGet("api/titles/{id:int}/trailers")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var cacheKey = $"response:{id}";
        var cached = await cache.GetAsync(CacheSource, cacheKey, ct);
        if (cached is not null)
            return Content(cached, "application/json");

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
            await db.SaveChangesAsync(ct);
            rows = [row];
            log.LogInformation("Fast-path трейлер верифіковано: title={TitleId}, video={YtId}, status={Status}",
                id, title.TrailerYtId, oembed ?? "uncertain");
        }

        // On-demand: 1 search — ЛИШЕ коли кандидатів нема взагалі (нема рядків і нема fast-path).
        if (rows.Count == 0)
        {
            await sync.SyncTitleAsync(id, ct);
            rows = await LoadRowsAsync(db, id, ct);
        }

        var response = new TitleTrailersResponse(id, ToInfos(rows));
        var json = JsonSerializer.Serialize(response, WebJson);
        await cache.SetAsync(CacheSource, cacheKey, json, CacheTtlSeconds, ct);
        return Ok(response);
    }

    /// <summary>Батч-синк трейлерів: 202 {queued:true, limit} — обробка у TitleTrailerSyncBackgroundService.</summary>
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

        return Accepted(new { queued = true, limit, source = "youtube-trailers" });
    }

    public sealed record TitleTrailersResponse(int TitleId, IReadOnlyList<TrailerInfo> Trailers);

    public sealed class TrailerSyncRequest
    {
        public int? Limit { get; set; } // ОБОВ'ЯЗКОВИЙ ліміт прогону; порожнє тіло → дефолт 20
    }

    private IActionResult? CheckAdmin()
    {
        var expected = adminOptions.Value.Key;
        if (string.IsNullOrEmpty(expected))
            return StatusCode(500, new { error = "admin_not_configured", message = "ADMIN_KEY не задано в .env" });
        var provided = Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(provided) || !string.Equals(provided, expected, StringComparison.Ordinal))
            return Unauthorized(new { error = "unauthorized", message = "Потрібен заголовок X-Admin-Key" });
        return null;
    }

    /// <summary>Рядки трейлерів тайтла (усі статуси — фільтрація показу в ToInfos).</summary>
    internal static Task<List<TitleTrailer>> LoadRowsAsync(MovieDiscoveryDbContext dbContext, int titleId, CancellationToken ct) =>
        dbContext.TitleTrailers.AsNoTracking()
            .Where(x => x.TitleId == titleId)
            .ToListAsync(ct);

    /// <summary>
    /// Відповідь: програвані = verified_status ok|NULL; синк-рядки з не-ok статусом НЕ показуються
    /// («не-ok не показувати»). Виняток — швидкий шлях (source=titles): його не-ok рядок віддається
    /// зі статусом, щоб фронт чесно показав «недоступний» + безпечне зовнішнє посилання (UX-05 f).
    /// Порядок: official → uk → ru → en → multi → інші мови → unknown.
    /// </summary>
    internal static List<TrailerInfo> ToInfos(IReadOnlyList<TitleTrailer> rows) => rows
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
