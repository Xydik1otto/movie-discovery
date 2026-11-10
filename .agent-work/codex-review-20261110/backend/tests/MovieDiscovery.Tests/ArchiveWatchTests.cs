using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

/// <summary>Нормалізація назв і матчинг «назва + рік ±1» (FR-14: Internet Archive).</summary>
public class TitleMatcherTests
{
    [Theory]
    [InlineData("Night of the Living Dead (1968)", "night of the living dead")]
    [InlineData("Detour (1945)", "detour")]
    [InlineData("The  Office!", "office")]
    [InlineData("A Clockwork Orange", "clockwork orange")]
    [InlineData("AN AMERICAN IN PARIS", "american in paris")]
    [InlineData("Ти — космос!", "ти космос")]
    public void Normalize_Strips_Punctuation_And_Articles(string input, string expected) =>
        Assert.Equal(expected, TitleNormalizer.Normalize(input));

    [Fact]
    public void Find_Matches_Name_With_Year_Delta_One()
    {
        var candidates = new List<Title> { new() { Id = 1, Type = "movie", TitleEn = "Night of the Living Dead", Year = 1968 } };

        Assert.NotNull(TitleMatcher.Find(candidates, null, "Night of the Living Dead (1968)", 1969)); // ±1
        Assert.NotNull(TitleMatcher.Find(candidates, null, "The Night of the Living Dead!", 1968));   // пунктуація/артиклі
        Assert.Null(TitleMatcher.Find(candidates, null, "Night of the Living Dead", 1990));           // рік далеко
    }

    [Fact]
    public void Find_Without_Doc_Year_Matches_Any_Year()
    {
        var candidates = new List<Title> { new() { Id = 2, Type = "movie", TitleEn = "Plan 9 from Outer Space", Year = 1957 } };
        Assert.NotNull(TitleMatcher.Find(candidates, null, "Plan 9 from Outer Space", null));
    }

    [Fact]
    public void Find_Prefers_En_But_Accepts_Uk()
    {
        var en = new Title { Id = 3, Type = "movie", TitleEn = "The Kid", Year = 1921 };
        var uk = new Title { Id = 4, Type = "movie", TitleEn = "Something Else", TitleUk = "Малюк", Year = 1921 };
        var candidates = new List<Title> { uk, en };

        var byEn = TitleMatcher.Find(candidates, null, "The Kid", 1921);
        Assert.Equal(3, byEn!.Id);

        var byUk = TitleMatcher.Find(candidates, "Малюк", null, 1921);
        Assert.Equal(4, byUk!.Id);
    }

    [Fact]
    public void Find_Empty_Names_Returns_Null()
    {
        var candidates = new List<Title> { new() { Id = 5, Type = "movie", TitleEn = "X", Year = 2000 } };
        Assert.Null(TitleMatcher.Find(candidates, "  ", null, 2000));
    }
}

/// <summary>ArchiveWatchService (FR-14) на InMemory + HTTP-стаб: матч → watch_links, повторний sync не дублює.</summary>
public class ArchiveWatchServiceTests
{
    [Fact]
    public void SplitTitleYear_Strips_Trailing_Year()
    {
        var (title, year) = ArchiveWatchService.SplitTitleYear(new IaDoc("id1", "Night of the Living Dead (1968)", null));
        Assert.Equal("Night of the Living Dead", title);
        Assert.Equal(1968, year);

        var (t2, y2) = ArchiveWatchService.SplitTitleYear(new IaDoc("id2", "Detour", 1945));
        Assert.Equal("Detour", t2);
        Assert.Equal(1945, y2);
    }

    [Fact]
    public async Task Sync_Matches_And_Creates_Links_Idempotently()
    {
        var dbName = "archive-sync";
        const string json = """
            {"responseHeader":{"status":0},"response":{"numFound":2,"docs":[
              {"identifier":"night_of_the_living_dead","title":"Night of the Living Dead (1968)","year":1968},
              {"identifier":"detour_1945","title":"Detour","year":1945},
              {"identifier":"unmatchable-film","title":"Zzz Unknown Picture","year":1950}
            ]}}
            """;
        var http = StubHttp.Client(_ => StubHttp.Json(json));
        await using var db = TestDb.Create(dbName);
        db.Titles.AddRange(
            new Title { Type = "movie", TitleEn = "Night of the Living Dead", Year = 1968 },
            new Title { Type = "movie", TitleEn = "Detour", Year = 1946 }); // рік ±1 → матч
        await db.SaveChangesAsync();

        var service = new ArchiveWatchService(http, db, Logs.Null<ArchiveWatchService>());
        var result = await service.SyncAsync(pages: 1);

        Assert.Equal(3, result.Docs);
        Assert.Equal(2, result.Matched);
        Assert.Equal(2, result.Links);

        Assert.Equal(2, await db.WatchLinks.CountAsync()); // нематчений тайтл НЕ створено
        Assert.Equal(2, await db.Titles.CountAsync());

        var link = await db.WatchLinks.FirstAsync(w => w.Provider == "archive");
        Assert.Equal("free", link.Kind);
        Assert.Equal("*", link.Region);
        Assert.True(link.IsAvailable);
        Assert.StartsWith("https://archive.org/details/", link.Url);

        // повторний sync — не дублює
        var again = await service.SyncAsync(pages: 1);
        Assert.Equal(2, again.Links);
        Assert.Equal(2, await db.WatchLinks.CountAsync());
    }

    [Fact]
    public async Task Sync_Http_Failure_Is_Not_Fatal()
    {
        await using var db = TestDb.Create("archive-http-fail");
        var http = StubHttp.Client(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        var service = new ArchiveWatchService(http, db, Logs.Null<ArchiveWatchService>());

        var result = await service.SyncAsync(pages: 2);
        Assert.Equal(0, result.Links);
        Assert.Equal(0, await db.WatchLinks.CountAsync());
    }
}
