using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Discover;
using MovieDiscovery.Core.Taste;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>FR-18: чистий скоринг DiscoverScorer (жанри Jaccard×вага, ера, актори, rating_norm, негативи).</summary>
public class DiscoverScorerTests
{
    private static DiscoverCandidate Cand(
        int id, string[] genres, int? year = null, string[]? actors = null,
        double? imdb = null, double popularity = 0) =>
        new(id, null, "movie", null, $"T{id}", year, genres, actors ?? [], null, imdb, null, popularity, "/p.jpg", null, null);

    [Fact]
    public void Genre_Overlap_Uses_Jaccard_Times_Profile_Weight()
    {
        var scorer = new DiscoverScorer(new DiscoverOptions());
        var profile = new TasteProfileData { Genres = new() { ["drama"] = 1.0, ["thriller"] = 2.0 } };

        // candidate genres [drama, thriller]: shared 2, union 2 → Jaccard 1 → 1.0×(1+2)=3.0;
        // нейтральний рейтинг imdb=0 → rating_norm 0; popularity 0 → 0
        var score = scorer.Score(Cand(1, ["drama", "thriller"], imdb: 0), profile);

        Assert.Equal(3.0, score, precision: 6);
    }

    [Fact]
    public void Partial_Overlap_Jaccard_Scales_Down()
    {
        var scorer = new DiscoverScorer(new DiscoverOptions());
        var profile = new TasteProfileData { Genres = new() { ["drama"] = 1.0 } };

        // shared 1, union 2 ([drama, action] ∪ {drama}) → 1/2 × 1.0 = 0.5; imdb=0 → rating 0
        var score = scorer.Score(Cand(1, ["drama", "action"], imdb: 0), profile);

        Assert.Equal(0.5, score, precision: 6);
    }

    [Fact]
    public void Era_Decade_And_Actors_Overlap_Add()
    {
        var scorer = new DiscoverScorer(new DiscoverOptions());
        var profile = new TasteProfileData
        {
            Era = new() { ["1990s"] = 1.5 },
            Actors = new() { ["Nolan"] = 0.5, ["Bale"] = 0.7 },
        };

        var score = scorer.Score(Cand(1, [], year: 1994, actors: ["Nolan", "Smith"], imdb: 7), profile);

        // era 1.5 + actors 0.5 + rating 7/10=0.7
        Assert.Equal(1.5 + 0.5 + 0.7, score, precision: 6);
    }

    [Fact]
    public void Negative_Genres_Penalize_Score()
    {
        var scorer = new DiscoverScorer(new DiscoverOptions());
        var profile = new TasteProfileData
        {
            Genres = new() { ["drama"] = 1.0 },
            NegativeGenres = new() { ["horror"] = 0.8 },
        };

        var withHorror = scorer.Score(Cand(1, ["drama", "horror"], imdb: 5), profile);
        var withoutHorror = scorer.Score(Cand(2, ["drama"], imdb: 5), profile);

        Assert.True(withHorror < withoutHorror);
        // без жахів: жанри Jaccard 1 ×1.0 + rating 0.5 = 1.5
        Assert.Equal(1.5, withoutHorror, precision: 6);
        // з жахами: жанри Jaccard(drama)=1/2 ×1.0 + penalty Jaccard(horror)=1/2 ×0.8 + rating 0.5 = 0.6
        Assert.Equal(0.6, withHorror, precision: 6);
    }

    [Fact]
    public void Empty_Profile_Still_Rates_By_Rating_And_Popularity()
    {
        var scorer = new DiscoverScorer(new DiscoverOptions());

        var high = scorer.Score(Cand(1, [], imdb: 9, popularity: 100), new TasteProfileData());
        var low = scorer.Score(Cand(2, [], imdb: 3, popularity: 1), new TasteProfileData());

        Assert.True(high > low);
    }

    [Fact]
    public void DecadeOf_Maps_Year_To_Decade_Key()
    {
        Assert.Equal("1990s", DiscoverScorer.DecadeOf(1994));
        Assert.Equal("2020s", DiscoverScorer.DecadeOf(2026));
        Assert.Null(DiscoverScorer.DecadeOf(null));
    }
}

/// <summary>FR-18: DiscoverPlanner — мікс 70/20/10, diversity ≤2/жанр, дедуплікація.</summary>
public class DiscoverPlannerTests
{
    private static (DiscoverCandidate, double) Item(int id, params string[] genres) =>
        (new DiscoverCandidate(id, null, "movie", null, $"T{id}", 2020, genres, [], null, 8, null, 10, "/p.jpg", null, null), 1.0 * id);

    [Fact]
    public void Mix_Is_70_20_10_For_Count_10()
    {
        var exploit = Enumerable.Range(1, 10).Select(i => Item(i, $"g{i}")).ToList();
        var safe = Enumerable.Range(11, 10).Select(i => Item(i, $"g{i}")).ToList();
        var explore = Enumerable.Range(21, 10).Select(i => Item(i, $"g{i}")).ToList();

        var picks = DiscoverPlanner.Build(exploit, safe, explore, 10, new DiscoverOptions());

        Assert.Equal(10, picks.Count);
        Assert.Equal(7, picks.Count(p => p.Pool == "exploit"));
        Assert.Equal(2, picks.Count(p => p.Pool == "safe"));
        Assert.Equal(1, picks.Count(p => p.Pool == "explore"));
        Assert.Equal(10, picks.Select(p => p.Item.Id).Distinct().Count()); // дедуплікація
    }

    [Fact]
    public void Diversity_No_More_Than_2_Per_Genre()
    {
        // 10 exploit-кандидатів — усі з жанром "action" (максимум 2 у видачі)
        var exploit = Enumerable.Range(1, 10).Select(i => Item(i, "action")).ToList();
        var safe = Enumerable.Range(11, 10).Select(i => Item(i, $"s{i}")).ToList();
        var explore = Enumerable.Range(21, 10).Select(i => Item(i, $"e{i}")).ToList();

        var picks = DiscoverPlanner.Build(exploit, safe, explore, 10, new DiscoverOptions());

        // exploit дає лише 2 "action"; добір з exploit/safe/explore (усі різні жанри) заповнює решту
        Assert.Equal(10, picks.Count);
        Assert.Equal(2, picks.Count(p => p.Item.Genres.Contains("action")));
    }

    [Fact]
    public void Fill_Takes_From_Other_Pools_When_Pool_Small()
    {
        var exploit = new List<(DiscoverCandidate, double)> { Item(1, "a"), Item(2, "b") };
        var safe = new List<(DiscoverCandidate, double)> { Item(3, "c") };
        var explore = new List<(DiscoverCandidate, double)> { Item(4, "d") };

        var picks = DiscoverPlanner.Build(exploit, safe, explore, 10, new DiscoverOptions());

        Assert.Equal(4, picks.Count); // усі наявні кандидати, без дублів
        Assert.Equal(2, picks.Count(p => p.Pool == "exploit"));
    }

    [Fact]
    public void Same_Candidate_In_Two_Pools_Appears_Once()
    {
        var cand = Item(1, "action");
        var picks = DiscoverPlanner.Build([cand], [cand], [], 5, new DiscoverOptions());

        Assert.Single(picks);
        Assert.Equal(1, picks[0].Item.Id);
    }
}
