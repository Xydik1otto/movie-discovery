using System.Numerics.Tensors;

namespace MovieDiscovery.Core.Similarity;

/// <summary>
/// Движок схожості (чистий C#, без I/O). Сигнали: semantic (cosine через TensorPrimitives/SIMD),
/// keywords (overlap-частка), genres (Жаккар), credits (актори + режисер ×2), era (exp-спад,
/// напівжиття 7 років), numeric (гаус рейтингу+тривалості), tmdbSimilarBoost.
/// Ваги нормалізуються на доступні сигнали; бонус same-type ×1.1 застосовується наприкінці.
/// </summary>
public sealed class SimilarityEngine
{
    private readonly SimilarityOptions _opt;

    public SimilarityEngine(SimilarityOptions? options = null)
    {
        _opt = options ?? new SimilarityOptions();
    }

    public SimilarityOptions Options => _opt;

    public SimilarityScore Score(SeedAggregate seed, TitleFeatures candidate)
    {
        var signals = new List<SignalContribution>();

        // --- semantic: cosine векторів (SIMD) ---
        if (seed.Embedding is { Length: > 0 } && candidate.Embedding is { Length: > 0 }
            && seed.Embedding.Length == candidate.Embedding.Length)
        {
            var cos = CosineSimilarity(seed.Embedding, candidate.Embedding);
            signals.Add(new SignalContribution("semantic", _opt.Semantic, cos, []));
        }

        // --- genres: зважений Жаккар ---
        if (seed.Genres.Count > 0 && candidate.Genres.Length > 0)
        {
            var v = WeightedJaccard(seed.Genres, candidate.Genres);
            var shared = SharedItems(seed.Genres, candidate.Genres);
            signals.Add(new SignalContribution("genres", _opt.Genres, v, shared, SupportOf(shared, seed.Genres)));
        }

        // --- keywords: overlap-частка ---
        if (seed.Keywords.Count > 0 && candidate.Keywords.Length > 0)
        {
            var v = OverlapCoefficient(seed.Keywords, candidate.Keywords);
            var shared = SharedItems(seed.Keywords, candidate.Keywords);
            signals.Add(new SignalContribution("keywords", _opt.Keywords, v, shared, SupportOf(shared, seed.Keywords)));
        }

        // --- credits: актори (overlap-частка) + режисер ×2, нормалізація на доступні частини ---
        var creditPartsAvailable = false;
        var actorOverlap = 0.0;
        var dirMatch = 0.0;
        var sharedActors = new List<string>();
        var sharedDirector = new List<string>();
        var actorsAvailable = false;
        var directorAvailable = false;
        if (seed.Actors.Count > 0 && candidate.Actors.Length > 0)
        {
            actorOverlap = OverlapCoefficient(seed.Actors, candidate.Actors);
            sharedActors = SharedItems(seed.Actors, candidate.Actors);
            actorsAvailable = true;
            creditPartsAvailable = true;
        }
        if (!string.IsNullOrWhiteSpace(candidate.Director) && seed.Directors.Count > 0)
        {
            var candDir = candidate.Director.Trim();
            var dirWeight = seed.Directors
                .Where(d => string.Equals(d.Value.Trim(), candDir, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Weight)
                .DefaultIfEmpty(0.0)
                .Max();
            if (dirWeight > 0)
            {
                dirMatch = dirWeight;
                sharedDirector.Add(candDir);
            }
            directorAvailable = true;
            creditPartsAvailable = true;
        }
        if (creditPartsAvailable)
        {
            // режисер рахується ×2; нормалізуємо на наявні частини:
            // лише актори → actorOverlap; лише режисер → dirMatch; обидва → (a + 2d)/3
            var creditNum = 0.0;
            var creditDen = 0.0;
            if (actorsAvailable) { creditNum += actorOverlap; creditDen += 1.0; }
            if (directorAvailable) { creditNum += 2.0 * dirMatch; creditDen += 2.0; }
            var v = creditNum / creditDen;
            var items = new List<string>();
            if (sharedDirector.Count > 0)
                items.Add("director:" + sharedDirector[0]);
            foreach (var a in sharedActors.Take(3))
                items.Add("actor:" + a);
            var support = Math.Max(
                dirMatch,
                sharedActors.Count > 0 ? SupportOf(sharedActors, seed.Actors) : 0.0);
            signals.Add(new SignalContribution("credits", _opt.Credits, v, items, support <= 0 ? 1.0 : support));
        }

        // --- era: exp-спад за |Δyear| з напівжиттям ---
        if (seed.Year.HasValue && candidate.Year.HasValue)
        {
            var delta = Math.Abs(seed.Year.Value - candidate.Year.Value);
            var v = Math.Exp(-Math.Log(2.0) * delta / Math.Max(0.5, _opt.EraHalfLifeYears));
            signals.Add(new SignalContribution("era", _opt.Era, v, [delta.ToString("0")]));
        }

        // --- numeric: гаус близькості рейтингу і тривалості ---
        var numericParts = new List<double>();
        if (seed.Rating.HasValue && candidate.Rating.HasValue)
            numericParts.Add(Gaussian(seed.Rating.Value - candidate.Rating.Value, _opt.RatingSigma));
        if (seed.RuntimeMin.HasValue && candidate.RuntimeMin.HasValue)
            numericParts.Add(Gaussian(seed.RuntimeMin.Value - candidate.RuntimeMin.Value, _opt.RuntimeSigmaMinutes));
        if (numericParts.Count > 0)
            signals.Add(new SignalContribution("numeric", _opt.Numeric, numericParts.Average(), []));

        // --- tmdbSimilarBoost: кандидат є у TMDB similar/recommended еталонів ---
        if (seed.TmdbSimilarWeights.Count > 0
            && seed.TmdbSimilarWeights.TryGetValue(candidate.TmdbId, out var w))
            signals.Add(new SignalContribution("tmdb", _opt.TmdbSimilarBoost, Math.Clamp(w, 0.0, 1.0), []));

        // --- нормалізація ваг на доступні сигнали ---
        var weightSum = signals.Sum(s => s.Weight);
        var raw = weightSum <= 0 ? 0.0 : signals.Sum(s => s.Weight * s.Value) / weightSum;
        raw = Math.Clamp(raw, 0.0, 1.0);

        // --- бонус same-type ×1.1 (ранжувальний множник — свідомо НЕ клампиться до 1.0) ---
        if (!string.IsNullOrEmpty(seed.Type)
            && string.Equals(seed.Type, candidate.Type, StringComparison.OrdinalIgnoreCase))
            raw *= _opt.SameTypeBonus;

        return new SimilarityScore(raw, signals);
    }

    /// <summary>Косинусна схожість через System.Numerics.Tensors (SIMD). Нуль-вектор → 0.</summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        if (IsZero(a) || IsZero(b)) return 0;
        var cos = TensorPrimitives.CosineSimilarity(a.AsSpan(), b.AsSpan());
        if (double.IsNaN(cos) || double.IsInfinity(cos)) return 0;
        return Math.Clamp((double)cos, 0.0, 1.0);
    }

