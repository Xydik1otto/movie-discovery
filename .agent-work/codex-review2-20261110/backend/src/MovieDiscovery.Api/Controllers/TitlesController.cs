using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Tmdb;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/title")]
public sealed class TitlesController(TitleService titles, SimilarityService similarity, MovieDiscoveryDbContext db) : ControllerBase
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
            // FR-14: компактні бейджі «де подивитись» (лише is_available=true)
            WatchProviders = (await Infrastructure.Watchability.WatchLinkLoader.LoadAvailableAsync(db, [id], ct))
                .TryGetValue(id, out var wp)
                ? wp.Select(w => new CardWatchProvider(w.Provider, w.Kind, w.Url)).ToList()
                : [],
            Status = t.Status,
            OriginalLanguage = t.OriginalLanguage,
            // UX-05: кандидати трейлерів (ok + не-верифіковані; не-ok синк-рядки не віддаються).
            // On-demand search тут НЕ запускається (квота) — лише GET /api/titles/{id}/trailers.
            Trailers = TrailersController.ToInfos(
                await TrailersController.LoadRowsAsync(db, id, ct)),
            // аніме/манга (FR-12)
            Format = card.Format,
            Episodes = card.Episodes,
            Chapters = card.Chapters,
            Volumes = card.Volumes,
            Studios = card.Studios,
            Authors = card.Authors,
            ScoreMal = card.ScoreMal,
            ScoreAnilist = card.ScoreAnilist,
            ScoreShikimori = card.ScoreShikimori,
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

        var scored = await similarity.SimilarByTitleAsync(seed, limit, langSafe, filters: null, ct);
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
            // Codex IV (6): Score.Value — ранжувальний сигнал у SimilarityEngine (same-type
            // бонус ×1.1 свідомо виходить за 1.0); у DTO чесна ДИСПЛЕЙ-семантика: % збігу ≤100,
            // score>1 → 1.0 (максимальний збіг). Ранжування у сервісі лишається на сирових значеннях.
            Score = Math.Round(Math.Min(s.Score.Value, 1.0), 4),
            // FR-21: причини — машинні коди (shared_genres:<genre> / shared_actor:<ім'я> / decade:<YYYY0>s),
            // фронт локалізує; ліміти — жанри ≤3, актори ≤2.
            Reasons = SimilarityReasons.Build(s.Score, s.Title.Year, s.SeedYear),
        };
        return item;
    }
}
