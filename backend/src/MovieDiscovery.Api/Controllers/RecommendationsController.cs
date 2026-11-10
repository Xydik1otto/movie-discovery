using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// Персональні рекомендації Головної. Тільки для авторизованих ([Authorize] → гість 401,
/// фронт ховає секцію). Сервісна логіка — DiscoverService.GetHomeRecommendationsAsync;
/// IMemoryCache per-user TTL 5 хв (тайтли — мовно-нейтральні сутності, картки мапляться
/// на запит з lang, тому ключ без lang).
/// </summary>
[ApiController]
[Authorize]
[Route("api/recommendations")]
public sealed class RecommendationsController(DiscoverService discover, IMemoryCache cache) : ControllerBase
{
    /// <summary>Пер-юзерний TTL рекомендацій: короткий (списки живі), але достатній,
    /// щоб повторні навігації на «Головну» не перераховували той самий профіль.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>GET /api/recommendations/home?limit=20&amp;lang=uk → {items:[TitleCard], reason}.</summary>
    [HttpGet("home")]
    public async Task<IActionResult> Home(
        [FromQuery] int limit = 20,
        [FromQuery] string? lang = null,
        CancellationToken ct = default)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
        var safeLimit = Math.Clamp(limit, 1, 50);
        // Ключ per-(user, limit): різні ліміти — різні записи (дешево: пул рахується раз на 5 хв на пару).
        var reco = await cache.GetOrCreateAsync($"reco:home:{userId}:{safeLimit}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await discover.GetHomeRecommendationsAsync(userId, safeLimit, ct);
        }) ?? await discover.GetHomeRecommendationsAsync(userId, safeLimit, ct);

        var langSafe = SearchController.Lang(lang);
        var items = reco.Items.Select(t => TitleMapper.ToCard(t, langSafe)).ToList();
        return Ok(new { items, reason = ReasonText(reco, langSafe) });
    }

    /// <summary>«бо ти лайкнув жанр drama» — локалізація смак-мотиву (uk/ru — українські назви жанрів,
    /// en — ключ як є); null, коли смаку нема.</summary>
    internal static string? ReasonText(DiscoverService.HomeRecommendations reco, string lang)
    {
        if (reco.ReasonGenre is null) return null;
        var g = lang is "en" or "ru" ? reco.ReasonGenre : TmdbGenres.ToUk(reco.ReasonGenre);
        return (lang, reco.ReasonKind) switch
        {
            ("en", Core.Domain.ListKinds.Liked) => $"because you liked the {g} genre",
            ("en", _) => $"because you keep up with the {g} genre",
            ("ru", Core.Domain.ListKinds.Liked) => $"потому что ты лайкнул жанр {g}",
            ("ru", _) => $"потому что тебе интересен жанр {g}",
            (_, Core.Domain.ListKinds.Liked) => $"бо ти лайкнув жанр {g}",
            (_, _) => $"бо ти цікавишся жанром {g}",
        };
    }
}