    /// <summary>Гаусова близькість різниці значень.</summary>
    public static double Gaussian(double delta, double sigma)
    {
        if (sigma <= 0) return delta == 0 ? 1.0 : 0.0;
        return Math.Exp(-(delta * delta) / (2.0 * sigma * sigma));
    }

    /// <summary>Зважений Жаккар: Σ min(w, inC) / Σ max(w, inC). Для одного seed-а — класичний Жаккар.</summary>
    public static double WeightedJaccard(IReadOnlyList<WeightedFeature> seed, string[] candidate)
    {
        if (seed.Count == 0 || candidate.Length == 0) return 0;
        var candSet = new HashSet<string>(candidate, StringComparer.OrdinalIgnoreCase);
        var seedSet = new HashSet<string>(seed.Select(f => f.Value), StringComparer.OrdinalIgnoreCase);
        double inter = 0, union = 0;
        foreach (var f in seed)
        {
            var inC = candSet.Contains(f.Value) ? 1.0 : 0.0;
            inter += Math.Min(f.Weight, inC);
            union += Math.Max(f.Weight, inC);
        }
        foreach (var c in candSet)
            if (!seedSet.Contains(c)) union += 1.0;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>Overlap-частка: Σ w(f) по спільних / min(Σ w, |candidate|). Для одного seed-а — |A∩B| / min(|A|,|B|).</summary>
    public static double OverlapCoefficient(IReadOnlyList<WeightedFeature> seed, string[] candidate)
    {
        if (seed.Count == 0 || candidate.Length == 0) return 0;
        var candSet = new HashSet<string>(candidate, StringComparer.OrdinalIgnoreCase);
        double inter = 0, seedTotal = 0;
        foreach (var f in seed)
        {
            seedTotal += f.Weight;
            if (candSet.Contains(f.Value)) inter += f.Weight;
        }
        var denom = Math.Min(seedTotal, candSet.Count);
        return denom <= 0 ? 0 : inter / denom;
    }

    /// <summary>Спільні значення seed-а і кандидата (для причин), максимум maxItems.</summary>
    public static List<string> SharedItems(IReadOnlyList<WeightedFeature> seed, string[] candidate, int maxItems = 3)
    {
        var candSet = new HashSet<string>(candidate, StringComparer.OrdinalIgnoreCase);
        return seed.Where(f => candSet.Contains(f.Value))
            .OrderByDescending(f => f.Weight)
            .Select(f => f.Value)
            .Take(maxItems)
            .ToList();
    }

    /// <summary>Частка еталонів, що підтримують спільні значення (максимальна вага серед спільних фіч).</summary>
    private static double SupportOf(IReadOnlyList<string> shared, IReadOnlyList<WeightedFeature> seedFeatures)
    {
        if (shared.Count == 0) return 1.0;
        var set = new HashSet<string>(shared, StringComparer.OrdinalIgnoreCase);
        var max = seedFeatures.Where(f => set.Contains(f.Value))
            .Select(f => f.Weight)
            .DefaultIfEmpty(1.0)
            .Max();
        return Math.Clamp(max, 0.0, 1.0);
    }

    private static bool IsZero(float[] v)
    {
        for (var i = 0; i < v.Length; i++)
            if (v[i] != 0f) return false;
        return true;
    }
}
