using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/health")]
public sealed class HealthController(MovieDiscoveryDbContext db, IEmbedder embedder) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        bool dbOk;
        var titlesCount = 0;
        try
        {
            titlesCount = await db.Titles.CountAsync(ct);
            dbOk = true;
        }
        catch
        {
            dbOk = false;
        }

        var ollama = await embedder.IsAvailableAsync(ct);
        return Ok(new { status = "ok", db = dbOk, ollama, titlesCount });
    }
}
