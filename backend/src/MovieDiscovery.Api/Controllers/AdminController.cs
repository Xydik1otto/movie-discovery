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
    Microsoft.Extensions.Options.IOptions<AdminOptions> adminOptions) : ControllerBase
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

    /// <summary>Синк аніме-каталогу (Jikan top/bypopularity + seasons/now): 202 {queued:true}.</summary>
    [HttpPost("sync/anime")]
    public IActionResult SyncAnime([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 10)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–10" });

        if (!animeSyncQueue.TryEnqueue(new AnimeSyncJob(pages, "anime")))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true });
    }

    /// <summary>Синк манга-каталогу (Jikan top/manga): 202 {queued:true}.</summary>
    [HttpPost("sync/manga")]
    public IActionResult SyncManga([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 10)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–10" });

        if (!animeSyncQueue.TryEnqueue(new AnimeSyncJob(pages, "manga")))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true });
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
}
