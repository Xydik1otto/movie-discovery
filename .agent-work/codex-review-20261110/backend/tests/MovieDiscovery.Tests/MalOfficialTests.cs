using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Tests;

/// <summary>
/// Офіційний MAL API v2: парсинг сторінок (data+paging), мапінг media_type → type/format,
/// статуси, runtime (movie → RuntimeMin, серіал → EpisodeRuntimeMin), SyncSkip без client id,
/// URL-будування сезону/топів. Фікстури без HTTP.
/// </summary>
public class MalOfficialTests
{
    private const string SeasonPageJson = """
        {
          "data": [
            { "id": 52991, "title": "Sousou no Frieren",
              "main_picture": { "large": "https://cdn.myanimelist.net/images/anime/1015/138006l.jpg",
                                "medium": "https://cdn.myanimelist.net/images/anime/1015/138006.jpg" },
              "alternative_titles": { "en": "Frieren: Beyond Journey's End", "ja": "葬送のフリーレン" },
              "start_date": "2023-09-29", "end_date": "2024-03-22",
              "synopsis": "During their journey, the mage Frieren mourns her companions.",
              "mean": 9.3, "num_list_users": 850000,
              "media_type": "tv", "status": "currently_airing",
              "genres": [ { "id": 1, "name": "Action" }, { "id": 38, "name": "Award Winning" }, { "id": 62, "name": "Isekai" } ],
              "studios": [ { "id": 7, "name": "Madhouse" } ],
              "rating": "pg_13", "num_episodes": 28, "average_episode_duration": 1440 },
            { "id": 41467, "title": null,
              "main_picture": { "medium": "https://cdn.myanimelist.net/images/anime/1792/110460.jpg" },
              "alternative_titles": { "en": "Demon Slayer: Mugen Train", "ja": "劇場版「鬼滅の刃」無限列車編" },
              "start_date": "2020-10-16", "mean": 8.46, "num_list_users": 400000,
              "media_type": "movie", "status": "finished_airing",
              "genres": [ { "id": 24, "name": "Sci-Fi" }, { "id": 17, "name": "Martial Arts" } ],
              "num_episodes": 1, "average_episode_duration": 7140 }
          ],
          "paging": { "next": "https://api.myanimelist.net/v2/anime/season/2023/fall?limit=100&offset=100&fields=id,title", "previous": null }
        }
        """;

    private const string MangaPageJson = """
        {
          "data": [
            { "id": 2, "title": "Berserk", "start_date": "1989-08-25",
              "mean": 9.24, "num_list_users": 250000,
              "media_type": "manga", "status": "finished_publishing",
              "genres": [ { "id": 8, "name": "Drama" }, { "id": 14, "name": "Horror" } ] },
            { "id": 104853, "title": "Omniscient Reader", "start_date": "2018-01-01",
              "mean": 8.1, "num_list_users": 90000,
              "media_type": "manhwa", "status": "currently_publishing",
              "genres": [ { "id": 1, "name": "Action" } ] },
            { "id": 121496, "title": "Frieren: Beyond Journey's End",
              "start_date": "2020-04-28", "mean": 9.1, "num_list_users": 300000,
              "media_type": "light_novel", "status": "currently_publishing",
              "genres": [ { "id": 10, "name": "Fantasy" } ] }
          ],
          "paging": { "next": null, "previous": null }
        }
        """;

    private static MalPage<MalNode> Parse(string json) =>
        JsonSerializer.Deserialize<MalPage<MalNode>>(json, TmdbJson.Options)!;

    [Fact]
    public void Season_Anime_Node_Maps_Type_Status_Runtime_Score_Popularity()
    {
        var page = Parse(SeasonPageJson);
        var node = page.Data[0];

        var t = MalOfficialMapper.Build(node, "anime");
        Assert.Equal("anime", t.Type);            // media_type tv → anime
        Assert.Equal(52991, t.MalId);
        Assert.Equal("Sousou no Frieren", t.TitleEn); // title пріоритет над alternative_titles.en
        Assert.Null(t.TitleUk);                       // uk-назв у MAL нема — не пишемо
        Assert.NotNull(t.OverviewEn);
        Assert.Equal(2023, t.Year);               // з start_date
        Assert.Equal("airing", t.Status);         // currently_airing → airing
        Assert.Equal("tv", t.Format);
        Assert.Equal(28, t.Episodes);             // num_episodes
        Assert.Equal(24, t.EpisodeRuntimeMin);    // серіал: 1440с → 24 хв
        Assert.Null(t.RuntimeMin);                // не фільм
        Assert.Equal(9.3, t.RatingTmdb ?? 0, 6);  // mean → rating_tmdb
        Assert.Equal(9.3, t.ScoreMal ?? 0, 6);    // mean — це і є MAL-оцінка
        Assert.Equal(85.0, t.Popularity, 6);      // num_list_users 850000 → /10000
        Assert.Equal(new[] { "action", "award winning", "isekai" }, t.Genres);
        Assert.Equal(new[] { "Madhouse" }, t.Studios);
        Assert.StartsWith("https://cdn.myanimelist.net/", t.PosterPath);
        // пейджинг по next збережено в моделі
        Assert.NotNull(page.Paging?.Next);
    }

