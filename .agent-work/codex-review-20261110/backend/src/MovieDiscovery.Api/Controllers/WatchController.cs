using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watchability;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// «Де подивитись» (FR-14): УНІФІКОВАНИЙ список варіантів перегляду — злиття watch_links
/// (перевірені лінки + метадані FR-19) і providers (каталог JustWatch/TMDB) — рішення
/// власника 09.11: усі провайдери (безкоштовні + платні), фронт ховає платні за перемикачем.
/// kind нормалізується до free|stream|rent|buy|ads (subscription → stream); дедуп за
/// (provider+url); порядок: спочатку free/ads, далі за пріоритетом джерела
/// (sweettv, megogo, plex, youtube, internet-archive, justwatch) — див. WatchOptionsMerger.
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
            .Select(w => new { w.Provider, w.Kind, w.Url, w.Region, w.IsAvailable, w.AudioLang, w.Quality, w.Dub })
            .ToListAsync(ct);

        var catalog = await db.Providers.AsNoTracking()
            .Where(p => p.TitleId == id)
            .Select(p => new { Provider = p.ProviderName, p.Kind, p.Url, p.Source, p.Region })
            .ToListAsync(ct);

        var merged = WatchOptionsMerger.Merge(
            links.Select(w => new WatchOptionRow(
                w.Provider, w.Kind, w.Url, null, w.Region, w.IsAvailable, w.AudioLang, w.Quality, w.Dub)),
            catalog.Select(p => new WatchOptionRow(
                p.Provider, p.Kind, p.Url, p.Source, p.Region)));

        return Ok(new TitleWatchResponse
        {
            Providers = [.. merged.Select(o => new WatchProviderInfo(
                o.Provider, o.Kind, o.Region, o.Url, o.IsAvailable ?? true, o.AudioLang, o.Quality, o.Dub)
            {
                Source = o.Source,
            })],
        });
    }
}
