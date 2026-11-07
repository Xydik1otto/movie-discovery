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
}
