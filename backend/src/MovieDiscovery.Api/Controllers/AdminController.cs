using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Embeddings;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Sync;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/admin")]
public sealed class AdminController(
    MovieDiscoveryDbContext db,
    SyncQueue syncQueue,
    EmbeddingQueue embeddingQueue,
    AnimeSyncQueue animeSyncQueue,
    Microsoft.Extensions.Options.IOptions<AdminOptions> adminOptions,
    Microsoft.Extensions.Options.IOptions<MalOfficialOptions> malOptions) : ControllerBase
{
    /// <summary>Перевірка X-Admin-Key. ADMIN_KEY не задано → 500 (конфіг-помилка), не збіг → 401.</summary>
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

    /// <summary>Поставити popular/discover-синхронізацію у чергу: 202 {queued:true}.</summary>
    [HttpPost("sync/popular")]
    public IActionResult SyncPopular([FromBody] SyncPopularRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var type = (req?.Type ?? "all").ToLowerInvariant();
        if (type is not ("movie" or "series" or "all"))
            return BadRequest(new { error = "invalid_request", message = "type має бути movie | series | all" });

        var pages = req?.Pages ?? 5;
        if (pages is < 1 or > 500)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–500" });

        if (!syncQueue.TryEnqueue(new SyncJob(pages, type)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true });
    }

    /// <summary>Синк аніме-каталогу (AniList POPULARITY_DESC × pages; Jikan — dormant): 202 {queued:true, source:"anilist"}.</summary>
    [HttpPost("sync/anime")]
    public IActionResult SyncAnime([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 20)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–20" });

        if (!animeSyncQueue.TryEnqueue(new AnimeSyncJob(pages, "anime")))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "anilist" });
    }

    /// <summary>Синк манга-каталогу (AniList POPULARITY_DESC × pages; Jikan — dormant): 202 {queued:true, source:"anilist"}.</summary>
    [HttpPost("sync/manga")]
    public IActionResult SyncManga([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 20)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–20" });

        if (!animeSyncQueue.TryEnqueue(new AnimeSyncJob(pages, "manga")))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "anilist" });
    }

    /// <summary>
    /// Сезонний шар офіційного MAL API v2 (anime/season/{year}/{season}): 202 {queued:true, source:"mal-official"}.
    /// MAL_CLIENT_ID не задано → 400 з підказкою (реєстрація безкоштовного client ID).
    /// </summary>
    [HttpPost("sync/mal-season")]
    public IActionResult SyncMalSeason([FromBody] MalSeasonRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        if (string.IsNullOrWhiteSpace(malOptions.Value.ClientId))
            return BadRequest(new
            {
                error = "mal_not_configured",
                message = "MAL_CLIENT_ID не задано: зареєструй безкоштовний client ID на myanimelist.net/apis.php і пропиши MAL_CLIENT_ID у .env",
            });

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 10)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–10" });

        var season = (req?.Season ?? MalOfficialClient.SeasonForMonth(DateTime.UtcNow.Month)).Trim().ToLowerInvariant();
        if (!MalOfficialClient.Seasons.Contains(season))
            return BadRequest(new { error = "invalid_request", message = "season має бути winter | spring | summer | fall" });

        var year = req?.Year ?? DateTime.UtcNow.Year;
        if (year is < 1900 or > 2999)
            return BadRequest(new { error = "invalid_request", message = "year має бути 1900–2999" });

        if (!animeSyncQueue.TryEnqueue(new AnimeSyncJob(pages, "mal-season", year, season)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "mal-official" });
    }

    [HttpGet("sync/status")]
    public async Task<IActionResult> SyncStatus(CancellationToken ct)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var lastRun = await db.SyncStates
            .Where(s => s.Source == "tmdb")
            .OrderByDescending(s => s.LastRunAt)
            .Select(s => s.LastRunAt)
            .FirstOrDefaultAsync(ct);

        var titlesCount = await db.Titles.CountAsync(ct);
        var embeddingsCount = await db.Embeddings.CountAsync(ct);
        var animeCount = await db.Titles.CountAsync(t => t.Type == "anime", ct);
        var mangaCount = await db.Titles.CountAsync(t => t.Type == "manga", ct);

        return Ok(new
        {
            lastRunAt = lastRun,
            titlesCount,
            embeddingsCount,
            queueSize = embeddingQueue.Count + syncQueue.Count + animeSyncQueue.Count,
            animeCount,
            mangaCount,
        });
    }

    public sealed class SyncPopularRequest
    {
        public int Pages { get; set; } = 5;
        public string Type { get; set; } = "all";
    }

    public sealed class SyncPagesRequest
    {
        public int Pages { get; set; } = 1;
    }

    public sealed class MalSeasonRequest
    {
        public int? Year { get; set; }     // null → поточний рік
        public string? Season { get; set; } // null → поточний сезон (winter|spring|summer|fall)
        public int Pages { get; set; } = 1;
    }
}
