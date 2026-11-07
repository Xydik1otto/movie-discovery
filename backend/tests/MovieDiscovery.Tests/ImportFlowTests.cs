using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Tests;

/// <summary>Статуси трекерів → kind списків, оцінки → rating, валідація read, форма джерел імпорту.</summary>
public class ImportFlowTests
{
    [Theory]
    [InlineData("completed", false, "watched")]   // аніме: completed → Переглянуте
    [InlineData("completed", true, "read")]       // манга: completed → Прочитане
    [InlineData("watching", false, "watchlist")]  // watching → Хочу подивитись
    [InlineData("reading", true, "watchlist")]    // reading → Хочу подивитись
    [InlineData("current", false, "watchlist")]   // AniList CURRENT
    [InlineData("repeating", false, "watchlist")] // AniList REPEATING
    [InlineData("rewatching", true, "watchlist")] // Shikimori rewatching
    [InlineData("  COMPLETED  ", true, "read")]   // регістр/пробіли не важливі
    public void KindForStatus_Maps_Watch_Statuses(string status, bool isManga, string expected)
    {
        Assert.Equal(expected, ListKinds.KindForStatus(status, isManga));
    }

    [Theory]
    [InlineData("on_hold")]
    [InlineData("dropped")]
    [InlineData("planned")]
    [InlineData("plan_to_watch")]
    [InlineData("plan_to_read")]
    [InlineData("paused")]
    [InlineData("planning")]
    [InlineData(null)]
    [InlineData("")]
    public void KindForStatus_Skips_Hold_Drop_Plan(string? status)
    {
        Assert.Null(ListKinds.KindForStatus(status, isManga: false));
        Assert.Null(ListKinds.KindForStatus(status, isManga: true));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, null)]   // не оцінено → null
    [InlineData(-3.0, null)]
    [InlineData(7.0, 7)]
    [InlineData(10.0, 10)]
    [InlineData(0.5, 1)]      // кламп до 1
    [InlineData(100.0, 10)]   // AniList POINT_100 → /10
    [InlineData(85.0, 9)]     // 8.5 → away-from-zero → 9
    [InlineData(11.0, 1)]
    [InlineData(55.0, 6)]
    public void RatingFromScore_Clamps_And_Normalizes(double? score, int? expected)
    {
        Assert.Equal(expected, ListKinds.RatingFromScore(score));
    }

    [Fact]
    public void Read_On_Movie_Is_Rejected_400_Semantics()
    {
        // контролер мапить false → 400 invalid_request
        Assert.False(ListKinds.AllowedForType("read", "movie"));
        Assert.False(ListKinds.AllowedForType("read", "series"));
        Assert.False(ListKinds.AllowedForType("read", "show"));
        Assert.False(ListKinds.AllowedForType("read", "anime")); // read лише для манги
        Assert.True(ListKinds.AllowedForType("read", "manga"));
        // решта kind — для всіх типів
        Assert.True(ListKinds.AllowedForType("watchlist", "manga"));
        Assert.True(ListKinds.AllowedForType("watched", "anime"));
    }

    [Fact]
    public void Read_Kind_Is_Valid()
    {
        Assert.True(ListKinds.IsValid("watchlist"));
        Assert.True(ListKinds.IsValid("watched"));
        Assert.True(ListKinds.IsValid("read"));
        Assert.False(ListKinds.IsValid("favorites"));
        Assert.False(ListKinds.IsValid(null));
    }

    [Fact]
    public void Sources_Catalog_Has_Three_Trackers()
    {
        Assert.Equal(3, ImportSourceCatalog.All.Length);

        var mal = Assert.Single(ImportSourceCatalog.All, s => s.Id == "mal");
        Assert.Equal("MyAnimeList", mal.Name);
        Assert.Equal("нікнейм MAL", mal.Hint);
        Assert.True(mal.Authless);

        var anilist = Assert.Single(ImportSourceCatalog.All, s => s.Id == "anilist");
        Assert.Equal("AniList", anilist.Name);
        Assert.True(anilist.Authless);

        var shikimori = Assert.Single(ImportSourceCatalog.All, s => s.Id == "shikimori");
        Assert.Equal("Shikimori", shikimori.Name);
        Assert.Equal("нікнейм або ID", shikimori.Hint);

        Assert.True(ImportSourceCatalog.IsKnown("mal"));
        Assert.False(ImportSourceCatalog.IsKnown("kitsu"));
        Assert.False(ImportSourceCatalog.IsKnown(null));
    }

    [Fact]
    public void Stats_Serializes_To_Contract_Shape()
    {
        var stats = new ImportStats
        {
            AnimeTotal = 10, MangaTotal = 4, Matched = 6, Created = 3,
            AnimeWatched = 5, MangaRead = 2, WatchlistAdded = 3, Skipped = 2,
            Errors = ["e1"],
        };
        using var doc = stats.ToJsonDocument();
        var root = doc.RootElement;

        Assert.Equal(10, root.GetProperty("animeTotal").GetInt32());
        Assert.Equal(4, root.GetProperty("mangaTotal").GetInt32());
        Assert.Equal(6, root.GetProperty("matched").GetInt32());
        Assert.Equal(3, root.GetProperty("created").GetInt32());
        Assert.Equal(5, root.GetProperty("animeWatched").GetInt32());
        Assert.Equal(2, root.GetProperty("mangaRead").GetInt32());
        Assert.Equal(3, root.GetProperty("watchlistAdded").GetInt32());
        Assert.Equal(2, root.GetProperty("skipped").GetInt32());
        Assert.Equal("e1", root.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public void Stats_Errors_Capped_At_20()
    {
        var stats = new ImportStats();
        for (var i = 0; i < 25; i++) stats.Errors.Add($"e{i}");
        using var doc = stats.ToJsonDocument();
        Assert.Equal(20, doc.RootElement.GetProperty("errors").GetArrayLength());
    }
}
