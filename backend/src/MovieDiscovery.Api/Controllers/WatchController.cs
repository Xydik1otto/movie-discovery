using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Sync;
using MovieDiscovery.Infrastructure.Tmdb;
using MovieDiscovery.Infrastructure.Watchability;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// «Де подивитись» (FR-14 + UX-04): УНІФІКОВАНИЙ список варіантів перегляду — злиття
/// watch_links (перевірені лінки + метадані FR-19/UX-04) і providers (каталог) —
/// WatchOptionsMerger (дедуп provider+url+мова-сигнатура, мовні варіанти не губляться).
/// UX-04 (хвиля 2):
///   • isAvailable — TRI-STATE: true лише для ПЕРЕВІРЕНИХ рядків (watch_links з живою
///     перевіркою); неперевірений каталог → unknown («доступність неперевірена»),
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
    WatchSyncQueue syncQueue,
    WebWatchQueue webQueue,
    IOptions<TmdbOptions> tmdbOptions) : ControllerBase
{
    [HttpGet("watch")]
    [EnableRateLimiting(YouTubeRatePolicy.PolicyName)] // 10/хв/IP — ЛИШЕ для ?enrich=youtube (див. RateLimitPolicies.cs)
    public async Task<IActionResult> Get(int id, string? enrich, string? region, string? searchCountry, CancellationToken ct)
    {
        var country = string.IsNullOrWhiteSpace(region) ? null : region.Trim().ToUpperInvariant();
        var webCountry = string.IsNullOrWhiteSpace(searchCountry) ? country : searchCountry.Trim().ToUpperInvariant();
        if ((country is not null && country != "*" && !IsCountryCode(country)) ||
            (webCountry is not null && webCountry != "*" && !IsCountryCode(webCountry)))
            return BadRequest(new { error = "invalid_region", message = "Вкажіть дволітерний код країни або * для глобальних пропозицій" });

        var title = await db.Titles.AsNoTracking().Where(t => t.Id == id)
            .Select(t => new { t.Type, t.TmdbId, t.ImdbId, t.TitleEn, t.TitleUk, t.Year })
            .SingleOrDefaultAsync(ct);
        if (title is null)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        var discoveryPending = tmdbOptions.Value.AutomaticWatchDiscoveryEnabled &&
            !string.IsNullOrWhiteSpace(tmdbOptions.Value.ApiKey) &&
            TmdbProvidersService.CanDiscover(title.Type, title.TmdbId, title.ImdbId) &&
            syncQueue.TryEnqueueTitle(id);
        if (webCountry is not null)
            discoveryPending |= webQueue.TryEnqueue(id, title.Type, title.Year, webCountry);

        // FR-20: on-demand збагачення (кеш; 1 search максимум, лише якщо лінку ще нема)
        if (string.Equals(enrich?.Trim(), "youtube", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await youtubeEnrich.EnsureYoutubeLinkAsync(id, ct);
            }
            catch (YouTubeQuotaExceededException ex)
            {
                // Денний глобальний ліміт search-викликів (90/день, YouTubeDailySearchQuota) вичерпано.
                return StatusCode(429, new { error = "quota_exceeded", message = ex.Message });
            }
        }

        var links = await db.WatchLinks.AsNoTracking()
            .Where(w => w.TitleId == id && w.Provider != TmdbProvidersService.LegacyProviderName &&
                (country == null || w.Region == country || w.Region == "*"))
            .OrderBy(w => w.Provider).ThenBy(w => w.Kind)
            .Select(w => new { w.Provider, w.Kind, w.Url, w.Region, w.IsAvailable, w.AudioLang, w.Quality, w.Dub, w.AudioLangs, w.SubLangs, w.Coverage })
            .ToListAsync(ct);

        var candidateCutoff = DateTime.UtcNow - WebWatchSearch.CandidateLifetime;
        var catalog = await db.Providers.AsNoTracking()
            .Where(p => p.TitleId == id && (country == null || p.Region == country || p.Region == "*") &&
                (p.Source != WebWatchSearch.Source || (title.Type != "manga" && p.UpdatedAt >= candidateCutoff &&
                    (p.Kind == WebWatchSearch.CandidateKind || p.Kind == WebWatchSearch.ApprovedKind))))
            .Select(p => new { Provider = p.ProviderName, p.Kind, p.Url, p.Source, p.Region, p.AudioLangs, p.SubLangs, p.Price, p.Currency, p.Coverage })
            .ToListAsync(ct);

        var candidates = catalog.Where(p => p.Source == WebWatchSearch.Source && p.Url != null)
            .OrderBy(p => p.Provider).ThenBy(p => p.Region)
            .Select(p => new WatchSearchLink(p.Provider, p.Region, p.Url!, p.Source)).ToList();
        catalog.RemoveAll(p => p.Source == WebWatchSearch.Source);

        var merged = WatchOptionsMerger.Merge(
            links.Select(w => new WatchOptionRow(
                w.Provider, w.Kind, w.Url, null, w.Region, w.IsAvailable, w.AudioLang, w.Quality, w.Dub,
                w.AudioLangs, w.SubLangs)),
            catalog.Select(p => new WatchOptionRow(
                p.Provider, p.Kind, p.Url, p.Source, p.Region, null, null, null, null,
                p.AudioLangs, p.SubLangs, p.Price, p.Currency)));

        // Покриття (watch-верифікатор) — властивість ЦІЛЬОВОГО URL (full = повний фільм/весь
        // серіал, episode = фрагмент/епізод, NULL = не перевірено). Дедуп не міняє URL, тож
        // coverage збираємо з обох джерел за (provider, url, region) — перший non-null перемагає
        // (watch_links першими: верифіковані рядки).
        var coverageByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var w in links)
            if (w.Url is { } wu && w.Coverage is { } wc)
                coverageByKey.TryAdd(CoverageKey(w.Provider, wu, w.Region), wc);
        foreach (var p in catalog)
            if (p.Url is { } pu && p.Coverage is { } pc)
                coverageByKey.TryAdd(CoverageKey(p.Provider, pu, p.Region), pc);

        return Ok(new TitleWatchResponse
        {
            DiscoveryPending = discoveryPending,
            SearchLinks = [.. candidates, .. WatchProviderDirectory.Get(title.Type,
                string.IsNullOrWhiteSpace(title.TitleEn) ? title.TitleUk ?? "" : title.TitleEn, country)],
            Providers = [.. merged.Select(o =>
            {
                var urlKind = WatchUrlClassifier.Classify(o.Url);
                bool? verified = urlKind == WatchUrlClassifier.Direct ? o.IsAvailable : null;

                // Полний tri-state у контракті: available | unavailable | unknown
                // (unknown = неперевірений каталог АБО bot-wall/таймаут перевірки).
                var status = verified switch
                {
                    true => "available",
                    false => "unavailable",
                    null => "unknown",
                };

                return new WatchProviderInfo(
                    o.Provider, o.Kind, o.Region ?? "", o.Url, verified == true, o.AudioLang, o.Quality, o.Dub)
                {
                    Source = o.Source,
                    AudioLangs = o.AudioLangs,
                    SubLangs = o.SubLangs,
                    Price = o.Price,
                    Currency = o.Currency,
                    UrlKind = urlKind,
                    AvailabilityStatus = status,
                    Coverage = urlKind == WatchUrlClassifier.Direct && o.Url is { } covUrl &&
                        coverageByKey.TryGetValue(CoverageKey(o.Provider, covUrl, o.Region), out var cov)
                        ? cov
                        : null,
                };
            })],
        });
    }

    private static bool IsCountryCode(string code) => code.Length == 2 && code.All(char.IsAsciiLetterUpper);

    private static string CoverageKey(string provider, string url, string? region) =>
        $"{provider.Trim().ToLowerInvariant()}\u0001{url.Trim()}\u0001{region?.Trim().ToUpperInvariant()}";
}