    [Fact]
    public void Movie_Node_Title_Falls_Back_To_Alternative_And_Runtime_Goes_To_RuntimeMin()
    {
        var node = Parse(SeasonPageJson).Data[1];

        var t = MalOfficialMapper.Build(node, "anime");
        Assert.Equal("anime", t.Type);
        Assert.Equal(41467, t.MalId);
        Assert.Equal("Demon Slayer: Mugen Train", t.TitleEn); // title порожній → alternative_titles.en
        Assert.Equal("movie", t.Format);
        Assert.Equal("finished", t.Status);       // finished_airing → finished
        Assert.Equal(119, t.RuntimeMin);          // фільм: 7140с → 119 хв
        Assert.Null(t.EpisodeRuntimeMin);         // не серіал
        Assert.Equal(1, t.Episodes);              // num_episodes=1 у фільмів — як і в Jikan-шляху
        Assert.Equal(new[] { "martial arts", "science fiction" }, t.Genres); // Sci-Fi → science fiction
        Assert.Equal(8.46, t.RatingTmdb ?? 0, 6);
        Assert.Equal(40.0, t.Popularity, 6);
        Assert.Equal("https://cdn.myanimelist.net/images/anime/1792/110460.jpg", t.PosterPath); // medium-фолбек
    }

    [Theory]
    [InlineData("manga", "manga", "manga")]
    [InlineData("manhwa", "manga", "manhwa")]
    [InlineData("manhua", "manga", null)] // manhua поза канонічним набором форматів → format null
    [InlineData("light_novel", "manga", "novel")]
    [InlineData("one_shot", "manga", "one_shot")]
    [InlineData("tv", "anime", "tv")]
    [InlineData("ova", "anime", "ova")]
    [InlineData("ona", "anime", "ona")]
    [InlineData("special", "anime", "special")]
    [InlineData("music", "anime", null)] // невідомий media_type → fallback-тип ендпоінта, format null
    public void MediaType_Determines_Type_And_Format(string mediaType, string expectedType, string? expectedFormat)
    {
        var node = new MalNode { Id = 1, Title = "X", MediaType = mediaType };
        var t = MalOfficialMapper.Build(node, "anime");
        Assert.Equal(expectedType, t.Type);
        Assert.Equal(expectedFormat, t.Format);
    }

    [Theory]
    [InlineData("currently_airing", "airing")]
    [InlineData("currently_publishing", "airing")]
    [InlineData("finished_airing", "finished")]
    [InlineData("finished_publishing", "finished")]
    [InlineData("not_yet_aired", "upcoming")]
    [InlineData("not_yet_published", "upcoming")]
    [InlineData("on_hiatus", null)]
    [InlineData("discontinued", null)]
    [InlineData(null, null)]
    [InlineData("якийсь_сміттєвий", null)]
    public void Status_Maps_Semantics(string? raw, string? expected)
    {
        var t = MalOfficialMapper.Build(new MalNode { Id = 2, Title = "Y", Status = raw });
        Assert.Equal(expected, t.Status);
    }

    [Fact]
    public void Manga_Nodes_Map_Type_From_Media_Type()
    {
        var data = Parse(MangaPageJson).Data;

        var manga = MalOfficialMapper.Build(data[0], "manga");
        Assert.Equal("manga", manga.Type);
        Assert.Equal(2, manga.MalId);
        Assert.Equal("finished", manga.Status); // finished_publishing
        Assert.Equal(25.0, manga.Popularity, 6);
        Assert.Equal(new[] { "drama", "horror" }, manga.Genres);

        var manhwa = MalOfficialMapper.Build(data[1], "manga");
        Assert.Equal("manga", manhwa.Type);
        Assert.Equal("manhwa", manhwa.Format);
        Assert.Equal("airing", manhwa.Status); // currently_publishing

        var novel = MalOfficialMapper.Build(data[2], "manga");
        Assert.Equal("manga", novel.Type);     // light_novel → type manga
        Assert.Equal("novel", novel.Format);
    }

    // ─── SyncSkip: MAL official без MAL_CLIENT_ID — шар пропускається, не фейлить синк ───

    private static MalOfficialClient MakeClient(string clientId) => new(
        new HttpClient(),
        new MemoryCacheStore(),
        new MalOfficialThrottle(),
        Options.Create(new MalOfficialOptions { ClientId = clientId }),
        NullLogger<MalOfficialClient>.Instance);

