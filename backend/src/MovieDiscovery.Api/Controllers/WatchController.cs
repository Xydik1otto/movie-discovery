using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// «Де подивитись» (FR-14): усі провайдери тайтла з watch_links.
/// FR-19: на кожному провайдері audioLang/quality/dub (nullable).
/// FR-20: ?enrich=youtube — on-demand пошук YouTube для ТОГО тайтла, якщо youtube-лінку нема
/// (1 search/запит, кеш 24 год; без ключа або з наявним лінком — тихий no-op).
/// </summary>
[ApiController]
[Route("api/titles/{id:int}")]
public sealed class WatchController(
    MovieDiscoveryDbContext db,
    YouTubeEnrichService youtubeEnrich) : ControllerBase
{
    [HttpGet("watch")]
    public async Task<IActionResult> Get(int id, string? enrich, CancellationToken ct)
    {
        var exists = await db.Titles.AnyAsync(t => t.Id == id, ct);
        if (!exists)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        // FR-20: on-demand збагачення (кеш; 1 search максимум, лише якщо лінку ще нема)
        if (string.Equals(enrich?.Trim(), "youtube", StringComparison.OrdinalIgnoreCase))
            await youtubeEnrich.EnsureYoutubeLinkAsync(id, ct);

        var links = await db.WatchLinks.AsNoTracking()
            .Where(w => w.TitleId == id)
            .OrderBy(w => w.Provider).ThenBy(w => w.Kind)
            .Select(w => new WatchProviderInfo(
                w.Provider, w.Kind, w.Region, w.Url, w.IsAvailable,
                w.AudioLang, w.Quality, w.Dub))
            .ToListAsync(ct);

        return Ok(new TitleWatchResponse { Providers = links });
    }
}
