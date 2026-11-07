using MovieDiscovery.Core.Similarity;

namespace MovieDiscovery.Tests;

/// <summary>Тести движка схожості: Жаккар, era-спад, нормалізація ваг, same-type бонус, credits, multi-seed.</summary>
public class SimilarityEngineTests
{
    private static SeedAggregate Seed(
        string[]? genres = null, string[]? keywords = null, string[]? actors = null,
        string? director = null, int? year = null, double? rating = null, int? runtime = null,
        float[]? embedding = null, string type = "movie", string[]? directors = null)
    {
        var directorList = directors is not null
            ? directors.Select(d => new WeightedFeature(d, 1.0)).ToList()
            : (director is null ? [] : new List<WeightedFeature> { new(director, 1.0) });

        return new SeedAggregate
        {
            SeedTmdbIds = [1],
            Type = type,
            Genres = SeedAggregate.UnionFeatures([genres ?? []], 1),
            Keywords = SeedAggregate.UnionFeatures([keywords ?? []], 1),
            Actors = SeedAggregate.UnionFeatures([actors ?? []], 1),
            Directors = directorList,
            Year = year,
            Rating = rating,
            RuntimeMin = runtime,
            Embedding = embedding,
        };
    }

    private static TitleFeatures Candidate(
        string[]? genres = null, string[]? keywords = null, string[]? actors = null,
        string? director = null, int? year = null, double? rating = null, int? runtime = null,
        float[]? embedding = null, string type = "movie", int tmdbId = 2) => new()
    {
        TmdbId = tmdbId,
        Type = type,
        Genres = genres ?? [],
        Keywords = keywords ?? [],
        Actors = actors ?? [],
        Director = director,
        Year = year,
        Rating = rating,
        RuntimeMin = runtime,
        Embedding = embedding,
    };

    [Fact]
    public void Jaccard_Basic()
    {
        var s = Seed(genres: ["Drama", "Thriller"]);
        var c = Candidate(genres: ["Drama", "Thriller", "Comedy"]);
        var score = new SimilarityEngine().Score(s, c);

        var genresSignal = score.Contributions.First(x => x.Signal == "genres");
        Assert.Equal(2.0 / 3.0, genresSignal.Value, 6);
    }

    [Fact]
    public void Jaccard_Disjoint_IsZero()
    {
        var s = Seed(genres: ["Drama"]);
        var c = Candidate(genres: ["Horror"]);
        var score = new SimilarityEngine().Score(s, c);
        Assert.Equal(0, score.Contributions.First(x => x.Signal == "genres").Value, 6);
    }

    [Fact]
    public void Era_HalfLife7Years()
    {
        // |Δyear| = 7 → рівно половина; 14 → чверть
        var s = Seed(year: 2000);
        var e = new SimilarityEngine();

        Assert.Equal(1.0, e.Score(s, Candidate(year: 2000)).Contributions.First(x => x.Signal == "era").Value, 6);
        Assert.Equal(0.5, e.Score(s, Candidate(year: 2007)).Contributions.First(x => x.Signal == "era").Value, 6);
        Assert.Equal(0.25, e.Score(s, Candidate(year: 2014)).Contributions.First(x => x.Signal == "era").Value, 6);
    }

    [Fact]
    public void NoSemantic_Weights_Renormalized_OnRest()
    {
        // Жодного вектора: ваги нормалізуються на решту доступних сигналів.
        // Доступні: genres (0.15, v=0.5) + era (0.05, v=1.0) → score = (0.15*0.5 + 0.05*1)/(0.20)
        var s = Seed(genres: ["Drama", "Thriller"], year: 2000);
        var c = Candidate(genres: ["Drama", "Thriller", "Comedy"], year: 2000);
        var score = new SimilarityEngine().Score(s, c);

        var expected = ((0.15 * (2.0 / 3.0) + 0.05 * 1.0) / (0.15 + 0.05)) * 1.1; // same-type movie↔movie
        Assert.Equal(expected, score.Value, 6);
        Assert.DoesNotContain(score.Contributions, x => x.Signal == "semantic");
    }

