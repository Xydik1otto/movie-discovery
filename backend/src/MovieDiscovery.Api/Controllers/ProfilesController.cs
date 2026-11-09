using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// Профілі (MD-01): доступні лише власному акаунту. Анонімний перелік усіх
/// профілів і довільне анонімне створення закриті — це розкривало назви/ID
/// приватних акаунтів і дозволяло захаращувати спільний набір профілів.
/// Гостьовий surface: списки гостя живуть у localStorage клієнта (MD-04);
/// серверний designated guest profile обслуговується через /api/lists без
/// /api/profiles (гість профілі не перелічує і не створює).
/// </summary>
[ApiController]
[Authorize]
[Route("api/profiles")]
public sealed class ProfilesController(MovieDiscoveryDbContext db, TasteProfileService taste) : ControllerBase
{
    /// <summary>Лише власний профіль поточного акаунта (401 для аноніма).</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = UserId();
        if (userId.Length == 0)
            return Unauthorized(new { error = "unauthorized", message = "Потрібен вхід" });

        var up = await taste.ResolveAsync(userId, ct);
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == up.ProfileId, ct);
        if (profile is null)
            return NotFound(new { error = "not_found", message = "Профіль не знайдено" });

        return Ok(new[] { new { profile.Id, profile.Name, profile.IsDefault } });
    }

    /// <summary>Створення профілю — лише для залогіненого юзера; якщо профіль уже є → 409
    /// (модель FR-16: один постійний профіль на акаунта, створюється автоматично).</summary>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] CreateProfileRequest req, CancellationToken ct)
    {
        var userId = UserId();
        if (userId.Length == 0)
            return Unauthorized(new { error = "unauthorized", message = "Потрібен вхід" });
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "invalid_request", message = "name не може бути порожнім" });
        if (req.Name.Trim().Length > 80)
            return BadRequest(new { error = "invalid_request", message = "name — до 80 символів" });

        var existing = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is not null)
        {
            var p0 = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == existing.ProfileId, ct);
            return Conflict(new
            {
                error = "profile_exists",
                message = "У акаунта вже є профіль",
                profile = p0 is null ? null : new { p0.Id, p0.Name, p0.IsDefault },
            });
        }

        var profile = new Core.Domain.Profile { Name = req.Name.Trim(), IsDefault = false };
        db.Profiles.Add(profile);
        db.UserProfiles.Add(new Core.Domain.UserProfile { UserId = userId, Profile = profile });
        await db.SaveChangesAsync(ct);
        return Created($"/api/profiles/{profile.Id}", new { profile.Id, profile.Name, profile.IsDefault });
    }

    private string UserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";

    public sealed record CreateProfileRequest(string Name);
}
