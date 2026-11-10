using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watch;
using MovieDiscovery.Infrastructure.Watchability;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// «Де подивитись» (FR-14 + UX-04): УНІФІКОВАНИЙ список варіантів перегляду — злиття
/// watch_links (перевірені лінки + метадані FR-19/UX-04) і providers (каталог) —
/// WatchOptionsMerger (дедуп provider+url+мова-сигнатура, мовні варіанти не губляться).
/// UX-04 (хвиля 2):
///   • isAvailable — TRI-STATE: true лише для ПЕРЕВІРЕНИХ рядків (watch_links з живою
///     перевіркою; для plex — після перевірки «Where to Watch» сторінки тайтла,
///     кеш обмежений); неперевірений каталог → unknown («доступність неперевірена»),
///     НЕ true і НЕ «Безкоштовно»-бейдж. Поле availabilityStatus: available|unavailable|unknown.
///   • langs — {audioLangs, subLangs} csv ISO-639-1 («en,ru»), NULL = мови невідомі
///     (джерело не заявило); НІКОЛИ не uk/ru за замовчуванням.
///   • urlKind — direct | find (класифікатор WatchUrlClassifier): find = «Знайти на
///     сервісу» (homepage/пошук/null), не пряме переглядання.
///   • price/currency — чесна ціна платного оффера, якщо джерело її дало.
/// kind нормалізується до free|stream|rent|buy|ads (subscription → stream).
/// FR-20: ?enrich=youtube — on-demand пошук YouTube для ТОГО тайтла (кеш 24 год).
/// </summary>
[ApiController]
[Route("api/titles/{id:int}")]
public sealed class WatchController(
    MovieDiscoveryDbContext db,
    YouTubeEnrichService youtubeEnrich,
    PlexAvailabilityService plexAvailability) : ControllerBase
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
            .Select(w => new { w.Provider, w.Kind, w.Url, w.Region, w.IsAvailable, w.AudioLang, w.Quality, w.Dub, w.AudioLangs, w.SubLangs })
            .ToListAsync(ct);

        var catalog = await db.Providers.AsNoTracking()
            .Where(p => p.TitleId == id)
            .Select(p => new { Provider = p.ProviderName, p.Kind, p.Url, p.Source, p.Region, p.AudioLangs, p.SubLangs, p.Price, p.Currency })
            .ToListAsync(ct);

        var merged = WatchOptionsMerger.Merge(
            links.Select(w => new WatchOptionRow(
                w.Provider, w.Kind, w.Url, null, w.Region, w.IsAvailable, w.AudioLang, w.Quality, w.Dub,
                w.AudioLangs, w.SubLangs)),
            catalog.Select(p => new WatchOptionRow(
                p.Provider, p.Kind, p.Url, p.Source, p.Region, null, null, null, null,
                p.AudioLangs, p.SubLangs, p.Price, p.Currency)));

        // UX-04 tri-state: plex-рядки з каталогу (isAvailable=null) перевіряємо живцем
        // («Where to Watch» сторінки тайтла, кеш обмежений); усе інше — як у джерела.
        var plexUrls = merged
            .Where(o => o.Source == "plex" && o.IsAvailable is null && !string.IsNullOrWhiteSpace(o.Url))
            .Select(o => o.Url!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var plexVerdicts = new Dictionary<string, PlexAvailabilityResult>(StringComparer.Ordinal);
        foreach (var url in plexUrls)
            plexVerdicts[url] = await plexAvailability.GetAsync(url, ct);

        return Ok(new TitleWatchResponse
        {
            Providers = [.. merged.Select(o =>
            {
                bool? verified = o.IsAvailable;
                if (o.Source == "plex" && verified is null &&
                    o.Url is not null && plexVerdicts.TryGetValue(o.Url.Trim(), out var verdict))
                {
                    // tri-state: available → true, unavailable → false, unknown → null
                    verified = verdict.Status switch
                    {
                        PlexAvailabilityResult.Available => true,
                        PlexAvailabilityResult.Unavailable => false,
                        _ => null,
                    };
                }

                // Полний tri-state у контракті: available | unavailable | unknown
                // (unknown = неперевірений каталог АБО bot-wall/таймаут перевірки).
                var status = verified switch
                {
                    true => "available",
                    false => "unavailable",
                    null => "unknown",
                };

                return new WatchProviderInfo(
                    o.Provider, o.Kind, o.Region, o.Url, verified == true, o.AudioLang, o.Quality, o.Dub)
                {
                    Source = o.Source,
                    AudioLangs = o.AudioLangs,
                    SubLangs = o.SubLangs,
                    Price = o.Price,
                    Currency = o.Currency,
                    UrlKind = WatchUrlClassifier.Classify(o.Url),
                    AvailabilityStatus = status,
                };
            })],
        });
    }
}
