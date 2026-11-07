using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/profiles")]
public sealed class ProfilesController(MovieDiscoveryDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok((await db.Profiles.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct))
            .Select(p => new { p.Id, p.Name, p.IsDefault })
            .ToList());

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] CreateProfileRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "invalid_request", message = "name не може бути порожнім" });

        var profile = new Core.Domain.Profile { Name = req.Name.Trim(), IsDefault = false };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return Created($"/api/profiles/{profile.Id}", new { profile.Id, profile.Name, profile.IsDefault });
    }

    public sealed record CreateProfileRequest(string Name);
}