    [Fact]
    public void Semantic_UsesAllWeights()
    {
        // Вектор [1,0] у обох → cosine 1.0; сигнал semantic присутній. Типи різні → без same-type бонуса.
        var s = Seed(genres: ["Drama", "Thriller"], year: 2000, embedding: [1f, 0f], type: "series");
        var c = Candidate(genres: ["Drama", "Thriller", "Comedy"], year: 2000, embedding: [1f, 0f], type: "movie");
        var score = new SimilarityEngine().Score(s, c);

        var semantic = score.Contributions.First(x => x.Signal == "semantic");
        Assert.Equal(1.0, semantic.Value, 6);

        // (0.35*1 + 0.15*(2/3) + 0.05*1) / (0.35+0.15+0.05)
        var expected = (0.35 * 1.0 + 0.15 * (2.0 / 3.0) + 0.05 * 1.0) / 0.55;
        Assert.Equal(expected, score.Value, 6);
    }

    [Fact]
    public void SameType_Bonus_X1_1()
    {
        var s = Seed(genres: ["Drama"], type: "movie");
        var same = new SimilarityEngine().Score(s, Candidate(genres: ["Drama"], type: "movie"));
        var other = new SimilarityEngine().Score(s, Candidate(genres: ["Drama"], type: "series"));

        var baseValue = (0.15 * 1.0) / 0.15; // єдиний доступний сигнал
        Assert.Equal(baseValue * 1.1, same.Value, 6);
        Assert.Equal(baseValue, other.Value, 6);
    }

    [Fact]
    public void Credits_DirectorCountsDouble()
    {
        var s = Seed(directors: ["Jane Director"]);
        var c = Candidate(director: "Jane Director");
        var score = new SimilarityEngine().Score(s, c);

        var credits = score.Contributions.First(x => x.Signal == "credits");
        Assert.Equal(1.0, credits.Value, 6); // лише режисер: 2*1/2 = 1
    }

    [Fact]
    public void Credits_ActorOverlap_Coefficient()
    {
        // overlap-частка: |A∩B| / min(|A|,|B|) = 1/2; режисера нема → нормалізація лише на акторів
        var s = Seed(actors: ["A", "B"]);
        var c = Candidate(actors: ["A", "C"]);
        var score = new SimilarityEngine().Score(s, c);

        var credits = score.Contributions.First(x => x.Signal == "credits");
        Assert.Equal(0.5, credits.Value, 6);
    }

    [Fact]
    public void Credits_Director_And_Actors_Combined()
    {
        var s = Seed(actors: ["A", "B"], directors: ["Jane Director"]);
        var c = Candidate(actors: ["A", "C"], director: "Jane Director");
        var score = new SimilarityEngine().Score(s, c);

        var credits = score.Contributions.First(x => x.Signal == "credits");
        Assert.Equal((0.5 + 2.0) / 3.0, credits.Value, 6); // (a + 2d)/3
    }

    [Fact]
    public void OverlapCoefficient_Keywords()
    {
        var s = Seed(keywords: ["dark comedy", "heist"]);
        var c = Candidate(keywords: ["dark comedy", "heist", "prison"]);
        var score = new SimilarityEngine().Score(s, c);
        Assert.Equal(1.0, score.Contributions.First(x => x.Signal == "keywords").Value, 6);
    }

    [Fact]
    public void Cosine_Simd_Behaviour()
    {
        Assert.Equal(1.0, SimilarityEngine.CosineSimilarity([1f, 2f, 3f], [2f, 4f, 6f]), 6);
        Assert.Equal(0.0, SimilarityEngine.CosineSimilarity([1f, 0f], [0f, 1f]), 6);
        Assert.Equal(0.0, SimilarityEngine.CosineSimilarity([1f, 0f], [-1f, 0f])); // негатив → clamp 0
        Assert.Equal(0.0, SimilarityEngine.CosineSimilarity([0f, 0f], [1f, 0f])); // нуль-вектор → 0
    }

    [Fact]
    public void TmdbSimilar_Boost_Applies()
    {
        var weights = new Dictionary<int, double> { [2] = 1.0 };
        var seedWithBoost = new SeedAggregate
        {
            SeedTmdbIds = [1],
            Type = "movie",
            Genres = [],
            Keywords = [],
            Actors = [],
            Directors = [],
            Year = 2020,
            TmdbSimilarWeights = weights,
        };

        var boosted = new SimilarityEngine().Score(seedWithBoost, Candidate(year: 2020));
        var boost = boosted.Contributions.FirstOrDefault(x => x.Signal == "tmdb");
        Assert.NotNull(boost);
        Assert.Equal(1.0, boost.Value, 6);
    }

