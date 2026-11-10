using System.Text.Json;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Tests;

/// <summary>
/// Каталог-sync AniList: Page(perPage:50) sort=POPULARITY_DESC, meanScore → score_anilist (/10),
/// favourites → popularity (/10000). Фікстури без HTTP; title.ukrainian у запитах бути НЕ може.
/// </summary>
public class AniListCatalogTests
{
    private const string CatalogPageJson = """
        {
          "data": {
            "Page": {
              "media": [
                { "id": 16498,
                  "title": { "romaji": "Shingeki no Kyojin", "english": "Attack on Titan" },
                  "coverImage": { "large": "https://s4.anilist.co/file/anilistcdn/media/anime/large/16498.jpg" },
                  "format": "TV", "episodes": 25,
                  "genres": [ "Action", "Drama", "Suspense" ],
                  "studios": { "nodes": [ { "name": "Wit Studio" }, { "name": "MAPPA" } ] },
                  "startDate": { "year": 2013 },
                  "averageScore": 85,
                  "favourites": 123456,
                  "meanScore": 88 },
                { "id": 30002,
                  "title": { "romaji": "Berserk", "english": null },
                  "coverImage": { "large": "https://s4.anilist.co/file/anilistcdn/media/anime/large/30002.jpg" },
                  "format": "MANGA", "chapters": 374, "volumes": 42,
                  "genres": [ "Drama", "Horror" ],
                  "studios": { "nodes": [] },
                  "startDate": { "year": 1989 },
                  "averageScore": 94,
                  "favourites": 50000,
                  "meanScore": 91 },
                { "id": 20,
                  "title": { "romaji": "Bleach", "english": "Bleach" },
                  "format": "TV", "episodes": 366,
                  "genres": [ "Action" ],
                  "studios": { "nodes": [ { "name": "Studio Pierrot" } ] },
                  "startDate": { "year": 2004 },
                  "averageScore": 75,
                  "favourites": null,
                  "meanScore": null }
              ]
            }
          }
        }
        """;

    private static List<AniListMedia> ParseMedia() =>
        JsonSerializer.Deserialize<AniListResponse<AniListPageData>>(CatalogPageJson, TmdbJson.Options)!
            .Data!.Page!.Media;

    [Fact]
    public void Catalog_Anime_Maps_MeanScore_Studios_And_Favourites_Popularity()
    {
        var t = AniListMapper.BuildCatalogAnime(ParseMedia()[0]);

        Assert.Equal("anime", t.Type);
        Assert.Equal(16498, t.AnilistId);
        Assert.Equal("Shingeki no Kyojin", t.TitleEn); // ромадзі — пріоритет
        Assert.Null(t.TitleUk);                        // uk-назв у каталог-запиті нема (поля не існує)
        Assert.Equal("tv", t.Format);
        Assert.Equal(25, t.Episodes);
        Assert.Equal(new[] { "action", "drama", "thriller" }, t.Genres); // Suspense → thriller
        Assert.Equal(new[] { "Wit Studio", "MAPPA" }, t.Studios);
        Assert.Equal(2013, t.Year);
        Assert.Equal(8.8, t.ScoreAnilist ?? 0, 6);  // meanScore 88 → 8.8 (пріоритет над averageScore 85)
        Assert.Equal(12.3456, t.Popularity, 6);     // favourites 123456 → /10000
        Assert.StartsWith("https://s4.anilist.co/", t.PosterPath);
    }

    [Fact]
    public void Catalog_Manga_Maps_Chapters_Volumes_And_Skips_Studios()
    {
        var t = AniListMapper.BuildCatalogManga(ParseMedia()[1]);

        Assert.Equal("manga", t.Type);
        Assert.Equal(30002, t.AnilistId);
        Assert.Equal("Berserk", t.TitleEn);
        Assert.Equal("manga", t.Format);
        Assert.Equal(374, t.Chapters);
        Assert.Equal(42, t.Volumes);
        Assert.Null(t.Episodes);
        Assert.Null(t.Studios); // студії — тільки для аніме
        Assert.Equal(9.1, t.ScoreAnilist ?? 0, 6); // meanScore 91 → 9.1
        Assert.Equal(5.0, t.Popularity, 6);        // favourites 50000 → 5.0
    }

    [Fact]
    public void MeanScore_Missing_Falls_Back_To_AverageScore()
    {
        var t = AniListMapper.BuildCatalogAnime(ParseMedia()[2]);
        Assert.Equal(7.5, t.ScoreAnilist ?? 0, 6); // averageScore 75
        Assert.Equal(0, t.Popularity);             // popularity/favourites відсутні → не чіпаємо (0 у нового тайтла)
    }

    [Fact]
    public void CatalogQuery_Contract_Popularity_Desc_No_Ukrainian()
    {
        Assert.Contains("POPULARITY_DESC", AniListClient.CatalogQuery);
        Assert.Contains("perPage", AniListClient.CatalogQuery);
        Assert.Contains("meanScore", AniListClient.CatalogQuery);
        Assert.Contains("favourites", AniListClient.CatalogQuery);
        Assert.DoesNotContain("ukrainian", AniListClient.CatalogQuery);

        // ФІКС імпорту: title.ukrainian в API не існує → з первинного запиту прибрано (fallback лишається)
        Assert.DoesNotContain("ukrainian", AniListClient.MediaListQuery);
        Assert.DoesNotContain("ukrainian", AniListClient.MediaListQueryFallback);
    }

    [Fact]
    public void Import_Media_Without_MeanScore_Keeps_AverageScore_Semantics()
    {
        // імпортний фіксат: лише averageScore → /10 (регрес-страж змін meanScore-пріоритету)
        var json = """
            { "data": { "MediaListCollection": { "lists": [ { "name": "Watching", "entries": [
                { "status": "CURRENT", "score": 0,
                  "media": { "id": 1535, "title": { "romaji": "Death Note" }, "format": "TV",
                             "startDate": { "year": 2006 }, "averageScore": 88 } }
            ] } ] } } }
            """;
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(json, TmdbJson.Options)!;
        var t = AniListMapper.BuildAnime(resp.Data!.MediaListCollection!.Lists[0].Entries[0]);
        Assert.Equal(8.8, t.ScoreAnilist ?? 0, 6);
    }
}
