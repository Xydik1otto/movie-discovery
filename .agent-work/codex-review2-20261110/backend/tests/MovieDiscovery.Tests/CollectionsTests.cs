using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Tests;

/// <summary>FR-21: збережені добірки — CRUD, валідація (≤30), ізоляція юзерів, пресети (3 ключі).
/// Сервісний рівень; авторизація/ownerId-з-клеймів — тонкий контролер (без HTTP у тестах репо).</summary>
public class CollectionsTests
{
    private static (MovieDiscoveryDbContext Db, CollectionsService Svc) Build(string name)
    {
        var db = TestDb.Create(name);
        return (db, new CollectionsService(db));
    }

    private static Title T(int id, string type = "movie", string[]? genres = null, double? rating = null,
        int? runtime = null, double popularity = 10, string? titleUk = null) => new()
    {
        Id = id,
        TmdbId = id,
        Type = type,
        TitleEn = $"t{id}",
        TitleUk = titleUk,
        Genres = genres ?? [],
        RatingImdb = rating,
        RuntimeMin = runtime,
        Popularity = popularity,
    };

    // ── CRUD + ізоляція ──

    [Fact]
    public async Task Create_Get_Own_Isolated_From_Other_Users()
    {
        var (db, svc) = Build($"coll-{Guid.NewGuid():N}");
        db.Titles.AddRange(T(1), T(2), T(3));
        await db.SaveChangesAsync();

        var (a, errA, _) = await svc.CreateAsync("user-a", "Вечірні трилери", [1, 2], default);
        var (b, errB, _) = await svc.CreateAsync("user-b", "Мої улюблені", [3], default);
        Assert.Null(errA);
        Assert.Null(errB);
        Assert.NotNull(a);
        Assert.NotNull(b);

        // ownerId береться СТРОГО з переданого userId (контролер подає claims-юзера, не body)
        Assert.Equal("user-a", a!.UserId);
        Assert.Equal("user-b", b!.UserId);

        var ownA = await svc.GetOwnAsync("user-a", default);
        var ownB = await svc.GetOwnAsync("user-b", default);
        Assert.Single(ownA);
        Assert.Equal([1L, 2L], ownA[0].Items.Select(i => i.TitleId).ToList()); // порядок ord збережено
        Assert.Single(ownB);
        Assert.Equal([3L], ownB[0].Items.Select(i => i.TitleId).ToList());

        // юзер А не може ні побачити, ні видалити добірку юзера Б (ізоляція)
        Assert.False(await svc.DeleteAsync("user-a", b!.Id, default));
        Assert.True(await svc.DeleteAsync("user-b", b.Id, default));
        Assert.Empty(await svc.GetOwnAsync("user-b", default));
        Assert.Single(await svc.GetOwnAsync("user-a", default)); // А-добірка не постраждала
    }

    [Fact]
    public async Task Create_Validates_Name_Count_UnknownTitles()
    {
        var (db, svc) = Build($"coll-{Guid.NewGuid():N}");
        for (var i = 1; i <= 31; i++) db.Titles.Add(T(i));
        await db.SaveChangesAsync();

        var (_, emptyName, _) = await svc.CreateAsync("user-a", "   ", [1], default);
        Assert.Equal("invalid_name", emptyName);
        var (_, tooLong, _) = await svc.CreateAsync("user-a", new string('x', 121), [1], default);
        Assert.Equal("invalid_name", tooLong);

        var (_, tooMany, _) = await svc.CreateAsync("user-a", "ok", Enumerable.Range(1, 31).Select(i => (long)i).ToList(), default);
        Assert.Equal("too_many_items", tooMany);

        // дублікати зливаються; ліміт рахується по унікальних
        var (dup, dupErr, _) = await svc.CreateAsync("user-a", "dup", [1, 1, 2, 1], default);
        Assert.Null(dupErr);
        Assert.Equal([1L, 2L], dup!.Items.OrderBy(i => i.Ord).Select(i => i.TitleId).ToList());

        var (_, unknownErr, unknown) = await svc.CreateAsync("user-a", "ok", [1, 999, 1000], default);
        Assert.Equal("unknown_titles", unknownErr);
        Assert.Equal([999L, 1000L], unknown);
    }

    // ── Пресети ──

    [Fact]
    public async Task Presets_Return_Three_Server_Keys()
    {
        var (db, svc) = Build($"coll-{Guid.NewGuid():N}");
        var presets = await svc.GetPresetsAsync(default);

        Assert.Equal(3, presets.Count);
        Assert.Equal(["preset.mood", "preset.evening", "preset.duration"], presets.Select(p => p.Key).ToList());
        Assert.All(presets, p => Assert.True(p.Items.Count <= CollectionsService.PresetSize));
    }

    [Fact]
    public async Task Presets_Mood_TopRated_ByGenre()
    {
        var (db, svc) = Build($"coll-{Guid.NewGuid():N}");
        db.Titles.AddRange(
            T(1, genres: ["comedy"], rating: 8.0, popularity: 50),
            T(2, genres: ["romance", "music"], rating: 7.2, popularity: 40),
            T(3, genres: ["comedy"], rating: 6.9, popularity: 99), // нижче порогу 7 → геть
            T(4, genres: ["horror"], rating: 9.0, popularity: 99)); // не mood-жанр → геть
        await db.SaveChangesAsync();

        var mood = (await svc.GetPresetsAsync(default)).First(p => p.Key == "preset.mood").Items;
        Assert.Equal([1, 2], mood.Select(t => t.Id).ToList()); // впорядковано за рейтингом
        Assert.All(mood, t => Assert.True((t.RatingTmdb ?? t.RatingImdb) >= 7.0));
    }

    [Fact]
    public async Task Presets_Duration_Short_Movies_Only()
    {
        var (db, svc) = Build($"coll-{Guid.NewGuid():N}");
        db.Titles.AddRange(
            T(1, runtime: 90, rating: 7.0),
            T(2, runtime: 120, rating: 9.5),                              // задовгий
            T(3, type: "series", runtime: null, rating: 9.0),             // серіал без runtime → геть
            T(4, runtime: 30, rating: 6.0));                              // короткометражка — ок
        await db.SaveChangesAsync();

        var duration = (await svc.GetPresetsAsync(default)).First(p => p.Key == "preset.duration").Items;
        Assert.Equal([1, 4], duration.Select(t => t.Id).ToList());
        Assert.All(duration, t => Assert.True(t.RuntimeMin is > 0 and < 95));
    }

    [Fact]
    public async Task Presets_WatchLinks_Priority_Beats_Rating_And_Caps_12()
    {
        var (db, svc) = Build($"coll-{Guid.NewGuid():N}");
        // 13 mood-кандидатів: 12 з rating 8.0 + один з 7.5, але З доступним watch_link
        for (var i = 1; i <= 12; i++)
            db.Titles.Add(T(i, genres: ["comedy"], rating: 8.0, popularity: 100 - i));
        db.Titles.Add(T(13, genres: ["comedy"], rating: 7.5, popularity: 1, titleUk: "З українською"));
        db.WatchLinks.Add(new WatchLink { TitleId = 13, Provider = "sweettv", Kind = "subscription", Region = "UA", IsAvailable = true });
        await db.SaveChangesAsync();

        var mood = (await svc.GetPresetsAsync(default)).First(p => p.Key == "preset.mood").Items;
        Assert.Equal(CollectionsService.PresetSize, mood.Count);   // кап 12
        Assert.Equal(13, mood[0].Id);                              // watch_links пріоритетніший за рейтинг
    }
}
