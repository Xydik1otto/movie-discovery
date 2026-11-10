using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Taste;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>FR-17: TasteProfileService — ваги, half-life, негативні жанри, інкремент, cold-start онбордингу.</summary>
public class TasteProfileTests
{
    private static (MovieDiscoveryDbContext Db, TasteProfileService Svc) New(string name)
    {
        var db = TestDb.Create(name);
        var svc = new TasteProfileService(db, Options.Create(new ProfileOptions()));
        return (db, svc);
    }

    private static Title NewTitle(int id, string[] genres, int? year = 2020, string[]? actors = null, string[]? keywords = null) => new()
    {
        Id = id, Type = "movie", TitleEn = $"T{id}", Year = year,
        Genres = genres, Actors = actors ?? [], Keywords = keywords ?? [],
        PosterPath = "/p.jpg", Popularity = 10,
    };

    [Fact]
    public async Task Like_Adds_Genre_Weight_1()
    {
        var (db, svc) = New("taste-like");
        db.Titles.Add(NewTitle(1, ["drama"]));
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 1, Action = "like" });
        await db.SaveChangesAsync();

        var up = await svc.RecomputeAsync("u1");
        var genres = TasteJson.Parse(up.TasteGenres);

        Assert.Equal(1.0, genres["drama"], precision: 6);
    }

    [Fact]
    public async Task Rate_9_Contributes_Rating_Formula()
    {
        var (db, svc) = New("taste-rate");
        db.Titles.Add(NewTitle(1, ["action"]));
        // watched з оцінкою 9 → дія rate: (9−5.5)/4.5×1.2
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 1, Action = "rate", Value = 9 });
        await db.SaveChangesAsync();

        var up = await svc.RecomputeAsync("u1");
        var genres = TasteJson.Parse(up.TasteGenres);

        Assert.Equal((9 - 5.5) / 4.5 * 1.2, genres["action"], precision: 6);
    }

    [Fact]
    public async Task Rate_5_Is_Near_Zero()
    {
        var (db, svc) = New("taste-rate-5");
        db.Titles.Add(NewTitle(1, ["action"]));
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 1, Action = "rate", Value = 5 });
        await db.SaveChangesAsync();

        var up = await svc.RecomputeAsync("u1");
        var genres = TasteJson.Parse(up.TasteGenres);

        // (5−5.5)/4.5×1.2 ≈ −0.13 — легкий негативний внесок
        Assert.Equal((5 - 5.5) / 4.5 * 1.2, genres["action"], precision: 6);
    }

    [Fact]
    public async Task Dislike_Writes_Negative_Genre_Weight()
    {
        var (db, svc) = New("taste-dislike");
        db.Titles.Add(NewTitle(1, ["horror", "thriller"]));
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 1, Action = "dislike" });
        await db.SaveChangesAsync();

        var up = await svc.RecomputeAsync("u1");
        var negative = TasteJson.Parse(up.NegativeGenres);
        var genres = TasteJson.Parse(up.TasteGenres);

        Assert.Equal(0.8, negative["horror"], precision: 6);
        Assert.Equal(0.8, negative["thriller"], precision: 6);
        Assert.Equal(-0.8, genres["horror"], precision: 6);
    }

    [Fact]
    public async Task Half_Life_60_Days_Decays_Contribution_Half()
    {
        var (db, svc) = New("taste-halflife");
        db.Titles.Add(NewTitle(1, ["drama"]));
        db.InteractionLogs.Add(new InteractionLog
        {
            UserId = "u1", TitleId = 1, Action = "like",
            CreatedAt = DateTime.UtcNow.AddDays(-60), // один період напівжиття
        });
        await db.SaveChangesAsync();

        var up = await svc.RecomputeAsync("u1");
        var genres = TasteJson.Parse(up.TasteGenres);

        Assert.Equal(0.5, genres["drama"], precision: 6);
    }

    [Fact]
    public async Task Recompute_Is_Incremental_Accumulating()
    {
        var (db, svc) = New("taste-increment");
        db.Titles.Add(NewTitle(1, ["drama"]));
        db.Titles.Add(NewTitle(2, ["drama", "comedy"]));
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 1, Action = "like" });
        await db.SaveChangesAsync();

        var first = await svc.RecomputeAsync("u1");
        Assert.Equal(1.0, TasteJson.Parse(first.TasteGenres)["drama"], precision: 6);

        // нова дія → другий перерахунок акумулює (профіль з interaction_log, не втрачається)
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 2, Action = "like" });
        await db.SaveChangesAsync();

        var second = await svc.RecomputeAsync("u1");
        var genres = TasteJson.Parse(second.TasteGenres);
        Assert.Equal(2.0, genres["drama"], precision: 6);
        Assert.Equal(1.0, genres["comedy"], precision: 6);
    }

    [Fact]
    public async Task Recompute_Is_Idempotent_On_Same_Log()
    {
        var (db, svc) = New("taste-idempotent");
        db.Titles.Add(NewTitle(1, ["drama"]));
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 1, Action = "like" });
        await db.SaveChangesAsync();

        var a = await svc.RecomputeAsync("u1");
        var b = await svc.RecomputeAsync("u1");

        Assert.Equal(TasteJson.Parse(a.TasteGenres), TasteJson.Parse(b.TasteGenres));
    }

    [Fact]
    public async Task Watchlist_And_Skip_Weights()
    {
        var (db, svc) = New("taste-watchlist-skip");
        db.Titles.Add(NewTitle(1, ["drama"]));
        db.Titles.Add(NewTitle(2, ["comedy"]));
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 1, Action = "watchlist" });
        db.InteractionLogs.Add(new InteractionLog { UserId = "u1", TitleId = 2, Action = "skip" });
        await db.SaveChangesAsync();

        var up = await svc.RecomputeAsync("u1");
        var genres = TasteJson.Parse(up.TasteGenres);

        Assert.Equal(0.4, genres["drama"], precision: 6);
        Assert.False(genres.ContainsKey("comedy")); // skip на профіль не впливає
    }

    [Fact]
    public async Task Onboarding_Cold_Start_Picks_And_Favorites()
    {
        var (db, svc) = New("taste-onboarding");
        db.Titles.Add(NewTitle(10, ["science fiction", "action"], year: 2015, actors: ["K. Reeves"], keywords: ["space"]));
        await db.SaveChangesAsync();

        var up = await svc.ApplyOnboardingAsync("u1",
            picks: ["science fiction", "comedy"], mood: ["thriller"], era: ["1990s"],
            favoriteTitles: [10], skipped: false);

        var genres = TasteJson.Parse(up.TasteGenres);
        Assert.True(up.OnboardingDone);
        Assert.Equal(0.6, genres["comedy"], precision: 6);      // pick ×0.6
        Assert.Equal(0.6, genres["thriller"], precision: 6);    // mood ×0.6
        Assert.Equal(0.6 + 2.0, genres["science fiction"], precision: 6); // pick 0.6 + favorite 2 лайки
        Assert.Equal(2.0, TasteJson.Parse(up.TasteActors)["K. Reeves"], precision: 6);
        Assert.Equal(0.6, TasteJson.Parse(up.TasteEra)["1990s"], precision: 6);
        Assert.Equal(2.0, TasteJson.Parse(up.TasteKeywords)["space"], precision: 6);

        // фаворит записаний у interaction_log як like×2 → повторний Recompute дає ту саму вагу
        var re = await svc.RecomputeAsync("u1");
        Assert.Equal(0.6 + 2.0, TasteJson.Parse(re.TasteGenres)["science fiction"], precision: 6);

        var quizGenres = TasteProfileService.ParseQuizGenres(up.Quiz);
        Assert.Contains("science fiction", quizGenres);
        Assert.Contains("thriller", quizGenres);
    }

    [Fact]
    public async Task Onboarding_Skipped_Keeps_Onboarding_Undone_But_Stores_Quiz()
    {
        var (db, svc) = New("taste-onboarding-skip");

        var up = await svc.ApplyOnboardingAsync("u1", [], [], [], [], skipped: true);

        Assert.False(up.OnboardingDone); // TZ FR-18: пропущений квіз не блокує нічого
        Assert.Contains("skipped", up.Quiz);
        Assert.Equal([], TasteJson.Parse(up.TasteGenres));
    }

    [Fact]
    public async Task Resolve_Creates_UserProfile_Linked_To_Profile()
    {
        var (db, svc) = New("taste-resolve");

        var up = await svc.ResolveAsync("u42");

        Assert.Equal("u42", up.UserId);
        var profile = await db.Profiles.SingleAsync(p => p.Id == up.ProfileId);
        Assert.NotNull(profile);
    }
}
