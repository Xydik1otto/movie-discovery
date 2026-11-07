using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Core.Text;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Services;

public sealed record ScoredTitle(Title Title, SimilarityScore Score);

/// <summary>
/// Оркестрація схожості: будує профіль еталонів (вектори з БД, TMDB-similar id з кешу),
/// вибирає пул кандидатів локально, скорить чистим движком Core і повертає топ-N.
/// </summary>
public sealed class SimilarityService(
    MovieDiscoveryDbContext db,
    SimilarityEngine engine,
    ICacheStore cache,
    ILogger<SimilarityService> log)
{
    public const int CandidateCap = 2000;

    /// <summary>Схожість від картки (1 seed).</summary>
    public Task<List<ScoredTitle>> SimilarByTitleAsync(Title seed, int limit, string lang, CancellationToken ct = default) =>
        SimilarBySeedsAsync([seed], limit, lang, ct);

    /// <summary>Multi-seed: 1..10 еталонів.</summary>
    public async Task<List<ScoredTitle>> SimilarBySeedsAsync(
        IReadOnlyList<Title> seeds, int limit, string lang, CancellationToken ct = default)
    {
        var seedFeatures = await BuildSeedFeaturesAsync(seeds, ct);
        var seedAgg = SeedAggregate.FromSeeds(seedFeatures);
        var candidates = await LoadCandidatesAsync(seedAgg, ct);
        return await ScoreAndRankAsync(seedAgg, candidates, limit, lang, tokenizeKeywords: false, ct);
    }

    /// <summary>
    /// Вільний текст: (результати, semanticUnavailable). Якщо Ollama недоступна і з тексту
    /// нічого не витягнуто → semanticUnavailable=true (контролер віддає 503).
    /// </summary>
    public async Task<(List<ScoredTitle>? Results, bool SemanticUnavailable)> SimilarByTextAsync(
        string text, int limit, string lang, IEmbedder embedder, CancellationToken ct = default)
    {
        var signals = KeywordExtractor.Extract(text);
        var vector = await embedder.EmbedAsync(text, ct);

        if (vector is null && signals.IsEmpty)
            return (null, true);

        var seedAgg = new SeedAggregate
        {
            SeedTmdbIds = [],
            Type = null, // тип невідомий — бонус same-type не застосовується
            Genres = signals.Genres.Select(g => new WeightedFeature(g, 1.0)).ToList(),
            Keywords = signals.Keywords.Select(k => new WeightedFeature(k, 1.0)).ToList(),
            Actors = [],
            Directors = [],
            Embedding = vector,
            TmdbSimilarWeights = new Dictionary<int, double>(),
        };

        var candidates = await LoadByTextCandidatesAsync(seedAgg, signals, vector is not null, ct);
        var results = await ScoreAndRankAsync(seedAgg, candidates, limit, lang, tokenizeKeywords: true, ct);
        return (results, false);
    }

    // ─── Побудова профілю еталонів ───

    private async Task<List<TitleFeatures>> BuildSeedFeaturesAsync(IReadOnlyList<Title> seeds, CancellationToken ct)
    {
        var ids = seeds.Select(s => s.Id).ToList();
        var vecs = await db.Embeddings.AsNoTracking()
            .Where(e => ids.Contains(e.TitleId))
            .ToDictionaryAsync(e => e.TitleId, e => e.Vec, ct);
        var similarIds = await LoadTmdbSimilarIdsAsync(seeds, ct);

        return seeds.Select(s => new TitleFeatures
        {
            TmdbId = s.TmdbId,
            Type = s.Type,
            Genres = s.Genres,
            Keywords = s.Keywords,
            Actors = s.Actors,
            Director = s.Director,
            Year = s.Year,
            Rating = s.RatingTmdb ?? s.RatingImdb,
            RuntimeMin = s.RuntimeMin ?? s.EpisodeRuntimeMin,
            Embedding = vecs.TryGetValue(s.Id, out var v) ? v.ToArray() : null,
            TmdbSimilarIds = similarIds.TryGetValue(s.Id, out var set) ? set : null,
        }).ToList();
    }

    /// <summary>TMDB similar/recommended id еталонів — з безстрокового кешу деталей (якщо тайтл уже відкривали).</summary>
    private async Task<Dictionary<int, HashSet<int>>> LoadTmdbSimilarIdsAsync(
        IReadOnlyList<Title> seeds, CancellationToken ct)
    {
        var result = new Dictionary<int, HashSet<int>>();
        foreach (var s in seeds)
        {
            var payload = await cache.GetAsync(
                TmdbCacheKeys.Source, TmdbCacheKeys.Details(s.Type, s.TmdbId, "en-US"), ct);
            if (payload is null) continue;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                var ids = new HashSet<int>();
                CollectListIds(root, "similar", ids);
                CollectListIds(root, "recommendations", ids);
                if (ids.Count > 0) result[s.Id] = ids;
            }
            catch (JsonException)
            {
                // пошкоджений кеш — пропускаємо сигнал
            }
        }
        return result;
    }

    private static void CollectListIds(JsonElement root, string prop, HashSet<int> ids)
    {
        if (!root.TryGetProperty(prop, out var list) || list.ValueKind != JsonValueKind.Object) return;
        if (!list.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return;
        foreach (var item in results.EnumerateArray())
            if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                && idEl.TryGetInt32(out var id))
                ids.Add(id);
    }

    // ─── Пул кандидатів ───

    private async Task<List<Title>> LoadCandidatesAsync(SeedAggregate seed, CancellationToken ct)
    {
        var seedLocalIds = await db.Titles.AsNoTracking()
            .Where(t => seed.SeedTmdbIds.Contains(t.TmdbId))
            .Select(t => t.Id)
            .ToListAsync(ct);

        var genres = seed.Genres.Select(g => g.Value).ToArray();
        var keywords = seed.Keywords.Select(k => k.Value).ToArray();
        var actors = seed.Actors.Select(a => a.Value).ToArray();
        var anyMeta = genres.Length > 0 || keywords.Length > 0 || actors.Length > 0;

        var pool = db.Titles.AsNoTracking();
        if (seedLocalIds.Count > 0)
            pool = pool.Where(t => !seedLocalIds.Contains(t.Id));
        if (anyMeta)
            pool = pool.Where(t =>
                t.Genres.Any(g => genres.Contains(g)) ||
                t.Keywords.Any(k => keywords.Contains(k)) ||
                t.Actors.Any(a => actors.Contains(a)));

        return await pool
            .OrderByDescending(t => t.Popularity)
            .Take(CandidateCap)
            .ToListAsync(ct);
    }

    private async Task<List<Title>> LoadByTextCandidatesAsync(
        SeedAggregate seed, ExtractedSignals signals, bool hasVector, CancellationToken ct)
    {
        var pools = new List<List<Title>>();

        // мета-пул: жанри (overlap) або keywords ILIKE по токенах
        var genres = signals.Genres.ToArray();
        var tokens = signals.Keywords.Where(k => !k.Contains(' ', StringComparison.Ordinal)).ToArray();
        var phrases = signals.Keywords.Where(k => k.Contains(' ', StringComparison.Ordinal)).ToArray();
        var patterns = tokens.Select(t => "%" + SearchService.EscapeLike(t) + "%")
            .Concat(phrases.Select(p => "%" + SearchService.EscapeLike(p) + "%"))
            .ToArray();

        if (genres.Length > 0 && patterns.Length > 0)
        {
            pools.Add(await db.Titles.FromSqlInterpolated($@"
SELECT * FROM titles
WHERE genres && {genres}
   OR EXISTS (SELECT 1 FROM unnest(keywords) k WHERE k ILIKE ANY({patterns}))
ORDER BY popularity DESC
LIMIT 1500").ToListAsync(ct));
        }
        else if (genres.Length > 0)
        {
            pools.Add(await db.Titles.AsNoTracking()
                .Where(t => t.Genres.Any(g => genres.Contains(g)))
                .OrderByDescending(t => t.Popularity)
                .Take(1500)
                .ToListAsync(ct));
        }
        else if (patterns.Length > 0)
        {
            pools.Add(await db.Titles.FromSqlInterpolated($@"
SELECT * FROM titles
WHERE EXISTS (SELECT 1 FROM unnest(keywords) k WHERE k ILIKE ANY({patterns}))
ORDER BY popularity DESC
LIMIT 1500").ToListAsync(ct));
        }

        // семантичний пул: тайтли з векторами
        if (hasVector)
        {
            var vecIds = db.Embeddings.AsNoTracking().Select(e => e.TitleId);
            pools.Add(await db.Titles.AsNoTracking()
                .Where(t => vecIds.Contains(t.Id))
                .OrderByDescending(t => t.Popularity)
                .Take(3000)
                .ToListAsync(ct));
        }

        // merge, dedupe, без порожніх
        var seen = new HashSet<int>();
        var merged = new List<Title>();
        foreach (var pool in pools)
        {
            foreach (var t in pool)
            {
                if (string.IsNullOrEmpty(t.OverviewEn) && string.IsNullOrEmpty(t.OverviewUk)) continue;
                if (seen.Add(t.Id)) merged.Add(t);
                if (merged.Count >= 4000) break;
            }
            if (merged.Count >= 4000) break;
        }
        return merged;
    }

    // ─── Скоринг і ранжування ───

    private async Task<List<ScoredTitle>> ScoreAndRankAsync(
        SeedAggregate seedAgg,
        List<Title> candidates,
        int limit,
        string lang,
        bool tokenizeKeywords,
        CancellationToken ct)
    {
        var candidateIds = candidates.Select(t => t.Id).ToList();
        var vecs = candidateIds.Count == 0
            ? new Dictionary<int, Pgvector.Vector>()
            : await db.Embeddings.AsNoTracking()
                .Where(e => candidateIds.Contains(e.TitleId))
                .ToDictionaryAsync(e => e.TitleId, e => e.Vec, ct);

        var scored = new List<ScoredTitle>(candidates.Count);
        foreach (var t in candidates)
        {
            var features = new TitleFeatures
            {
                TmdbId = t.TmdbId,
                Type = t.Type,
                Genres = t.Genres,
                Keywords = tokenizeKeywords ? TokenizeKeywords(t.Keywords) : t.Keywords,
                Actors = t.Actors,
                Director = t.Director,
                Year = t.Year,
                Rating = t.RatingTmdb ?? t.RatingImdb,
                RuntimeMin = t.RuntimeMin ?? t.EpisodeRuntimeMin,
                Embedding = vecs.TryGetValue(t.Id, out var v) ? v.ToArray() : null,
            };
            var score = engine.Score(seedAgg, features);
            if (score.Value <= 0) continue;
            scored.Add(new ScoredTitle(t, score));
        }

        var top = scored
            .OrderByDescending(s => s.Score.Value)
            .ThenByDescending(s => s.Title.Popularity)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();

        log.LogInformation("Схожість: seeds={Seeds}, кандидатів={Candidates}, оцінено={Scored}, віддано={Top}",
            seedAgg.SeedCount, candidates.Count, scored.Count, top.Count);
        return top;
    }

    /// <summary>TMDB-keywords — фрази («dark comedy»); для пошуку за токенами розкладаємо на слова.</summary>
    public static string[] TokenizeKeywords(string[] keywords)
    {
        if (keywords.Length == 0) return [];
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phrase in keywords)
        {
            foreach (var word in phrase.Split([' ', '-', ',', ':'], StringSplitOptions.RemoveEmptyEntries))
            {
                var w = word.Trim().ToLowerInvariant();
                if (w.Length >= 3) tokens.Add(w);
            }
        }
        return tokens.Take(30).ToArray();
    }
}
