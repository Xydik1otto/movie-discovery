using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Api.Controllers;

/// <summary>«Де подивитись» (FR-14): усі провайдери тайтла з watch_links.</summary>
[ApiController]
[Route("api/titles/{id:int}")]
public sealed class WatchController(MovieDiscoveryDbContext db) : ControllerBase
{
    [HttpGet("watch")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var exists = await db.Titles.AnyAsync(t => t.Id == id, ct);
        if (!exists)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        var links = await db.WatchLinks.AsNoTracking()
            .Where(w => w.TitleId == id)
            .OrderBy(w => w.Provider).ThenBy(w => w.Kind)
            .Select(w => new WatchProviderInfo(w.Provider, w.Kind, w.Region, w.Url, w.IsAvailable))
            .ToListAsync(ct);

        return Ok(new TitleWatchResponse { Providers = links });
    }
}
