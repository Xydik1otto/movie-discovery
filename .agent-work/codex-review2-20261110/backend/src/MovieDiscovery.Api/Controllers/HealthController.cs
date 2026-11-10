using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Api.Controllers;

/// <summary>MD-07: чесне здоров'я. GET /api/health — ЗАВЖДИ 200 (liveness, фронтенд на ньому живе),
/// але status чесний: «ok» при живій БД, «degraded» при недоступній; ollama=false чесно означає
/// вимкнену семантику. GET /api/health/ready — readiness: 200 при живій БД, 503 при недоступній.
/// MD-09: liveness-проба БД — дешевий SELECT 1 (CountAsync по 2.8 млн рядків давало ~2.6 с
/// на кожен виклик health, а фронтенд викликає його на кожному завантаженні сторінки);
/// titlesCount кешується на 60 с — статистика, не liveness.</summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController(
    MovieDiscoveryDbContext db,
    IEmbedder embedder,
    IMemoryCache cache) : ControllerBase
{
    private const int CountTtlSeconds = 60;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var dbOk = await ProbeDbAsync(ct);
        var ollama = await embedder.IsAvailableAsync(ct);
        return Ok(new { status = Status(dbOk), db = dbOk, ollama, titlesCount = await TitlesCountAsync(dbOk, ct) });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        var dbOk = await ProbeDbAsync(ct);
        var ollama = await embedder.IsAvailableAsync(ct);
        return dbOk
            ? Ok(new { status = Status(true), db = true, ollama, titlesCount = await TitlesCountAsync(true, ct) })
            : StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { status = Status(false), db = false, ollama, titlesCount = 0 });
    }

    /// <summary>MD-07: status=ok лише при живій БД; db=false → чесний degraded (не «ok»).</summary>
    public static string Status(bool dbOk) => dbOk ? "ok" : "degraded";

    /// <summary>Liveness-проба: дешева перевірка з'єднання (не COUNT по каталозі).</summary>
    private async Task<bool> ProbeDbAsync(CancellationToken ct)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>titlesCount — кеш 60 с: статистика для UI, а не проба живості.</summary>
    private async Task<int> TitlesCountAsync(bool dbOk, CancellationToken ct)
    {
        if (!dbOk) return 0;
        return await cache.GetOrCreateAsync("health:titles-count", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CountTtlSeconds);
            try
            {
                return await db.Titles.CountAsync(ct);
            }
            catch
            {
                return 0;
            }
        });
    }
}
