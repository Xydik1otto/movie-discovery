using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/title")]
public sealed class TitlesController(TitleService titles, SimilarityService similarity) : ControllerBase
{
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, [FromQuery] string? lang, CancellationToken ct)
    {
        var langSafe = SearchController.Lang(lang);
        var detail = await titles.GetDetailAsync(id, langSafe, ct);
        if (detail is null)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        var t = detail.Title;
        var card = TitleMapper.ToCard(t, langSafe);
        var uk = SimilarityTexts.IsUk(langSafe);
        return Ok(new TitleDetailResponse
        {
            Id = card.Id,
            TmdbId = card.TmdbId,
            Type = card.Type,
            Title = card.Title,
            OriginalTitle = card.OriginalTitle,
            Year = card.Year,
            Genres = card.Genres,
            Countries = card.Countries,
            RatingTmdb = card.RatingTmdb,
            RatingImdb = card.RatingImdb,
            PosterUrl = card.PosterUrl,
            RuntimeMin = card.RuntimeMin,
            Seasons = card.Seasons,
            Overview = card.Overview,
            Actors = [.. t.Actors],
            Director = t.Director,
            Keywords = [.. t.Keywords],
            OverviewFull = uk ? (t.OverviewUk ?? t.OverviewEn) : t.OverviewEn,
            BackdropUrl = TitleMapper.ImgUrl(t.BackdropPath, "w780"),
            TrailerYtId = t.TrailerYtId,
            Providers = detail.Providers
                .Select(p => new ProviderInfo(p.ProviderName, p.Kind, p.Url, p.Source))
                .ToList(),
            Status = t.Status,
            OriginalLanguage = t.OriginalLanguage,
        });
    }

    /// <summary>«Схожі» від картки: 1..limit результатів з поясненнями (uk/en).</summary>
    [HttpGet("{id:int}/similar")]
    public async Task<IActionResult> Similar(int id, [FromQuery] int limit = 20, [FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var langSafe = SearchController.Lang(lang);
        var seed = await titles.ResolveAsync(id, ct);
        if (seed is null)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        var scored = await similarity.SimilarByTitleAsync(seed, limit, langSafe, ct);
        return Ok(new { items = scored.Select(s => ToSimilarItem(s, langSafe)).ToList() });
    }

    internal static SimilarItem ToSimilarItem(ScoredTitle s, string lang)
    {
        var card = TitleMapper.ToCard(s.Title, lang);
        var item = new SimilarItem
        {
            Id = card.Id,
            TmdbId = card.TmdbId,
            Type = card.Type,
            Title = card.Title,
            OriginalTitle = card.OriginalTitle,
            Year = card.Year,
            Genres = card.Genres,
            Countries = card.Countries,
            RatingTmdb = card.RatingTmdb,
            RatingImdb = card.RatingImdb,
            PosterUrl = card.PosterUrl,
            RuntimeMin = card.RuntimeMin,
            Seasons = card.Seasons,
            Overview = card.Overview,
            Score = Math.Round(s.Score.Value, 4),
            Reasons = SimilarityTexts.BuildReasons(s.Score.Contributions, 1, lang),
        };
        return item;
    }
}
