using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/admin/watch/sources")]
public sealed class WatchSourcesController(MovieDiscoveryDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Pending(int before = int.MaxValue, CancellationToken ct = default)
    {
        if (AdminGate.Verify(HttpContext, configuration) is { } denied) return denied;

        var cutoff = DateTime.UtcNow - WebWatchSearch.CandidateLifetime;
        var candidates = await db.Providers.AsNoTracking()
            .Where(p => p.Id < before && p.Source == WebWatchSearch.Source &&
                p.Kind == WebWatchSearch.PendingKind && p.UpdatedAt >= cutoff)
            .OrderByDescending(p => p.Id).Take(100)
            .Select(p => new { p.Id, p.TitleId, p.Title.TitleEn, p.Title.TitleUk, p.Title.Year, p.ProviderName, p.Region, p.Url, p.UpdatedAt })
            .ToListAsync(ct);
        return Ok(new { candidates });
    }

    [HttpPost("{id:int}/review")]
    public async Task<IActionResult> Review(int id, ReviewRequest request, CancellationToken ct)
    {
        if (AdminGate.Verify(HttpContext, configuration) is { } denied) return denied;

        var cutoff = DateTime.UtcNow - WebWatchSearch.CandidateLifetime;
        var candidate = await db.Providers.SingleOrDefaultAsync(p => p.Id == id && p.Source == WebWatchSearch.Source &&
            p.Kind == WebWatchSearch.PendingKind && p.UpdatedAt >= cutoff, ct);
        if (candidate is null) return NotFound();
        // Bind approval to the same URL the administrator reviewed.
        if (!string.Equals(candidate.Url, request.Url, StringComparison.Ordinal)) return Conflict();

        candidate.Kind = request.Approve ? WebWatchSearch.ApprovedKind : WebWatchSearch.RejectedKind;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed record ReviewRequest(bool Approve, string Url);
}
