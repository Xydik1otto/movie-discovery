using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// Розділи «Аніме» і «Манга» (FR-12): тонкі обгортки над /api/search з type=anime|manga
/// та дефолтним сортуванням за популярністю для UI-розділів.
/// </summary>
[ApiController]
public sealed class AnimeCatalogController(SearchService search) : ControllerBase
{
    [HttpGet("api/anime/catalog")]
    public Task<IActionResult> Anime(
        [FromQuery] string? q, [FromQuery] string? genres, [FromQuery] string? sort,
        [FromQuery] string? lang, [FromQuery] int page = 1, [FromQuery] int pageSize = 24,
        CancellationToken ct = default) =>
        CatalogAsync("anime", q, genres, sort, lang, page, pageSize, ct);

    [HttpGet("api/manga/catalog")]
    public Task<IActionResult> Manga(
        [FromQuery] string? q, [FromQuery] string? genres, [FromQuery] string? sort,
        [FromQuery] string? lang, [FromQuery] int page = 1, [FromQuery] int pageSize = 24,
        CancellationToken ct = default) =>
        CatalogAsync("manga", q, genres, sort, lang, page, pageSize, ct);

    private async Task<IActionResult> CatalogAsync(
        string type, string? q, string? genres, string? sort, string? lang,
        int page, int pageSize, CancellationToken ct)
    {
        var langSafe = SearchController.Lang(lang);
        var genreList = SearchController.ParseGenres(genres);
        if (genreList is null)
            return BadRequest(new { error = "invalid_request", message = "Некоректний фільтр жанрів" });

        var result = await search.SearchAsync(new SearchQuery(
            q, type, genreList, null, null, null, null, null, null, null, null,
            string.IsNullOrWhiteSpace(sort) ? "popularity" : sort!.Trim(),
            page, pageSize), ct);

        return Ok(new
        {
            items = result.Items.Select(t => TitleMapper.ToCard(t, langSafe)).ToList(),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }
}
