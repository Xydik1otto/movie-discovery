using System.Text.Json;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Tests;

/// <summary>
/// Парсинг GraphQL-відповідей AniList + дедуп списків. score POINT_10 у користувачів
/// POINT_100 приходить 0–100 → rating 100 → 10 (кламп через /10).
/// </summary>
public class AniListMappingTests
{
    private const string UserQueryResponseJson = """
        { "data": { "User": { "id": 460802 } } }
        """;

    private const string MediaListResponseJson = """
        {
          "data": {
            "MediaListCollection": {
              "lists": [
                { "name": "Watching", "entries": [
                  { "status": "CURRENT", "score": 0,
                    "media": { "id": 16498,
                               "title": { "romaji": "Shingeki no Kyojin", "english": "Attack on Titan", "ukrainian": null },
                               "coverImage": { "large": "https://s4.anilist.co/file/anilistcdn/media/anime/large/16498.jpg" },
                               "format": "TV", "episodes": 25,
                               "genres": [ "Action", "Drama" ],
                               "studios": { "nodes": [ { "name": "Wit Studio" } ] },
                               "startDate": { "year": 2013 },
                               "averageScore": 85 } },
                  { "status": "CURRENT", "score": 0,
                    "media": { "id": 16498,
                               "title": { "romaji": "Shingeki no Kyojin", "english": "Attack on Titan", "ukrainian": null },
                               "format": "TV", "episodes": 25,
                               "genres": [ "Action" ],
                               "studios": { "nodes": [ { "name": "Wit Studio" } ] },
                               "startDate": { "year": 2013 },
                               "averageScore": 85 } }
                ] },
                { "name": "Completed", "entries": [
                  { "status": "COMPLETED", "score": 10,
                    "media": { "id": 5114,
                               "title": { "romaji": "Hagane no Renkinjutsushi: Fullmetal Alchemist",
                                          "english": "Fullmetal Alchemist: Brotherhood",
                                          "ukrainian": "Сталевий алхімік: Братство" },
                               "coverImage": { "large": "https://s4.anilist.co/file/anilistcdn/media/anime/large/5114.jpg" },
                               "format": "TV", "episodes": 64,
                               "genres": [ "Action", "Adventure", "Drama", "Fantasy" ],
                               "studios": { "nodes": [ { "name": "Bones" } ] },
                               "startDate": { "year": 2009 },
                               "averageScore": 91 } },
                  { "status": "COMPLETED", "score": 100,
                    "media": { "id": 30, "title": { "romaji": "Neon Genesis Evangelion", "english": "Neon Genesis Evangelion", "ukrainian": null },
                               "format": "TV", "episodes": 26,
                               "genres": [ "Mecha", "Psychological" ],
                               "studios": { "nodes": [ { "name": "Gainax" } ] },
                               "startDate": { "year": 1995 },
                               "averageScore": 86 } }
                ] },
                { "name": "Dropped", "entries": [
                  { "status": "DROPPED", "score": 3,
                    "media": { "id": 20, "title": { "romaji": "Bleach", "english": "Bleach", "ukrainian": null },
                               "format": "TV", "episodes": 366,
                               "genres": [ "Action" ],
                               "studios": { "nodes": [ { "name": "Studio Pierrot" } ] },
                               "startDate": { "year": 2004 },
                               "averageScore": 75 } }
                ] },
                { "name": "Manga Completed", "entries": [
                  { "status": "COMPLETED", "score": 9,
                    "media": { "id": 30002, "title": { "romaji": "Berserk", "english": "Berserk", "ukrainian": null },
                               "format": "MANGA", "chapters": 374, "volumes": 42,
                               "genres": [ "Drama", "Horror" ],
                               "studios": { "nodes": [] },
                               "startDate": { "year": 1989 },
                               "averageScore": 94 } }
                ] },
                { "name": "Кастомний список", "entries": [
                  { "status": "COMPLETED", "score": 9,
                    "media": { "id": 5114,
                               "title": { "romaji": "Hagane no Renkinjutsushi: Fullmetal Alchemist", "english": "Fullmetal Alchemist: Brotherhood", "ukrainian": null },
                               "format": "TV", "episodes": 64,
                               "genres": [ "Action" ],
                               "studios": { "nodes": [] },
                               "startDate": { "year": 2009 },
                               "averageScore": 91 } }
                ] }
              ]
            }
          }
        }
        """;