    [Fact]
    public void No_Client_Id_Is_Not_Configured_And_SyncSkips()
    {
        var client = MakeClient("");
        Assert.False(client.IsConfigured);

        var skip = Assert.Throws<SyncSkipException>(() => client.EnsureConfigured());
        Assert.Contains("MAL_CLIENT_ID", skip.UserMessage);
        Assert.Contains("myanimelist.net/apis.php", skip.UserMessage);
    }

    [Fact]
    public async Task No_Client_Id_Season_And_Top_SyncSkip_Before_Any_Http()
    {
        var client = MakeClient("   ");
        await Assert.ThrowsAsync<SyncSkipException>(() => client.SeasonAsync(2026, "winter", 1));
        await Assert.ThrowsAsync<SyncSkipException>(() => client.TopAnimeAsync(1));
        await Assert.ThrowsAsync<SyncSkipException>(() => client.TopMangaAsync(1));
    }

    [Fact]
    public void With_Client_Id_Is_Configured()
    {
        var client = MakeClient("free-client-id");
        Assert.True(client.IsConfigured);
        client.EnsureConfigured(); // не кидає
    }

    // ─── URL-будування і валідація сезону ───

    [Fact]
    public void Season_Url_Builds_Path_Limit_And_Fields()
    {
        var url = MalOfficialClient.SeasonUrl(2026, "Winter");
        Assert.StartsWith("anime/season/2026/winter?", url);
        Assert.Contains("limit=100", url);
        Assert.Contains("fields=", url);
        Assert.Contains("alternative_titles{en,ja}", url);
        Assert.Contains("num_list_users", url);
        Assert.Contains("average_episode_duration", url);
        Assert.Contains("mean", url);
    }

    [Fact]
    public void Ranking_Urls_Build_For_Anime_And_Manga()
    {
        Assert.StartsWith("anime/ranking?ranking_type=all", MalOfficialClient.RankingUrl("anime"));
        Assert.StartsWith("manga/ranking?ranking_type=all", MalOfficialClient.RankingUrl("manga", "all"));
    }

    [Theory]
    [InlineData("winter")]
    [InlineData("spring")]
    [InlineData("summer")]
    [InlineData("fall")]
    public void Season_Url_Accepts_All_Four_Seasons(string season)
    {
        Assert.Contains($"anime/season/2025/{season}", MalOfficialClient.SeasonUrl(2025, season));
    }

    [Theory]
    [InlineData("autumn")]
    [InlineData("WINTERX")]
    [InlineData("")]
    [InlineData(null)]
    public void Season_Url_Rejects_Invalid_Season(string? season)
    {
        Assert.Throws<ArgumentException>(() => MalOfficialClient.SeasonUrl(2026, season!));
    }

    [Theory]
    [InlineData(1899)]
    [InlineData(3000)]
    [InlineData(0)]
    public void Season_Url_Rejects_Year_Out_Of_Range(int year)
    {
        Assert.Throws<ArgumentException>(() => MalOfficialClient.SeasonUrl(year, "winter"));
    }

    [Fact]
    public void NormalizeSeason_Trims_And_Lowercases()
    {
        Assert.Equal("winter", MalOfficialClient.NormalizeSeason(" Winter "));
        Assert.Null(MalOfficialClient.NormalizeSeason("autumn"));
        Assert.Null(MalOfficialClient.NormalizeSeason(null));
    }

    [Theory]
    [InlineData(1, "winter")]
    [InlineData(2, "winter")]
    [InlineData(3, "spring")]
    [InlineData(6, "summer")]
    [InlineData(9, "fall")]
    [InlineData(11, "fall")]
    [InlineData(12, "winter")]
    public void SeasonForMonth_Maps_Meteorological_Seasons(int month, string expected)
    {
        Assert.Equal(expected, MalOfficialClient.SeasonForMonth(month));
    }

    /// <summary>Пейджинг по next: перша сторінка з paging.next веде до другої (offset+limit), без HTTP — лише модель.</summary>
    [Fact]
    public void Paging_Next_Carries_Offset_For_Follow_Up_Page()
    {
        var page = Parse(SeasonPageJson);
        Assert.Contains("offset=100", page.Paging!.Next!);
    }

    /// <summary>Мінімальний in-memory ICacheStore для клієнтів без HTTP.</summary>
    private sealed class MemoryCacheStore : ICacheStore
    {
        public Dictionary<string, string> Store { get; } = new();

        public Task<string?> GetAsync(string source, string cacheKey, CancellationToken ct = default) =>
            Task.FromResult(Store.TryGetValue($"{source}:{cacheKey}", out var v) ? v : null);

        public Task SetAsync(string source, string cacheKey, string payload, int ttlSeconds, CancellationToken ct = default)
        {
            Store[$"{source}:{cacheKey}"] = payload;
            return Task.CompletedTask;
        }
    }
}
