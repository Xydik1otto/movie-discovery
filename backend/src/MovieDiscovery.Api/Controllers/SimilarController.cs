using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Core.Domain;
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
        // FR-21: опційні фільтри студії підборів (hard: type/рік; soft: жанри-акценти).
        public int? YearFrom { get; set; }
        public int? YearTo { get; set; }
        public List<string>? Types { get; set; }
        public List<string>? Genres { get; set; }
    }

    public sealed class ByTextRequest
    {
        public string? Text { get; set; }
        public int Limit { get; set; } = 20;
        public string? Lang { get; set; }
    }

    /// <summary>Multi-seed GET (FR-21): ?ids=1,2,3&limit=20&lang=uk&yearFrom=&yearTo=&types=movie,series&genres=.
    /// MD-03: ids — ЛИШЕ внутрішні title id (резолв строго по t.Id); невідомий id → 404 titles_not_found
    /// з переліком missing, без часткових результатів. Legacy tmdb-простір — лише POST by-seeds {tmdbIds}.
    /// types/рік — hard-фільтри пулу (до сортування), genres — soft-boost (бонус до score, не відсікання).</summary>
    [HttpGet("by-seeds")]
    public async Task<IActionResult> BySeedsGet(
        [FromQuery] string? ids,
        [FromQuery] int limit = 20,
        [FromQuery] string? lang = null,
        [FromQuery] int? yearFrom = null,
        [FromQuery] int? yearTo = null,
        [FromQuery] string? types = null,
        [FromQuery] string? genres = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return BadRequest(new { error = "invalid_request", message = "ids є обов'язковим (csv, від 1 до 10 ідентифікаторів)" });

        var parsed = new List<int>();
        foreach (var token in ids.Split(','))
        {
            if (!int.TryParse(token.Trim(), out var id))
                return BadRequest(new { error = "invalid_request", message = $"Некоректний id: «{token.Trim()}»" });
            parsed.Add(id);
        }
        var distinct = parsed.Distinct().ToList();
        if (distinct is not { Count: >= 1 and <= 10 })
            return BadRequest(new { error = "invalid_request", message = "ids має містити від 1 до 10 ідентифікаторів" });

        if (ValidateYears(yearFrom, yearTo) is { } badYears) return badYears;

        var filters = new SimilarFilters
        {
            YearFrom = yearFrom,
            YearTo = yearTo,
            Types = SimilarFilters.NormalizeTokens(SimilarFilters.ParseCsv(types)),
            Genres = SimilarFilters.NormalizeGenres(SimilarFilters.ParseCsv(genres), TmdbGenres.NormalizeToEn),
        };

        // MD-03: GET-простір — внутрішні id, резолв строго по t.Id (жодного tmdb-фолбеку).
        var found = await db.Titles.AsNoTracking()
            .Where(t => distinct.Contains(t.Id))
            .ToListAsync(ct);
        if (found.Count != distinct.Count)
            return NotFound(new
            {
                error = "titles_not_found",
                message = "Деякі тайтли не знайдено",
                missing = distinct.Except(found.Select(t => t.Id)).ToList(),
            });

        return await ScoreAsync(found, limit, SearchController.Lang(lang), filters, ct);
    }

    /// <summary>
    /// Multi-seed POST: 1–10 tmdbIds; незнайдені id → 404 з переліком. Фільтри FR-21 — опційні поля body.
    /// LEGACY (MD-03): подвійний резолв — tmdb_id ПЕРШИЙ, fallback на внутрішній id. Через це існує
    /// контракт колізій: при A.Id == B.TmdbId запит POST tmdbIds=A повертає B (tmdb-приорітет),
    /// тоді як GET ?ids=A повертає A (внутрішній простір). Не змінювати резолв без узгодження контракту.
    /// </summary>
    [HttpPost("by-seeds")]
    public async Task<IActionResult> BySeeds([FromBody] BySeedsRequest req, CancellationToken ct)
    {
        if (req.TmdbIds is not { Count: >= 1 and <= 10 })
            return BadRequest(new { error = "invalid_request", message = "tmdbIds має містити від 1 до 10 ідентифікаторів" });

        if (ValidateYears(req.YearFrom, req.YearTo) is { } badYears) return badYears;

        var filters = new SimilarFilters
        {
            YearFrom = req.YearFrom,
            YearTo = req.YearTo,
            Types = SimilarFilters.NormalizeTokens(req.Types ?? []),
            Genres = SimilarFilters.NormalizeGenres(req.Genres ?? [], TmdbGenres.NormalizeToEn),
        };
        return await RunBySeedsAsync(req.TmdbIds.Distinct().ToList(), req.Limit, SearchController.Lang(req.Lang), filters, ct);
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

        var items = results.Select(s => TitlesController.ToSimilarItem(s, langSafe)).ToList();
        return Ok(new SimilarItemsResponse(items));
    }

    // ─── Спільне ядро GET/POST by-seeds ───

    /// <summary>Форма відповіді схожості: {items:[SimilarItem…]} (типізовано для тестів і стабільності контракту).</summary>
    public sealed record SimilarItemsResponse(IReadOnlyList<SimilarItem> Items);

    private async Task<IActionResult> RunBySeedsAsync(
        List<int> distinct, int limit, string langSafe, SimilarFilters filters, CancellationToken ct)
    {
        // FR-21 (legacy POST): подвійний резолв — tmdb_id АБО внутрішній id (2.8 млн IMDb-рядків
        // мають tmdb_id = NULL; «Схоже на...» має працювати на всьому каталозі).
        var found = await db.Titles.AsNoTracking()
            .Where(t => (t.TmdbId != null && distinct.Contains(t.TmdbId.Value)) || distinct.Contains(t.Id))
            .ToListAsync(ct);

        var seeds = new List<Title>(distinct.Count);
        var missing = new List<int>();
        foreach (var id in distinct)
        {
            // tmdb-матч пріоритетніший (seed-контракти історично по tmdb); всередині — movie перший
            var match = found
                .Where(t => t.TmdbId == id || t.Id == id)
                .OrderBy(t => t.TmdbId != null ? 0 : 1)
                .ThenBy(t => t.Type == "movie" ? 0 : 1)
                .FirstOrDefault();
            if (match is null)
                missing.Add(id);
            else
                seeds.Add(match);
        }

        // незнайдені id → 404 ДО побудови seeds (інакше First() на порожній послідовності падає в 500)
        if (missing.Count > 0)
            return NotFound(new { error = "titles_not_found", message = "Деякі тайтли не знайдено", missing });

        return await ScoreAsync(seeds, limit, langSafe, filters, ct);
    }

    /// <summary>Спільний хвіст GET/POST by-seeds: скоринг seeds + побудова відповіді (reasons ставить
    /// TitlesController.ToSimilarItem). GET і POST проходять той самий SimilarFilters-шлях (MD-03).</summary>
    private async Task<IActionResult> ScoreAsync(
        List<Title> seeds, int limit, string langSafe, SimilarFilters filters, CancellationToken ct)
    {
        var scored = await similarity.SimilarBySeedsAsync(seeds, limit, langSafe, filters, ct);
        return Ok(new SimilarItemsResponse(scored.Select(s => TitlesController.ToSimilarItem(s, langSafe)).ToList()));
    }

    private IActionResult? ValidateYears(int? yearFrom, int? yearTo) =>
        yearFrom is int from && yearTo is int to && from > to
            ? BadRequest(new { error = "invalid_request", message = "yearFrom не може бути більшим за yearTo" })
            : null;
}