    [Fact]
    public void User_Query_Parses_Id()
    {
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListUserData>>(UserQueryResponseJson, TmdbJson.Options)!;
        Assert.Equal(460802, resp.Data!.User!.Id);
    }

    [Fact]
    public void MediaList_Parses_And_Maps_With_Real_Ukrainian_Title()
    {
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(MediaListResponseJson, TmdbJson.Options)!;
        var collection = resp.Data!.MediaListCollection!;
        Assert.Equal(5, collection.Lists.Count);

        var fma = AniListMapper.BuildAnime(collection.Lists[1].Entries[0]);
        Assert.Equal("anime", fma.Type);
        Assert.Equal(5114, fma.AnilistId);
        // title_en — ромадзі (пріоритет), а справжня uk-назва → title_uk
        Assert.Equal("Hagane no Renkinjutsushi: Fullmetal Alchemist", fma.TitleEn);
        Assert.Equal("Сталевий алхімік: Братство", fma.TitleUk);
        Assert.Equal("tv", fma.Format);
        Assert.Equal(64, fma.Episodes);
        Assert.Equal(new[] { "action", "adventure", "drama", "fantasy" }, fma.Genres);
        Assert.Equal(new[] { "Bones" }, fma.Studios);
        Assert.Equal(2009, fma.Year);
        Assert.Equal(9.1, fma.ScoreAnilist ?? 0, 6); // averageScore 91 → 9.1
        Assert.StartsWith("https://s4.anilist.co/", fma.PosterPath);
    }

    [Fact]
    public void Score_100_Becomes_Rating_10()
    {
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(MediaListResponseJson, TmdbJson.Options)!;
        var eva = resp.Data!.MediaListCollection!.Lists[1].Entries[1];

        Assert.Equal(100, eva.Score); // POINT_100-користувач
        var rating = ListKinds.RatingFromScore(eva.Score);
        Assert.Equal(10, rating); // >10 → /10, кламп 1..10
    }

    [Fact]
    public void Manga_Media_Maps_Chapters_Volumes()
    {
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(MediaListResponseJson, TmdbJson.Options)!;
        var mangaList = resp.Data!.MediaListCollection!.Lists[3];

        var t = AniListMapper.BuildManga(mangaList.Entries[0]);
        Assert.Equal("manga", t.Type);
        Assert.Equal(30002, t.AnilistId);
        Assert.Equal("manga", t.Format);
        Assert.Equal(374, t.Chapters);
        Assert.Equal(42, t.Volumes);
        Assert.Equal(new[] { "drama", "horror" }, t.Genres);
        Assert.Equal(9.4, t.ScoreAnilist ?? 0, 6);
    }

    [Fact]
    public void Flatten_Dedupes_Across_Custom_Lists()
    {
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(MediaListResponseJson, TmdbJson.Options)!;
        var collection = resp.Data!.MediaListCollection!;

        // Watching(1) + Completed(2) + Dropped(1) + Manga(1) + Custom(дубль 5114) = 6 entry-ів, 5 унікальних
        var anime = AniListFlatten.Flatten(collection);
        Assert.Equal(5, anime.Count);
        Assert.Equal(1, anime.Count(e => e.Media!.Id == 5114));
    }

    // --- Опис (description): HTML → OverviewEn ---

    private const string MediaListWithDescriptionJson = """
        {
          "data": {
            "MediaListCollection": {
              "lists": [
                { "name": "Watching", "entries": [
                  { "status": "CURRENT", "score": 0,
                    "media": { "id": 16498,
                               "title": { "romaji": "Shingeki no Kyojin", "english": "Attack on Titan", "ukrainian": null },
                               "description": "Humanity lived behind giant walls.<br><br><i>Attack on Titan</i> &amp; <b>heroes</b> — it&#039;s their story.<br />Read more at <a href=\"https://anilist.co\">AniList</a>.",
                               "format": "TV", "episodes": 25,
                               "genres": [ "Action" ],
                               "startDate": { "year": 2013 },
                               "averageScore": 85 } }
                ] }
              ]
            }
          }
        }
        """;

