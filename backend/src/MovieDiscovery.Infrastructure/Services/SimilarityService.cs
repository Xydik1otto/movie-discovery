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

/// <summary>Скорений кандидат. SeedYear — рік еталона (для multi-seed — усереднений рік агрегата);
/// null, коли рік еталона невідомий (by-text) — код декади причин тоді не видається.</summary>
public sealed record ScoredTitle(Title Title, SimilarityScore Score, int? SeedYear = null);

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

    /// <summary>InMemory-провайдер (тести) не транслює масив-предикати/EXISTS-сортування — гілки адаптації.</summary>
    private bool IsInMemoryProvider =>
        !db.Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase);

    /// <summary>Схожість від картки (1 seed).</summary>
    public Task<List<ScoredTitle>> SimilarByTitleAsync(
        Title seed, int limit, string lang, SimilarFilters? filters = null, CancellationToken ct = default) =>
        SimilarBySeedsAsync([seed], limit, lang, filters, ct);

    /// <summary>Multi-seed: 1..10 еталонів. filters — опційні hard-фільтри type/рік + soft-boost жанрів (FR-21).</summary>
    public async Task<List<ScoredTitle>> SimilarBySeedsAsync(
        IReadOnlyList<Title> seeds, int limit, string lang, SimilarFilters? filters = null, CancellationToken ct = default)
    {
        var seedFeatures = await BuildSeedFeaturesAsync(seeds, ct);
        var seedAgg = SeedAggregate.FromSeeds(seedFeatures);
        var candidates = await LoadCandidatesAsync(seedAgg, filters, ct);
        return await ScoreAndRankAsync(seedAgg, candidates, limit, lang, tokenizeKeywords: false, filters, ct);
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
        var results = await ScoreAndRankAsync(seedAgg, candidates, limit, lang, tokenizeKeywords: true, filters: null, ct);
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
            if (s.TmdbId is not int seedTmdb) continue; // аніме/манга — TMDB-сигнал недоступний
            var payload = await cache.GetAsync(
                TmdbCacheKeys.Source, TmdbCacheKeys.Details(s.Type, seedTmdb, "en-US"), ct);
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

    private async Task<List<Title>> LoadCandidatesAsync(SeedAggregate seed, SimilarFilters? filters, CancellationToken ct)
    {
        var seedLocalIds = await db.Titles.AsNoTracking()
            .Where(t => t.TmdbId != null && seed.SeedTmdbIds.Contains(t.TmdbId.Value))
            .Select(t => t.Id)
            .ToListAsync(ct);

        var genres = seed.Genres.Select(g => g.Value.Trim()).ToArray();
        var keywords = seed.Keywords.Select(k => k.Value.Trim()).ToArray();
        var actors = seed.Actors.Select(a => a.Value.Trim()).ToArray();
        var anyMeta = genres.Length > 0 || keywords.Length > 0 || actors.Length > 0;

        var pool = db.Titles.AsNoTracking();
        if (seedLocalIds.Count > 0)
            pool = pool.Where(t => !seedLocalIds.Contains(t.Id));
        // Npgsql-шлях: meta-пул через unnest/&& по масивах (GIN). InMemory-провайдер не транслює
        // Any-по-масивових колонках (усталена адаптація тестів, як у DbContext) — пул ширший,
        // зайвих кандидатів відсіює gate score>0 у скорингу; семантика результату та сама.
        if (anyMeta && !IsInMemoryProvider)
            pool = pool.Where(t =>
                t.Genres.Any(g => genres.Contains(g)) ||
                t.Keywords.Any(k => keywords.Contains(k)) ||
                t.Actors.Any(a => actors.Contains(a)));

        // FR-21: hard-фільтри type/рік — у SQL ДО ORDER BY/TAKE (індексний план, а не фільтрація
        // 2000 матеріалізованих кандидатів). Рік — діапазон на year (BETWEEN-семантика) → ix_titles_year.
        pool = ApplyCandidateFilters(pool, filters);

        return await pool
            .OrderByDescending(t => t.Popularity)
            .Take(CandidateCap)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Hard-фільтри пулу кандидатів (FR-21): types → type = ANY(...), рік → year &gt;= from AND year &lt;= to
    /// (BETWEEN-семантика: той самий btree-range-план по ix_titles_year). Жанри сюди НЕ входять — вони soft-boost.
    /// Public static — щоб тест SQL-трансляції (ToQueryString, без БД) перевіряв план.
    /// </summary>
    public static IQueryable<Title> ApplyCandidateFilters(IQueryable<Title> pool, SimilarFilters? filters)
    {
        if (filters is null || !filters.HasPoolFilters) return pool;
        if (filters.Types is { Count: > 0 } types)
            pool = pool.Where(t => types.Contains(t.Type));
        if (filters.YearFrom is int yearFrom)
            pool = pool.Where(t => t.Year >= yearFrom);
        if (filters.YearTo is int yearTo)
            pool = pool.Where(t => t.Year <= yearTo);
        return pool;
    }

    private async Task<List<Title>> LoadByTextCandidatesAsync(
        SeedAggregate seed, ExtractedSignals signals, bool hasVector, CancellationToken ct)
    {
        var pools = new List<List<Title>>();

        // мета-пул: жанри (overlap) або keywords ILIKE по токенах
        var genres = signals.Genres.Select(g => g.ToLowerInvariant()).ToArray();
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
        SimilarFilters? filters,
        CancellationToken ct)
    {
        var candidateIds = candidates.Select(t => t.Id).ToList();
        var vecs = candidateIds.Count == 0
            ? new Dictionary<int, Pgvector.Vector>()
            : await db.Embeddings.AsNoTracking()
                .Where(e => candidateIds.Contains(e.TitleId))
                .ToDictionaryAsync(e => e.TitleId, e => e.Vec, ct);

        // Soft-boost жанрів (FR-21): перетин з жанрами-акцентами додає бонус до score (НЕ відсікання).
        HashSet<string>? boostSet = null;
        if (filters?.Genres is { Count: > 0 } boostGenres)
            boostSet = new HashSet<string>(boostGenres, StringComparer.OrdinalIgnoreCase);

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

            var value = score.Value;
            if (boostSet is not null)
            {
                var matches = t.Genres.Count(g => boostSet.Contains(g));
                if (matches > 0)
                    value += SimilarFilters.GenreBoostPerGenre * Math.Min(matches, SimilarFilters.MaxBoostGenres);
            }
            scored.Add(new ScoredTitle(t, new SimilarityScore(value, score.Contributions), seedAgg.Year));
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
