using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Tests;

/// <summary>Статуси трекерів → kind списків, оцінки → rating, валідація read, форма джерел імпорту.</summary>
public class ImportFlowTests
{
    [Theory]
    [InlineData("completed", false, "watched")]     // аніме: completed → Переглянуте
    [InlineData("completed", true, "read")]         // манга: completed → Прочитане
    [InlineData("watching", false, "watching")]     // watching → Дивиться зараз
    [InlineData("watching", true, "watching")]
    [InlineData("current", false, "watching")]      // AniList CURRENT
    [InlineData("repeating", false, "watching")]    // AniList REPEATING
    [InlineData("rewatching", true, "watching")]    // Shikimori rewatching
    [InlineData("reading", true, "reading")]        // манга: reading → Читає зараз
    [InlineData("reading", false, "watching")]      // для аніме reading не буває — фолбек watching
    [InlineData("planned", false, "watchlist")]     // «Хочу подивитись» — семантика збігається
    [InlineData("planned", true, "watchlist")]
    [InlineData("plan_to_watch", false, "watchlist")] // MAL
    [InlineData("plan_to_read", true, "watchlist")]   // MAL
    [InlineData("planning", false, "watchlist")]      // AniList PLANNING
    [InlineData("on_hold", false, "on_hold")]
    [InlineData("on_hold", true, "on_hold")]
    [InlineData("paused", false, "on_hold")]          // AniList PAUSED
    [InlineData("dropped", false, "dropped")]
    [InlineData("dropped", true, "dropped")]
    [InlineData("  COMPLETED  ", true, "read")]     // регістр/пробіли не важливі
    [InlineData(" On_Hold ", true, "on_hold")]
    public void KindForStatus_Maps_All_Tracker_Statuses(string status, bool isManga, string expected)
    {
        Assert.Equal(expected, ListKinds.KindForStatus(status, isManga));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown_status")]
    [InlineData("watchingg")]
    public void KindForStatus_Skips_Unknown_Statuses(string? status)
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
    public void Read_And_Reading_On_Non_Manga_Are_Rejected_400_Semantics()
    {
        // контролер мапить false → 400 invalid_request
        Assert.False(ListKinds.AllowedForType("read", "movie"));
        Assert.False(ListKinds.AllowedForType("read", "series"));
        Assert.False(ListKinds.AllowedForType("read", "show"));
        Assert.False(ListKinds.AllowedForType("read", "anime")); // read лише для манги
        Assert.True(ListKinds.AllowedForType("read", "manga"));
        // reading — те саме правило
        Assert.False(ListKinds.AllowedForType("reading", "movie"));
        Assert.False(ListKinds.AllowedForType("reading", "series"));
        Assert.False(ListKinds.AllowedForType("reading", "show"));
        Assert.False(ListKinds.AllowedForType("reading", "anime"));
        Assert.True(ListKinds.AllowedForType("reading", "manga"));
        // решта kind — для всіх типів
        Assert.True(ListKinds.AllowedForType("watchlist", "manga"));
        Assert.True(ListKinds.AllowedForType("watched", "anime"));
        Assert.True(ListKinds.AllowedForType("watching", "anime"));
        Assert.True(ListKinds.AllowedForType("on_hold", "manga"));
        Assert.True(ListKinds.AllowedForType("dropped", "anime"));
    }

    [Fact]
    public void IsValid_Accepts_All_Nine_Kinds()
    {
        Assert.True(ListKinds.IsValid("watchlist"));
        Assert.True(ListKinds.IsValid("watched"));
        Assert.True(ListKinds.IsValid("read"));
        Assert.True(ListKinds.IsValid("liked"));
        Assert.True(ListKinds.IsValid("disliked"));
        Assert.True(ListKinds.IsValid("watching"));
        Assert.True(ListKinds.IsValid("reading"));
        Assert.True(ListKinds.IsValid("on_hold"));
        Assert.True(ListKinds.IsValid("dropped"));
        Assert.False(ListKinds.IsValid("favorites"));
        Assert.False(ListKinds.IsValid(null));
        Assert.False(ListKinds.IsValid(""));
    }

    [Fact]
    public void DiscoverExcluded_Covers_All_Nine_Kinds()
    {
        // Нічого з любого списку не рекомендувати повторно
        Assert.Equal(9, ListKinds.DiscoverExcluded.Length);
        Assert.Contains(ListKinds.Watchlist, ListKinds.DiscoverExcluded);
        Assert.Contains(ListKinds.Watched, ListKinds.DiscoverExcluded);
        Assert.Contains(ListKinds.Read, ListKinds.DiscoverExcluded);
        Assert.Contains(ListKinds.Liked, ListKinds.DiscoverExcluded);
        Assert.Contains(ListKinds.Disliked, ListKinds.DiscoverExcluded);
        Assert.Contains(ListKinds.Watching, ListKinds.DiscoverExcluded);
        Assert.Contains(ListKinds.Reading, ListKinds.DiscoverExcluded);
        Assert.Contains(ListKinds.OnHold, ListKinds.DiscoverExcluded);
        Assert.Contains(ListKinds.Dropped, ListKinds.DiscoverExcluded);
    }

    [Fact]
    public void ActionForKind_Watching_Reading_Are_Interest_OnHold_Dropped_Are_Neutral()
    {
        // watching/reading — сигнал інтересу для смаку (як watchlist)
        Assert.Equal("watchlist", ListKinds.ActionForKind(ListKinds.Watching));
        Assert.Equal("watchlist", ListKinds.ActionForKind(ListKinds.Reading));
        // on_hold/dropped — на смак не впливають
        Assert.Null(ListKinds.ActionForKind(ListKinds.OnHold));
        Assert.Null(ListKinds.ActionForKind(ListKinds.Dropped));
        // базова поведінка не змінена
        Assert.Equal("like", ListKinds.ActionForKind(ListKinds.Liked));
        Assert.Equal("dislike", ListKinds.ActionForKind(ListKinds.Disliked));
        Assert.Equal("watchlist", ListKinds.ActionForKind(ListKinds.Watchlist));
        Assert.Equal("watched", ListKinds.ActionForKind(ListKinds.Watched));
        Assert.Equal("watched", ListKinds.ActionForKind(ListKinds.Read));
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
            AnimeWatched = 5, MangaRead = 2, WatchlistAdded = 3,
            WatchingAdded = 7, ReadingAdded = 1, OnHoldAdded = 2, DroppedAdded = 4, Skipped = 2,
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
        Assert.Equal(7, root.GetProperty("watchingAdded").GetInt32());
        Assert.Equal(1, root.GetProperty("readingAdded").GetInt32());
        Assert.Equal(2, root.GetProperty("onHoldAdded").GetInt32());
        Assert.Equal(4, root.GetProperty("droppedAdded").GetInt32());
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
