using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// Порядок перегляду серії: рік ASC (NULL — в кінець), потім назва (ordinal).
/// Чиста функція — гейтується тестами (TitleSeriesTests).
/// </summary>
public static class SeriesOrdering
{
    public static int Compare(int? aYear, string? aTitle, int? bYear, string? bTitle)
    {
        var ya = aYear ?? int.MaxValue;
        var yb = bYear ?? int.MaxValue;
        if (ya != yb) return ya.CompareTo(yb);
        return string.CompareOrdinal(aTitle ?? "", bTitle ?? "");
    }
}

/// <summary>
/// «Фільми з тієї ж лінійки» (FR): GET api/titles/{id}/series — група франшизи за
/// title_series (Wikidata P179), упорядкована як порядок перегляду (рік ASC, NULL
/// — в кінець, потім назва), з per-account позначками watched/watchlist.
/// Анонім — 200 з watched=false/watchlist=false (не 401).
/// </summary>
[ApiController]
[Route("api/titles/{id:int}/series")]
public sealed class SeriesController(
    MovieDiscoveryDbContext db,
    TasteProfileService taste) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int id, [FromQuery] string? lang, CancellationToken ct)
    {
        var langSafe = SearchController.Lang(lang);

        var title = await db.Titles.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (title is null)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        // Поза серією (немає imdb_id або imdb_id не в title_series) → порожня відповідь 200.
        var imdbId = title.ImdbId;
        if (string.IsNullOrWhiteSpace(imdbId))
            return Ok(Empty());

        var own = await db.TitleSeries.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ImdbId == imdbId, ct);
        if (own is null || string.IsNullOrWhiteSpace(own.SeriesQid))
            return Ok(Empty());

        var seriesQid = own.SeriesQid;

        // Група = ВСІ рядки title_series з тим самим SeriesQid → тайтли каталогу за imdb_id.
        var groupImdbIds = await db.TitleSeries.AsNoTracking()
            .Where(s => s.SeriesQid == seriesQid)
            .Select(s => s.ImdbId)
            .ToListAsync(ct);

        var groupTitles = await db.Titles.AsNoTracking()
            .Where(t => t.ImdbId != null && groupImdbIds.Contains(t.ImdbId))
            .ToListAsync(ct);

        // Позначки акаунта: cookie-юзер → його постійний профіль (FR-16, як у ListsController);
        // анонім/bot-незалежний шлях — без позначок (200, не 401).
        int? profileId = null;
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        if (User.Identity?.IsAuthenticated == true && userId.Length > 0)
            profileId = (await taste.ResolveAsync(userId, ct)).ProfileId;

        var groupTitleIds = groupTitles.Select(t => t.Id).ToList();
        var marks = profileId is { } pid
            ? await db.ListEntries.AsNoTracking()
                .Where(l => l.ProfileId == pid
                            && groupTitleIds.Contains(l.TitleId)
                            && (l.Kind == ListKinds.Watched || l.Kind == ListKinds.Watchlist))
                .Select(l => new { l.TitleId, l.Kind })
                .ToListAsync(ct)
            : [];
        var watchedIds = marks.Where(m => m.Kind == ListKinds.Watched).Select(m => m.TitleId).ToHashSet();
        var watchlistIds = marks.Where(m => m.Kind == ListKinds.Watchlist).Select(m => m.TitleId).ToHashSet();

        var items = groupTitles
            .Select(t =>
            {
                var card = TitleMapper.ToCard(t, langSafe); // постер = ImgUrl(poster_path, w500)
                return new SeriesItem(
                    t.Id,
                    card.Title,
                    t.Year,
                    card.PosterUrl,
                    t.Type,
                    watchedIds.Contains(t.Id),
                    watchlistIds.Contains(t.Id),
                    t.Id == id);
            })
            .ToList();
        items.Sort((a, b) => SeriesOrdering.Compare(a.Year, a.Title, b.Year, b.Title));

        return Ok(new
        {
            seriesKey = seriesQid,
            seriesName = SeriesName(langSafe, own.SeriesLabelUk, own.SeriesLabelRu, own.SeriesLabelEn),
            items,
        });
    }

    /// <summary>Назва серії: uk → uk ?? ru ?? en; ru → ru ?? uk ?? en (10.11); en → en ?? uk ?? ru (лапки/порожні — null).</summary>
    public static string? SeriesName(string lang, string? uk, string? ru, string? en) =>
        string.Equals(lang, "uk", StringComparison.OrdinalIgnoreCase)
            ? NonEmpty(uk) ?? NonEmpty(ru) ?? NonEmpty(en)
            : string.Equals(lang, "ru", StringComparison.OrdinalIgnoreCase)
                ? NonEmpty(ru) ?? NonEmpty(uk) ?? NonEmpty(en)
                : NonEmpty(en) ?? NonEmpty(uk) ?? NonEmpty(ru);

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static object Empty() => new
    {
        seriesKey = (string?)null,
        seriesName = (string?)null,
        items = Array.Empty<SeriesItem>(),
    };
}

/// <summary>Елемент серії: контракт відповіді (id/title/year/posterUrl/type + позначки акаунта).</summary>
public sealed record SeriesItem(
    int Id,
    string Title,
    int? Year,
    string? PosterUrl,
    string Type,
    bool Watched,
    bool Watchlist,
    bool IsCurrent);
