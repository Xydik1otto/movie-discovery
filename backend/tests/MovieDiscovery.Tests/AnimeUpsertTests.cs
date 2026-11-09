using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;

namespace MovieDiscovery.Tests;

/// <summary>Ідемпотентність upsert: повторний upsert не дублює і не псує стан (оновлює score/episodes).</summary>
public class AnimeUpsertTests
{
    private static Title TopPatch(int malId = 5114, double score = 9.09, int episodes = 64) =>
        new()
        {
            Type = "anime",
            MalId = malId,
            TitleEn = "Fullmetal Alchemist: Brotherhood",
            Genres = ["action", "drama"],
            Format = "tv",
            Episodes = episodes,
            ScoreMal = score,
            Popularity = 350,
        };

    [Fact]
    public void Resolve_Without_Candidates_Creates_Patch_As_Entity()
    {
        var patch = TopPatch();
        var (title, created) = AnimeUpsertCore.Resolve([], patch);

        Assert.True(created);
        Assert.Same(patch, title); // сам патч стає новою сутністю — без копій
        Assert.Equal(5114, title.MalId);
    }

    [Fact]
    public void Second_Upsert_Does_Not_Duplicate()
    {
        var (_, created1) = AnimeUpsertCore.Resolve([], TopPatch());
        Assert.True(created1);

        var existing = TopPatch(); // "збережений" у БД тайтл (id видано)
        existing.Id = 101;

        var patchAgain = TopPatch(score: 9.12, episodes: 64); // повторний синк — оцінка трохи оновилась
        var (title2, created2) = AnimeUpsertCore.Resolve([existing], patchAgain);

        Assert.False(created2);             // НЕ новий рядок
        Assert.Same(existing, title2);      // той самий запис
        AnimeUpsertCore.Merge(existing, patchAgain);
        Assert.Equal(9.12, existing.ScoreMal ?? 0, 6); // score оновлено
        Assert.Equal(64, existing.Episodes);
        Assert.Equal(101, existing.Id); // id не змінено
    }

    [Fact]
    public void Merge_Twice_Is_Stable_No_Array_Growth()
    {
        var target = TopPatch();
        target.Id = 42;
        AnimeUpsertCore.Merge(target, TopPatch());
        var genres1 = target.Genres;
        var score1 = target.ScoreMal;
        var pop1 = target.Popularity;
        var episodes1 = target.Episodes;

        AnimeUpsertCore.Merge(target, TopPatch());

        Assert.Equal(genres1, target.Genres);      // той самий вміст (послідовно), не виріс
        Assert.Equal(2, target.Genres!.Length);    // жанри замінені, не сконкатеновані
        Assert.Equal(score1, target.ScoreMal);
        Assert.Equal(pop1, target.Popularity);
        Assert.Equal(episodes1, target.Episodes);
    }

    [Fact]
    public void Merge_Does_Not_Null_Out_TitleUk()
    {
        var target = TopPatch();
        target.TitleUk = "Сталевий алхімік: Братство";

        var patchWithoutUk = TopPatch(); // Jikan не має uk-назви → TitleUk = null у патчі
        AnimeUpsertCore.Merge(target, patchWithoutUk);

        Assert.Equal("Сталевий алхімік: Братство", target.TitleUk); // НЕ затерто
    }

    [Fact]
    public void Merge_Fills_Missing_External_Ids()
    {
        // тайтл створено з AniList (тільки anilist_id) — потім прийшов MAL-патч
        var target = new Title
        {
            Type = "anime",
            AnilistId = 5114,
            TitleEn = "Fullmetal Alchemist: Brotherhood",
        };
        AnimeUpsertCore.Merge(target, TopPatch());

        Assert.Equal(5114, target.AnilistId);
        Assert.Equal(5114, target.MalId);     // дозаповнено
        Assert.Null(target.ShikimoriId);
        Assert.Equal(9.09, target.ScoreMal ?? 0, 6);
    }

    [Fact]
    public void Resolve_Matches_By_Anilist_When_Mal_Missing()
    {
        var existing = new Title { Id = 7, Type = "anime", AnilistId = 30, TitleEn = "Neon Genesis Evangelion" };
        var patch = new Title { Type = "anime", AnilistId = 30, TitleEn = "Neon Genesis Evangelion", ScoreAnilist = 8.6 };

        var (title, created) = AnimeUpsertCore.Resolve([existing], patch);
        Assert.False(created);
        Assert.Same(existing, title);

        AnimeUpsertCore.Merge(existing, patch);
        Assert.Equal(8.6, existing.ScoreAnilist ?? 0, 6);
    }

    [Fact]
    public void Resolve_Matches_By_Shikimori_Id()
    {
        var existing = new Title { Id = 9, Type = "manga", ShikimoriId = 1, TitleEn = "Berserk" };
        var patch = new Title { Type = "manga", ShikimoriId = 1, TitleEn = "Berserk", Chapters = 374 };

        var (title, created) = AnimeUpsertCore.Resolve([existing], patch);
        Assert.False(created);
        Assert.Same(existing, title);
    }

    [Fact]
    public void Resolve_Different_Ids_Creates_New()
    {
        var existing = TopPatch(malId: 5114);
        var patchOther = TopPatch(malId: 9253);
        var (title, created) = AnimeUpsertCore.Resolve([existing], patchOther);
        Assert.True(created);
        Assert.Equal(9253, title.MalId);
    }

    // ── Дедеп list-рядків у межах батчу (регрес 23505 імпорту shikimori 09.11) ──

    private static Core.Domain.ListEntry Row(string kind, Title title, int? rating) => new()
    {
        ProfileId = 7,
        Kind = kind,
        Title = title,
        Rating = rating,
        CreatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void DedupeBatchRows_Created_Title_Collapses_Later_Rating_Wins()
    {
        // два rate одного target у ОДНОМУ батчі: перший створює тайтл (Id=0),
        // другий резолвиться в нього ж (спільне посилання) — має лишитись ОДИН рядок
        var title = new Title { Type = "anime", TitleEn = "FMA:B", ShikimoriId = 5114 };
        var deduped = AnimeUpsertCore.DedupeBatchRows(
        [
            Row("watched", title, 10),
            Row("watched", title, 9),  // пізніший дубль — оцінка переноситься
        ]);

        var entry = Assert.Single(deduped);
        Assert.Equal(9, entry.Rating); // пізніший виграє
    }

    [Fact]
    public void DedupeBatchRows_Matched_Candidate_Collapses()
    {
        // обидва rate зійшлися на ОДНОМУ каталожному тайтлі (перетин mal/anilist/shikimori)
        var candidate = new Title { Id = 700, Type = "anime", TitleEn = "FMA:B", MalId = 5114 };
        var deduped = AnimeUpsertCore.DedupeBatchRows(
        [
            Row("watched", candidate, 8),
            Row("watched", candidate, 10),
        ]);

        var entry = Assert.Single(deduped);
        Assert.Equal(10, entry.Rating);
    }

    [Fact]
    public void DedupeBatchRows_Different_Kind_Or_Title_Are_Kept()
    {
        var t1 = new Title { Id = 700, TitleEn = "FMA:B" };
        var t2 = new Title { Id = 701, TitleEn = "Other" };
        var deduped = AnimeUpsertCore.DedupeBatchRows(
        [
            Row("watched", t1, 8),
            Row("watchlist", t1, null), // інший kind — УНІКАЛЬНІСТЬ включає kind → обидва свої
            Row("watched", t2, 7),      // інший тайтл
        ]);

        Assert.Equal(3, deduped.Count);
    }
}