    [Fact]
    public void MultiSeed_GenreWeight_K_of_N()
    {
        // 3 seed-и: Action у 2 з 3 → вага 2/3
        var seeds = new List<TitleFeatures>
        {
            new() { TmdbId = 1, Type = "movie", Genres = ["Action", "Drama"] },
            new() { TmdbId = 2, Type = "movie", Genres = ["Action"] },
            new() { TmdbId = 3, Type = "movie", Genres = ["Comedy"] },
        };
        var agg = SeedAggregate.FromSeeds(seeds);

        var action = agg.Genres.First(g => g.Value.Equals("Action", StringComparison.OrdinalIgnoreCase));
        var drama = agg.Genres.First(g => g.Value.Equals("Drama", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2.0 / 3.0, action.Weight, 6);
        Assert.Equal(1.0 / 3.0, drama.Weight, 6);
        Assert.Equal(3, agg.SeedCount);
        Assert.Equal("movie", agg.Type);
    }

    [Fact]
    public void MultiSeed_VectorAverage()
    {
        var seeds = new List<TitleFeatures>
        {
            new() { TmdbId = 1, Type = "movie", Embedding = [1f, 0f] },
            new() { TmdbId = 2, Type = "movie", Embedding = [0f, 1f] },
            new() { TmdbId = 3, Type = "movie" }, // без вектора — не бере участі
        };
        var agg = SeedAggregate.FromSeeds(seeds);

        Assert.NotNull(agg.Embedding);
        Assert.Equal(0.5f, agg.Embedding[0], 5);
        Assert.Equal(0.5f, agg.Embedding[1], 5);
    }

    [Fact]
    public void MultiSeed_AggregatedYear_Rating()
    {
        var seeds = new List<TitleFeatures>
        {
            new() { TmdbId = 1, Type = "movie", Year = 2000, Rating = 7.0, RuntimeMin = 100 },
            new() { TmdbId = 2, Type = "movie", Year = 2010, Rating = 9.0, RuntimeMin = 120 },
        };
        var agg = SeedAggregate.FromSeeds(seeds);

        Assert.Equal(2005, agg.Year);
        Assert.Equal(8.0, agg.Rating ?? 0, 6);
        Assert.Equal(110, agg.RuntimeMin);
    }

    [Fact]
    public void Reasons_Top3_Localized_UkAndEn()
    {
        var s = Seed(genres: ["Drama", "Thriller"], actors: ["Іван"], director: "Jane Director", year: 2000);
        var c = Candidate(genres: ["Drama", "Thriller"], actors: ["Іван"], director: "Jane Director", year: 2001);
        var score = new SimilarityEngine().Score(s, c);

        var uk = SimilarityTexts.BuildReasons(score.Contributions, 1, "uk");
        var en = SimilarityTexts.BuildReasons(score.Contributions, 1, "en");

        Assert.Equal(3, uk.Count);
        Assert.Contains(uk, r => r.StartsWith("Спільний режисер: Jane Director"));
        Assert.Contains(uk, r => r.StartsWith("Спільні жанри: Drama, Thriller"));
        Assert.Contains(uk, r => r.StartsWith("Спільні актори: Іван"));
        Assert.Contains(en, r => r.StartsWith("Shared director: Jane Director"));
        Assert.Contains(en, r => r.StartsWith("Shared genres: Drama, Thriller"));
        Assert.Equal(3, en.Count);
    }

    [Fact]
    public void Reasons_MultiSeed_Suffix()
    {
        var contrib = new List<SignalContribution>
        {
            new("genres", 0.15, 0.5, ["Action"], 2.0 / 3.0),
        };
        var uk = SimilarityTexts.BuildReasons(contrib, 3, "uk");
        var en = SimilarityTexts.BuildReasons(contrib, 3, "en");

        Assert.Equal("Спільні жанри: Action (у 2 з 3 еталонів)", uk[0]);
        Assert.Equal("Shared genres: Action (in 2 of 3 seeds)", en[0]);
    }
}
