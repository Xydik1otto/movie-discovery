using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController(SearchService search) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? q,
        [FromQuery] string? type,
        [FromQuery] string? genres,
        [FromQuery] int? yearFrom,
        [FromQuery] int? yearTo,
        [FromQuery] string? country,
        [FromQuery] string? lang,
        [FromQuery] double? minRating,
        [FromQuery] double? maxRating,
        [FromQuery] int? minRuntime,
        [FromQuery] int? maxRuntime,
        [FromQuery] string? status,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 24,
        // ── Розширені фільтри ──
        [FromQuery] string? actor = null,
        [FromQuery] string? director = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? language = null,
        [FromQuery] int? minVotes = null,
        [FromQuery] int? seasonsFrom = null,
        [FromQuery] int? seasonsTo = null,
        [FromQuery] string? excludeGenres = null,
        [FromQuery] bool? hasTrailer = null,
        [FromQuery] bool? hasProviders = null,
        [FromQuery] string? ratingSource = null,
        [FromQuery] string? providerRegion = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(type) && type is not ("movie" or "series" or "show" or "anime" or "manga"))
            return BadRequest(new { error = "invalid_request", message = "type має бути movie | series | show | anime | manga" });

        var langSafe = Lang(lang);
        var genreList = ParseGenres(genres);
        if (genreList is null)
            return BadRequest(new { error = "invalid_request", message = "Некоректний фільтр жанрів" });

        var excludeList = ParseGenres(excludeGenres);
        if (excludeList is null)
            return BadRequest(new { error = "invalid_request", message = "Некоректний фільтр виключених жанрів" });

        if (!string.IsNullOrWhiteSpace(language) && !System.Text.RegularExpressions.Regex.IsMatch(language.Trim(), @"^[a-zA-Z]{2,3}$"))
            return BadRequest(new { error = "invalid_request", message = "language має бути ISO-кодом (2-3 літери)" });

        if (!string.IsNullOrWhiteSpace(ratingSource) && ratingSource is not ("tmdb" or "imdb"))
            return BadRequest(new { error = "invalid_request", message = "ratingSource має бути tmdb | imdb" });

        var result = await search.SearchAsync(new SearchQuery(
            q, type, genreList, yearFrom, yearTo, country,
            minRating, maxRating, minRuntime, maxRuntime, status,
            sort ?? "relevance", page, pageSize,
            Actor: actor, Director: director, Keyword: keyword,
            Language: language, MinVotes: minVotes,
            SeasonsFrom: seasonsFrom, SeasonsTo: seasonsTo,
            ExcludeGenres: excludeList,
            HasTrailer: hasTrailer, HasProviders: hasProviders,
            RatingSource: ratingSource, ProviderRegion: providerRegion ?? "UA"), ct);

        return Ok(new
        {
            items = result.Items.Select(t => TitleMapper.ToCard(t, langSafe)).ToList(),
            total = result.Total,
            page = result.Page,
            pageSize = result.PageSize,
        });
    }

    /// <summary>Жанри-фільтри: uk-назви нормалізуємо в канонічні en (як зберігаємо в БД).</summary>
    internal static string[]? ParseGenres(string? genres)
    {
        if (string.IsNullOrWhiteSpace(genres)) return [];
        var list = new List<string>();
        foreach (var raw in genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // канонічні жанри TMDB + жанри аніме/манги (school, mecha, slice of life...)
            var en = TmdbGenres.NormalizeToEn(raw) ?? Infrastructure.AnimeManga.AnimeGenres.NormalizeToEn(raw);
            if (en is null) return null;
            if (!list.Contains(en, StringComparer.OrdinalIgnoreCase)) list.Add(en);
        }
        return [.. list];
    }

    internal static string Lang(string? lang) =>
        string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "uk";
}