    private const string CatalogPageWithDescriptionJson = """
        {
          "data": {
            "Page": {
              "media": [
                { "id": 30002,
                  "title": { "romaji": "Berserk", "english": null },
                  "description": "Guts, a lone mercenary...<br><i>Source: manga</i>",
                  "format": "MANGA", "chapters": 374, "volumes": 42,
                  "genres": [ "Drama", "Horror" ],
                  "studios": { "nodes": [] },
                  "startDate": { "year": 1989 },
                  "averageScore": 94 }
              ]
            }
          }
        }
        """;

    [Fact]
    public void CleanDescription_Br_Becomes_Newline_Tags_Stripped_Entities_Decoded()
    {
        Assert.Equal("Рядок один\nРядок два", AniListMapper.CleanDescription("Рядок один<br>Рядок два"));
        Assert.Equal("Рядок один\nРядок два", AniListMapper.CleanDescription("Рядок один<br/>Рядок два"));
        Assert.Equal("Рядок один\nРядок два", AniListMapper.CleanDescription("Рядок один<br />Рядок два"));
        Assert.Equal("Тільки текст", AniListMapper.CleanDescription("<i>Тільки</i> <b>текст</b>"));
        Assert.Equal("AniList", AniListMapper.CleanDescription("<a href=\"https://anilist.co\">AniList</a>"));
        Assert.Equal("Кур'єр & мета", AniListMapper.CleanDescription("Кур&#039;єр &amp; мета"));
        // CR: <br> між абзацами + \n джерела → максимум один порожній рядок
        Assert.Equal("А\n\nБ", AniListMapper.CleanDescription("А<br><br>\n<br>Б"));
    }

    [Fact]
    public void CleanDescription_Null_Or_Blank_Returns_Null()
    {
        Assert.Null(AniListMapper.CleanDescription(null));
        Assert.Null(AniListMapper.CleanDescription(""));
        Assert.Null(AniListMapper.CleanDescription("   \n  "));
        Assert.Null(AniListMapper.CleanDescription("<br><br>")); // опис лише з тегів → порожньо → null
    }

    [Fact]
    public void Description_Maps_To_OverviewEn_OverviewUk_Untouched()
    {
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(MediaListWithDescriptionJson, TmdbJson.Options)!;
        var t = AniListMapper.BuildAnime(resp.Data!.MediaListCollection!.Lists[0].Entries[0]);

        Assert.Equal("Humanity lived behind giant walls.\n\nAttack on Titan & heroes — it's their story.\nRead more at AniList.",
            t.OverviewEn);
        Assert.DoesNotContain("<", t.OverviewEn!); // тегів у тексті не лишилось
        // uk-описів AniList не має — OverviewUk лишається порожнім (en-фолбек робить TitleMapper.ToCard)
        Assert.Null(t.OverviewUk);
    }

    [Fact]
    public void Catalog_Description_Maps_To_OverviewEn()
    {
        var media = JsonSerializer.Deserialize<AniListResponse<AniListPageData>>(CatalogPageWithDescriptionJson, TmdbJson.Options)!
            .Data!.Page!.Media;
        var t = AniListMapper.BuildCatalogManga(media[0]);

        Assert.Equal("Guts, a lone mercenary...\nSource: manga", t.OverviewEn);
        Assert.Null(t.OverviewUk);
    }

    [Fact]
    public void Missing_Or_Empty_Description_Leaves_OverviewEn_Null()
    {
        // старий формат відповіді без description → OverviewEn не пишемо (не порожній рядок)
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(MediaListResponseJson, TmdbJson.Options)!;
        var t = AniListMapper.BuildAnime(resp.Data!.MediaListCollection!.Lists[1].Entries[0]);
        Assert.Null(t.OverviewEn);

        // порожній рядок у відповіді → null (Merge не перезапише наявний опис порожнечею)
        Assert.Null(AniListMapper.CleanDescription("   "));
    }

    [Fact]
    public void GraphQL_Queries_Request_Description_Field()
    {
        // description має бути в УСІХ запитах (primary + fallback + каталог) — інакше поле не приїде
        Assert.Contains("description", AniListClient.MediaListQuery);
        Assert.Contains("description", AniListClient.MediaListQueryFallback);
        Assert.Contains("description", AniListClient.CatalogQuery);
    }
}
