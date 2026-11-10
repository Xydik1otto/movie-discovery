using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>Імпорт переглянутого/прочитаного з трекерів (FR-13): джоби з прогресом.
/// Ізоляція профілів (Codex IV-01): сервер САМ визначає профіль джоби —
/// auth → власний профіль юзера; анонім → designated guest profile (legacy);
/// profileId клієнта НІКОЛИ не є джерелом правди. Читання джоби — лише власнику.</summary>
[ApiController]
[Route("api/import")]
public sealed class ImportController(
    MovieDiscoveryDbContext db,
    ImportQueue queue,
    TasteProfileService taste,
    GuestProfileService guest) : ControllerBase
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

        // Ізоляція профілів (MD-01 + Codex IV-01): profileId з КЛІЄНТА ніколи не визначає,
        // куди пишеться імпорт — це був шлях до запису чужих списків (202 у чужий профіль).
        //   Залогінений:   СВІЙ профіль (TasteProfileService.ResolveAsync), явний чужий
        //                  profileId → 403 (не мовчазне ігнорування — чесна відмова).
        //   Анонім:        лише legacy guest-path — designated guest profile; явний
        //                  profileId ≠ guest → 403; guest-шлях вимкнено (профіль
        //                  приріплений/відсутній) → 401.
        int? profileId;
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        if (User.Identity?.IsAuthenticated == true && userId.Length > 0)
        {
            var up = await taste.ResolveAsync(userId, ct); // створює user_profiles + профіль за потреби
            var own = up.ProfileId;
            if (req.ProfileId is { } foreign && foreign != own)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden" });
            profileId = own;
        }
        else
        {
            var guestProfile = await guest.ResolveAsync(ct);
            if (guestProfile is null)
                return Unauthorized(new { error = "unauthorized", message = "Анонімний імпорт недоступний — увійдіть в акаунт" });

            if (req.ProfileId is { } requested && requested != guestProfile.Id)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden" });

            profileId = guestProfile.Id; // чесний profile_id у джоби (не NULL)
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

    /// <summary>Статус і статистика джоби (опитування прогресу). Ownership (Codex IV-01):
    /// власник — профіль запитувача (auth → свій профіль; анонім → guest/legacy-NULL джоба);
    /// чужа джоба → 404 без розкриття існування.</summary>
    [HttpGet("jobs/{id:int}")]
    public async Task<IActionResult> GetJob(int id, CancellationToken ct)
    {
        var job = await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return NotFound(new { error = "not_found", message = "Джобу імпорту не знайдено" });

        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        if (User.Identity?.IsAuthenticated == true && userId.Length > 0)
        {
            var up = await taste.ResolveAsync(userId, ct);
            if (job.ProfileId != up.ProfileId)
                return NotFound(new { error = "not_found", message = "Джобу імпорту не знайдено" });
        }
        else
        {
            var guestProfile = await guest.ResolveAsync(ct);
            var guestOwned = guestProfile is { } g
                && (job.ProfileId is null || job.ProfileId == g.Id); // NULL — legacy гостьові джоби
            if (!guestOwned)
                return NotFound(new { error = "not_found", message = "Джобу імпорту не знайдено" });
        }

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
        /// <summary>Ігнорується сервером (Codex IV-01): профіль визначається з auth/guest-шляху.</summary>
        public int? ProfileId { get; set; }
    }
}
