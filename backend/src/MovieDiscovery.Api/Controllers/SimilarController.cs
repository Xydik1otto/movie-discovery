using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/similar")]
public sealed class SimilarController(
    SimilarityService similarity,
    MovieDiscoveryDbContext db,
    IEmbedder embedder) : ControllerBase
{
    public sealed class BySeedsRequest
    {
        public List<int> TmdbIds { get; set; } = [];
        public int Limit { get; set; } = 20;
        public string? Lang { get; set; }
    }

    public sealed class ByTextRequest
    {
        public string? Text { get; set; }
        public int Limit { get; set; } = 20;
        public string? Lang { get; set; }
    }

    /// <summary>Multi-seed: 1–10 tmdbIds; незнайдені id → 404 з переліком.</summary>
    [HttpPost("by-seeds")]
    public async Task<IActionResult> BySeeds([FromBody] BySeedsRequest req, CancellationToken ct)
    {
        if (req.TmdbIds is not { Count: >= 1 and <= 10 })
            return BadRequest(new { error = "invalid_request", message = "tmdbIds має містити від 1 до 10 ідентифікаторів" });

        var langSafe = SearchController.Lang(req.Lang);
        var distinct = req.TmdbIds.Distinct().ToList();
        var found = await db.Titles.AsNoTracking()
            .Where(t => t.TmdbId != null && distinct.Contains(t.TmdbId.Value))
            .ToListAsync(ct);

        // незнайдені id → 404 ДО побудови seeds (інакше First() на порожній послідовності падає в 500)
        var missing = distinct.Where(id => !found.Any(t => t.TmdbId == id)).ToList();
        if (missing.Count > 0)
            return NotFound(new { error = "titles_not_found", message = "Деякі тайтли не знайдено", missing });

        // один tmdb id теоретично може відповідати і фільму, і серіалу — беремо перший (movie пріоритетніше)
        var seeds = distinct
            .Select(id => found.Where(t => t.TmdbId == id).OrderBy(t => t.Type == "movie" ? 0 : 1).First())
            .ToList();

        var scored = await similarity.SimilarBySeedsAsync(seeds, req.Limit, langSafe, ct);
        var items = scored.Select(s =>
        {
            var item = TitlesController.ToSimilarItem(s, langSafe);
            item.Reasons = SimilarityTexts.BuildReasons(s.Score.Contributions, seeds.Count, langSafe,
                g => langSafe == "en" ? g : TmdbGenres.ToUk(g));
            return item;
        }).ToList();

        return Ok(new { items });
    }

    /// <summary>Вільний текст. Ollama недоступна → fallback Е0/Е1 (keywords/genres); нічого не спрацювало → 503.</summary>
    [HttpPost("by-text")]
    public async Task<IActionResult> ByText([FromBody] ByTextRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "invalid_request", message = "text не може бути порожнім" });

        var langSafe = SearchController.Lang(req.Lang);
        var (results, semanticUnavailable) = await similarity.SimilarByTextAsync(req.Text, req.Limit, langSafe, embedder, ct);

        if (results is null)
            return StatusCode(503, new
            {
                error = "semantic_unavailable",
                message = "Semantic index unavailable",
                detail = SimilarityTexts.IsUk(langSafe)
                    ? "Семантичний індекс недоступний (Ollama), а з тексту не вдалося витягти жанри чи ключові слова."
                    : "Semantic index is unavailable (Ollama), and no genres or keywords could be extracted from the text.",
            });

        var items = results.Select(s =>
        {
            var item = TitlesController.ToSimilarItem(s, langSafe);
            item.Reasons = SimilarityTexts.BuildReasons(s.Score.Contributions, 1, langSafe,
                g => langSafe == "en" ? g : TmdbGenres.ToUk(g));
            return item;
        }).ToList();

        return Ok(new { items });
    }
}
