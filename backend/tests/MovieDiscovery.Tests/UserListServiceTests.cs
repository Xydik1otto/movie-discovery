using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Discover;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Taste;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>FR-16/17/18 через сервісний шар: списки юзера (liked/disliked/watched+rating), stats, migrate-guest, discover-дії.</summary>
public class UserListServiceTests
{
    private static (MovieDiscoveryDbContext Db, TasteProfileService Taste, UserListService Lists, DiscoverService Discover) New(string name)
    {
        var db = TestDb.Create(name);
        var taste = new TasteProfileService(db, Options.Create(new ProfileOptions()));
        var lists = new UserListService(db, taste);
        var discover = new DiscoverService(db, taste, lists, Options.Create(new DiscoverOptions()));
        return (db, taste, lists, discover);
    }

    private static Title NewTitle(int id, string[] genres, int? year = 2020, bool isAdult = false) => new()
    {
        Id = id, Type = "movie", TitleEn = $"T{id}", Year = year, Genres = genres,
        PosterPath = "/p.jpg", Popularity = 100 - id, RatingImdb = 7, IsAdult = isAdult,
    };

    [Fact]
    public async Task Add_Liked_And_Disliked_Writes_List_And_Log()
    {
        var (db, _, lists, _) = New("lists-kinds");
        db.Titles.Add(NewTitle(1, ["drama"]));
        await db.SaveChangesAsync();

        var liked = await lists.AddAsync("u1", ListKinds.Liked, titleId: 1, tmdbId: null, rating: null);
        Assert.True(liked.Ok);
        var disliked = await lists.AddAsync("u1", ListKinds.Disliked, titleId: 1, tmdbId: null, rating: null);

        Assert.True(disliked.Ok);
        Assert.Equal(2, await db.ListEntries.CountAsync()); // liked + disliked — два окремі рядки
        Assert.Equal(["like", "dislike"],
            (await db.InteractionLogs.OrderBy(l => l.Id).ToListAsync()).Select(l => l.Action).ToArray());
    }

    [Fact]
    public async Task Rating_Validation_Rejects_Out_Of_Range()
    {
        var (db, _, lists, _) = New("lists-rating-invalid");
        db.Titles.Add(NewTitle(1, ["drama"]));
        await db.SaveChangesAsync();

        Assert.False((await lists.AddAsync("u1", ListKinds.Watched, 1, null, 0)).Ok);
        Assert.False((await lists.AddAsync("u1", ListKinds.Watched, 1, null, 11)).Ok);
        Assert.True((await lists.AddAsync("u1", ListKinds.Watched, 1, null, 10)).Ok);
        Assert.True((await lists.AddAsync("u1", ListKinds.Watched, 1, null, null)).Ok is false); // дублікат → 409-семантика
    }

    [Fact]
    public async Task Stats_Counts_And_TopGenres()
    {
        var (db, _, lists, _) = New("lists-stats");
        db.Titles.Add(NewTitle(1, ["drama", "thriller"]));
        db.Titles.Add(NewTitle(2, ["drama"]));
        db.Titles.Add(NewTitle(3, ["horror"]));
        db.Titles.Add(NewTitle(4, ["comedy"]));
        await db.SaveChangesAsync();

        await lists.AddAsync("u1", ListKinds.Watched, 1, null, 8);
        await lists.AddAsync("u1", ListKinds.Watched, 2, null, 6);
        await lists.AddAsync("u1", ListKinds.Liked, 3, null, null);
        await lists.AddAsync("u1", ListKinds.Disliked, 4, null, null);

        var stats = await lists.StatsAsync("u1");

        Assert.Equal(2, stats.WatchedCount);
        Assert.Equal(1, stats.LikedCount);
        Assert.Equal(1, stats.DislikedCount);
        Assert.Equal(7.0, (double)stats.AvgRating!, precision: 6);
        Assert.Equal("drama", stats.TopGenres[0].Genre);
        Assert.Equal(2, stats.TopGenres[0].Count);
    }

