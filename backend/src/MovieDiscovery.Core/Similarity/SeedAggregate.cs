namespace MovieDiscovery.Core.Similarity;

/// <summary>Ознака з вагою для multi-seed агрегації (ознака зустрічається у k з n еталонів → вага k/n).</summary>
public sealed record WeightedFeature(string Value, double Weight);

/// <summary>Плоскі ознаки тайтла для движка схожості (чисті дані, без I/O).</summary>
public sealed record TitleFeatures
{
    public required int? TmdbId { get; init; } // null для аніме/манги з зовнішніх джерел
    public required string Type { get; init; } // movie | series | show | anime | manga
    public string[] Genres { get; init; } = [];
    public string[] Keywords { get; init; } = [];
    public string[] Actors { get; init; } = [];
    public string? Director { get; init; }
    public int? Year { get; init; }
    public double? Rating { get; init; }
    public int? RuntimeMin { get; init; }
    public float[]? Embedding { get; init; }
    /// <summary>TMDB similar/recommended id цього тайтла (джерело сигналу tmdbSimilarBoost).</summary>
    public IReadOnlySet<int>? TmdbSimilarIds { get; init; }
}

/// <summary>
/// Агрегований профіль еталонів (1..10 seed-тайтлів): середнє векторів,
/// об'єднані жанри/keywords/актори/режисери з вагою k/n, усереднені числові поля.
/// </summary>
public sealed class SeedAggregate
{
    public required IReadOnlyList<int> SeedTmdbIds { get; init; }
    public int SeedCount => SeedTmdbIds.Count;
    public string? Type { get; init; }
    public required IReadOnlyList<WeightedFeature> Genres { get; init; }
    public required IReadOnlyList<WeightedFeature> Keywords { get; init; }
    public required IReadOnlyList<WeightedFeature> Actors { get; init; }
    public required IReadOnlyList<WeightedFeature> Directors { get; init; }
    public int? Year { get; init; }
    public double? Rating { get; init; }
    public int? RuntimeMin { get; init; }
    public float[]? Embedding { get; init; }

    /// <summary>TMDB similar/recommended id → частка еталонів (з даними), що його згадують.</summary>
    public IReadOnlyDictionary<int, double> TmdbSimilarWeights { get; init; } =
        new Dictionary<int, double>();

    public static readonly SeedAggregate Empty = new()
    {
        SeedTmdbIds = [],
        Genres = [],
        Keywords = [],
        Actors = [],
        Directors = [],
    };

    /// <summary>Агрегувати 1..n еталонів. Ембедінги без векторів просто не беруть участі в середньому.</summary>
    public static SeedAggregate FromSeeds(IReadOnlyList<TitleFeatures> seeds)
    {
        if (seeds.Count == 0) return Empty;
        var n = seeds.Count;

        string? type = null;
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in seeds)
        {
            if (string.IsNullOrWhiteSpace(s.Type)) continue;
            typeCounts[s.Type] = typeCounts.GetValueOrDefault(s.Type) + 1;
        }
        if (typeCounts.Count > 0)
            type = typeCounts.OrderByDescending(kv => kv.Value).First().Key.ToLowerInvariant();

        var genres = UnionFeatures(seeds.Select(s => s.Genres), n);
        var keywords = UnionFeatures(seeds.Select(s => s.Keywords), n);
        var actors = UnionFeatures(seeds.Select(s => s.Actors), n);

        var directors = new Dictionary<string, (string Display, double Weight)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in seeds)
        {
            if (string.IsNullOrWhiteSpace(s.Director)) continue;
            var key = s.Director.Trim();
            var (display, weight) = directors.TryGetValue(key, out var cur) ? (cur.Display, cur.Weight) : (key, 0.0);
            directors[key] = (display, weight + 1.0 / n);
        }

        var years = seeds.Select(s => s.Year).Where(y => y.HasValue).Select(y => y!.Value).ToList();
        var ratings = seeds.Select(s => s.Rating).Where(r => r.HasValue).Select(r => r!.Value).ToList();
        var runtimes = seeds.Select(s => s.RuntimeMin).Where(r => r.HasValue).Select(r => r!.Value).ToList();

        float[]? embedding = AverageVectors(seeds.Select(s => s.Embedding).Where(v => v is { Length: > 0 }).Select(v => v!));

        var tmdbSimilar = new Dictionary<int, double>();
        var seedsWithData = 0;
        foreach (var s in seeds)
        {
            if (s.TmdbSimilarIds is not { Count: > 0 }) continue;
            seedsWithData++;
            foreach (var id in s.TmdbSimilarIds)
                tmdbSimilar[id] = tmdbSimilar.GetValueOrDefault(id) + 1.0;
        }
        if (seedsWithData > 0)
            foreach (var id in tmdbSimilar.Keys.ToList())
                tmdbSimilar[id] = tmdbSimilar[id] / seedsWithData;

        return new SeedAggregate
        {
            SeedTmdbIds = seeds.Select(s => s.TmdbId).Where(v => v.HasValue).Select(v => v!.Value).ToList(),
            Type = type,
            Genres = genres,
            Keywords = keywords,
            Actors = actors,
            Directors = directors.Values.Select(d => new WeightedFeature(d.Display, d.Weight)).ToList(),
            Year = years.Count > 0 ? (int)Math.Round(years.Average()) : null,
            Rating = ratings.Count > 0 ? ratings.Average() : null,
            RuntimeMin = runtimes.Count > 0 ? (int)Math.Round(runtimes.Average()) : null,
            Embedding = embedding,
            TmdbSimilarWeights = tmdbSimilar,
        };
    }

    /// <summary>Об'єднання масивів ознак: ознака зустрічається у k з n еталонів → вага k/n.</summary>
    public static IReadOnlyList<WeightedFeature> UnionFeatures(IEnumerable<string?[]> arrays, int n)
    {
        if (n <= 0) n = 1;
        var map = new Dictionary<string, (string Display, double Weight)>(StringComparer.OrdinalIgnoreCase);
        foreach (var arr in arrays)
        {
            if (arr is not { Length: > 0 }) continue;
            foreach (var raw in arr)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var key = raw.Trim();
                var (display, weight) = map.TryGetValue(key, out var cur) ? (cur.Display, cur.Weight) : (key, 0.0);
                map[key] = (display, weight + 1.0 / n);
            }
        }
        return map.Values.Select(v => new WeightedFeature(v.Display, v.Weight)).ToList();
    }

    /// <summary>Середнє векторів еталонів; null, якщо жодного вектора нема.</summary>
    public static float[]? AverageVectors(IEnumerable<float[]> vectors)
    {
        var list = vectors.Where(v => v is { Length: > 0 }).ToList();
        if (list.Count == 0) return null;
        var dim = list[0].Length;
        var acc = new float[dim];
        var used = 0;
        foreach (var v in list)
        {
            if (v.Length != dim) continue; // вектор іншої розмірності не змішуємо
            for (var i = 0; i < dim; i++) acc[i] += v[i];
            used++;
        }
        if (used == 0) return null;
        for (var i = 0; i < dim; i++) acc[i] /= used;
        return acc;
    }
}
