using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>Імпорт переглянутого/прочитаного з трекерів (FR-13): джоби з прогресом.</summary>
[ApiController]
[Route("api/import")]
public sealed class ImportController(MovieDiscoveryDbContext db, ImportQueue queue, TasteProfileService taste) : ControllerBase
{
    /// <summary>Статичний перелік джерел імпорту з підказками (що вводити користувачу).</summary>
    [HttpGet("sources")]
    public IActionResult Sources() => Ok(ImportSourceCatalog.All
        .Select(s => new { id = s.Id, name = s.Name, hint = s.Hint, authless = s.Authless })
        .ToList());

    /// <summary>Створити джобу імпорту: 202 {jobId}; невідомий source → 400.</summary>
    [HttpPost("jobs")]
    public async Task<IActionResult> CreateJob([FromBody] CreateImportJobRequest? req, CancellationToken ct)
    {
        var source = req?.Source?.Trim().ToLowerInvariant() ?? "";
        if (!ImportSourceCatalog.IsKnown(source))
            return BadRequest(new { error = "invalid_request", message = "source має бути mal | anilist | shikimori" });

        var username = req!.Username?.Trim() ?? "";
        if (username.Length is 0 or > 64)
            return BadRequest(new { error = "invalid_request", message = "username обов'язковий (1–64 символів)" });

        // MD-01 + ізоляція профілів: імпорт ЗАЛОГІНЕНОГО юзера пише в ЙОГО ВЛАСНИЙ профіль —
        // НІКОЛИ у спільний гостьовий/дефолтний профіль (дефект хвилі-1: усе імпортоване
        // зливалось у profile_id=1, імпортовані списки були невидимі юзеру, а
        // import_jobs.profile_id залишався NULL). profileId клієнта для cookie-юзера
        // ігнорується — як і в списках: авторизований завжди має власний профіль.
        // Анонімний імпорт (legacy-сумісність): явний profileId клієнта або null →
        // ImportService.ResolveProfileAsync падає на дефолтний (гостьовий) профіль.
        int? profileId;
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        if (User.Identity?.IsAuthenticated == true && userId.Length > 0)
        {
            var up = await taste.ResolveAsync(userId, ct); // створює user_profiles + профіль за потреби
            profileId = up.ProfileId;
        }
        else
        {
            profileId = req.ProfileId;
            if (profileId is { } pid && !await db.Profiles.AnyAsync(p => p.Id == pid, ct))
                return NotFound(new { error = "not_found", message = $"Профіль з id={pid} не знайдено" });
        }

        var job = new ImportJob
        {
            Source = source,
            Username = username,
            ProfileId = profileId,
            Status = "queued",
            CreatedAt = DateTime.UtcNow,
        };
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync(ct);

        if (!queue.TryEnqueue(new ImportQueueJob(job.Id)))
            return StatusCode(503, new { error = "queue_full", message = "Черга імпортів переповнена" });

        return Accepted(new { jobId = job.Id });
    }

    /// <summary>Статус і статистика джоби (опитування прогресу).</summary>
    [HttpGet("jobs/{id:int}")]
    public async Task<IActionResult> GetJob(int id, CancellationToken ct)
    {
        var job = await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return NotFound(new { error = "not_found", message = "Джобу імпорту не знайдено" });

        return Ok(new
        {
            id = job.Id,
            source = job.Source,
            username = job.Username,
            status = job.Status,
            stats = job.Stats?.RootElement,
            error = job.Error,
            createdAt = job.CreatedAt,
            finishedAt = job.FinishedAt,
        });
    }

    public sealed class CreateImportJobRequest
    {
        public string? Source { get; set; }
        public string? Username { get; set; }
        public int? ProfileId { get; set; }
    }
}