    [Fact]
    public async Task Migrate_Guest_Is_Idempotent()
    {
        var (db, _, lists, _) = New("lists-migrate");
        db.Titles.Add(NewTitle(1, ["drama"]));
        db.Titles.Add(NewTitle(2, ["comedy"]));
        await db.SaveChangesAsync();

        var items = new List<GuestListItem>
        {
            new(ListKinds.Watched, 1, 8),
            new(ListKinds.Liked, 2, null),
            new(ListKinds.Disliked, 999, null), // неіснуючий тайтл → skipped
        };

        var (firstMoved, firstSkipped) = await lists.MigrateGuestAsync("u1", items);
        Assert.Equal(2, firstMoved);
        Assert.Equal(1, firstSkipped);

        // повторний перенос: нічого не дублює (UNIQUE-семантика)
        var (secondMoved, secondSkipped) = await lists.MigrateGuestAsync("u1", items);
        Assert.Equal(0, secondMoved);
        Assert.Equal(3, secondSkipped);
        Assert.Equal(2, await db.ListEntries.CountAsync());

        // перенос згенерував профільний сигнал: негативних жанрів нема, позитивні є
        var taste = new TasteProfileService(db, Options.Create(new ProfileOptions()));
        var up = await taste.ResolveAsync("u1");
        Assert.NotEmpty(MovieDiscovery.Core.Taste.TasteJson.Parse(up.TasteGenres));
    }

    [Fact]
    public async Task Delete_Removes_Own_Entry()
    {
        var (db, _, lists, _) = New("lists-delete");
        db.Titles.Add(NewTitle(1, ["drama"]));
        await db.SaveChangesAsync();

        var added = await lists.AddAsync("u1", ListKinds.Watchlist, 1, null, null);
        Assert.True(await lists.DeleteAsync("u1", added.ItemId!.Value));
        Assert.False(await lists.DeleteAsync("u1", added.ItemId.Value));
    }
}

/// <summary>FR-18: DiscoverService на InMemory — виключення переглянутого, mode, дії свайпу, негативні жанри.</summary>
public class DiscoverServiceTests
{
    private static (MovieDiscoveryDbContext Db, TasteProfileService Taste, UserListService Lists, DiscoverService Discover) New(string name)
    {
        var db = TestDb.Create(name);
        var taste = new TasteProfileService(db, Options.Create(new ProfileOptions()));
        var lists = new UserListService(db, taste);
        var discover = new DiscoverService(db, taste, lists, Options.Create(new DiscoverOptions()));
        return (db, taste, lists, discover);
    }

    private static Title NewTitle(int id, string[] genres, int? year = 2020, bool isAdult = false, double imdb = 7) => new()
    {
        Id = id, Type = "movie", TitleEn = $"T{id}", Year = year, Genres = genres,
        PosterPath = "/p.jpg", Popularity = 200 - id, RatingImdb = imdb, IsAdult = isAdult,
    };

    [Fact]
    public async Task Feed_Excludes_Already_Listed_And_Adult()
    {
        var (db, taste, lists, discover) = New("discover-exclude");
        db.Titles.AddRange(Enumerable.Range(1, 15).Select(i => NewTitle(i, [$"genre{i % 7}"])));
        db.Titles.Add(NewTitle(90, ["adultgenre"], isAdult: true));
        await db.SaveChangesAsync();

        var up = await taste.ResolveAsync("u1");
        db.ListEntries.Add(new ListEntry { ProfileId = up.ProfileId, Kind = ListKinds.Liked, TitleId = 1 });
        await db.SaveChangesAsync();

        var (items, mode) = await discover.GetFeedAsync("u1", 10, "uk", allowAdult: false);

        Assert.DoesNotContain(items, t => t.Id == 1);
        Assert.DoesNotContain(items, t => t.IsAdult);
        Assert.Equal(10, items.Count);
        Assert.Equal("safe", mode); // порожній профіль → popularity+safe
    }

    [Fact]
    public async Task Adult_Included_When_Allowed()
    {
        var (db, _, _, discover) = New("discover-adult");
        // єдиний кандидат — adult
        db.Titles.Add(NewTitle(90, ["adultgenre"]));
        await db.SaveChangesAsync();
        var adult = await db.Titles.SingleAsync(t => t.Id == 90);
        adult.IsAdult = true;
        await db.SaveChangesAsync();

        var (denied, _) = await discover.GetFeedAsync("u1", 10, "uk", allowAdult: false);
        Assert.Empty(denied);

        var (allowed, _) = await discover.GetFeedAsync("u1", 10, "uk", allowAdult: true);
        Assert.Single(allowed);
    }

    [Fact]
    public async Task Dislike_Action_Writes_Negative_Genre_Weight()
    {
        var (db, taste, _, discover) = New("discover-dislike");
        db.Titles.Add(NewTitle(1, ["horror"]));
        await db.SaveChangesAsync();

        var result = await discover.HandleActionAsync("u1", titleId: 1, action: "dislike", rating: null);

        Assert.True(result.Ok);
        Assert.True(result.ProfileUpdated);
        var up = await taste.ResolveAsync("u1");
        var negative = MovieDiscovery.Core.Taste.TasteJson.Parse(up.NegativeGenres);
        Assert.True(negative.ContainsKey("horror"));
        Assert.Equal(0.8, negative["horror"], precision: 6);
        Assert.True(await db.ListEntries.AnyAsync(l => l.Kind == ListKinds.Disliked && l.TitleId == 1));
        Assert.True(await db.InteractionLogs.AnyAsync(l => l.Action == "dislike" && l.TitleId == 1));
    }

    [Fact]
    public async Task Skip_Does_Not_Touch_Profile()
    {
        var (db, taste, _, discover) = New("discover-skip");
        db.Titles.Add(NewTitle(1, ["horror"]));
        await db.SaveChangesAsync();

        var result = await discover.HandleActionAsync("u1", 1, "skip", null);

        Assert.True(result.Ok);
        Assert.False(result.ProfileUpdated);
        Assert.False(await db.ListEntries.AnyAsync());
        var up = await taste.ResolveAsync("u1");
        Assert.Equal([], MovieDiscovery.Core.Taste.TasteJson.Parse(up.NegativeGenres));
    }

    [Fact]
    public async Task Repeat_Action_Is_Idempotent_No_Profile_Change()
    {
        var (db, taste, _, discover) = New("discover-repeat");
        db.Titles.Add(NewTitle(1, ["drama"]));
        await db.SaveChangesAsync();

        var first = await discover.HandleActionAsync("u1", 1, "like", null);
        var second = await discover.HandleActionAsync("u1", 1, "like", null);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.True(first.ProfileUpdated);
        Assert.False(second.ProfileUpdated); // дублікат не перераховує профіль
        Assert.Single(await db.ListEntries.ToListAsync());
    }

    [Fact]
    public async Task Watched_Action_With_Rating_Stores_Both()
    {
        var (db, _, _, discover) = New("discover-watched");
        db.Titles.Add(NewTitle(1, ["drama"]));
        await db.SaveChangesAsync();

        var ok = await discover.HandleActionAsync("u1", 1, "watched", rating: 9);
        var bad = await discover.HandleActionAsync("u1", 1, "watched", rating: 11);

        Assert.True(ok.Ok);
        Assert.True(await db.ListEntries.AnyAsync(l => l.Kind == ListKinds.Watched && l.Rating == 9));
        Assert.False(bad.Ok); // rating 11 → invalid_request
    }

    [Fact]
    public async Task Unknown_Action_Rejected()
    {
        var (db, _, _, discover) = New("discover-unknown-action");
        db.Titles.Add(NewTitle(1, ["drama"]));
        await db.SaveChangesAsync();

        var result = await discover.HandleActionAsync("u1", 1, "hate", null);
        Assert.False(result.Ok);
        Assert.Equal("invalid_request", result.Error);
    }

    [Fact]
    public async Task Mode_Turns_Exploit_After_Taste_Signal()
    {
        var (db, taste, lists, discover) = New("discover-mode");
        db.Titles.AddRange(Enumerable.Range(1, 12).Select(i => NewTitle(i, [$"genre{i % 6}"])));
        await db.SaveChangesAsync();

        var (_, beforeMode) = await discover.GetFeedAsync("u1", 5, "uk", false);
        Assert.Equal("safe", beforeMode);

        await lists.AddAsync("u1", ListKinds.Liked, 1, null, null);
        var (_, afterMode) = await discover.GetFeedAsync("u1", 5, "uk", false);
        Assert.Equal("exploit", afterMode);
    }
}
