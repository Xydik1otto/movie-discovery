using System.Text.Json;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Tests;

/// <summary>
/// Парсинг і мапінг Jikan v4 (top/anime, top/manga, user animelist/mangalist) —
/// зафіксовані JSON-фікстури, реальних HTTP-викликів нема.
/// </summary>
public class JikanMappingTests
{
    private const string TopAnimeJson = """
        {
          "pagination": { "last_visible_page": 1, "has_next_page": false, "items": { "count": 2, "total": 2, "per_page": 25 } },
          "data": [
            {
              "mal_id": 5114,
              "title": "Fullmetal Alchemist: Brotherhood",
              "title_english": "Fullmetal Alchemist: Brotherhood",
              "images": { "jpg": { "image_url": "https://cdn.myanimelist.net/images/anime/1208/94745.jpg",
                                   "large_image_url": "https://cdn.myanimelist.net/images/anime/1208/94745l.jpg" } },
              "type": "TV",
              "episodes": 64,
              "status": "Finished Airing",
              "score": 9.09,
              "members": 3500000,
              "favorites": 120000,
              "year": 2009,
              "aired": { "from": "2009-04-05T00:00:00+00:00", "to": "2010-07-04T00:00:00+00:00" },
              "synopsis": "Two brothers search for the Philosopher's Stone.",
              "genres": [ { "mal_id": 1, "name": "Action" }, { "mal_id": 8, "name": "Drama" }, { "mal_id": 62, "name": "Isekai" } ],
              "studios": [ { "mal_id": 14, "name": "Sunrise" } ]
            },
            {
              "mal_id": 9253,
              "title": "Steins;Gate",
              "title_english": null,
              "images": { "jpg": { "image_url": "https://cdn.myanimelist.net/images/anime/1935/127974.jpg" } },
              "type": "TV",
              "episodes": 24,
              "status": "Currently Airing",
              "score": 9.07,
              "members": 2600000,
              "year": 2011,
              "genres": [ { "name": "Drama" }, { "name": "Sci-Fi" } ],
              "studios": [ { "name": "White Fox" } ]
            }
          ]
        }
        """;

    private const string UserAnimeListJson = """
        {
          "pagination": { "last_visible_page": 1, "has_next_page": false, "items": { "count": 3, "total": 3, "per_page": 100 } },
          "data": [
            { "status": "completed", "score": 10,
              "anime": { "mal_id": 1, "title": "Cowboy Bebop", "title_english": "Cowboy Bebop", "type": "TV",
                         "episodes": 26, "score": 8.75, "members": 2000000, "year": 1998,
                         "genres": [ { "name": "Action" } ], "studios": [ { "name": "Sunrise" } ] } },
            { "status": "watching", "score": 0,
              "anime": { "mal_id": 30, "title": "Neon Genesis Evangelion", "type": "TV",
                         "episodes": 26, "score": 8.35, "members": 2300000, "year": 1995 } },
            { "status": "on_hold", "score": 7,
              "anime": { "mal_id": 20, "title": "Bleach", "type": "TV", "episodes": 366 } },
            { "status": "dropped", "score": 0, "anime": { "mal_id": 21, "title": "One Piece", "type": "TV" } }
          ]
        }
        """;

    private const string UserMangaListJson = """
        {
          "pagination": { "last_visible_page": 1, "has_next_page": false, "items": { "count": 3, "total": 3, "per_page": 100 } },
          "data": [
            { "status": "completed", "score": 9,
              "manga": { "mal_id": 13, "title": "One Piece", "type": "Manga",
                         "chapters": 1130, "volumes": 108, "score": 9.15, "members": 200000,
                         "published": { "from": "1997-07-22T00:00:00+00:00" },
                         "authors": [ { "name": "Oda, Eiichiro" } ],
                         "genres": [ { "name": "Adventure" }, { "name": "Comedy" } ] } },
            { "status": "reading", "score": 0,
              "manga": { "mal_id": 2, "title": "Berserk", "type": "Manga", "chapters": 374, "volumes": 42 } },
            { "status": "plan_to_read", "score": 0,
              "manga": { "mal_id": 1, "title": "Monster", "type": "Light Novel", "chapters": 18 } }
          ]
        }
        """;

    [Fact]
    public void TopAnime_Parses_And_Maps()
    {
        var page = JsonSerializer.Deserialize<JikanPage<JikanMedia>>(TopAnimeJson, TmdbJson.Options)!;
        Assert.Equal(2, page.Data.Count);

        var t = JikanAnimeMapper.Build(page.Data[0]);
        Assert.Equal("anime", t.Type);
        Assert.Equal(5114, t.MalId);
        Assert.Equal("Fullmetal Alchemist: Brotherhood", t.TitleEn); // ромадзі
        Assert.Equal("tv", t.Format);
        Assert.Equal(64, t.Episodes);
        Assert.Null(t.Chapters);
        Assert.Equal(new[] { "action", "drama", "isekai" }, t.Genres);
        Assert.Equal(new[] { "Sunrise" }, t.Studios);
        Assert.Equal(9.09, t.ScoreMal ?? 0, 6);
        Assert.Equal(350.0, t.Popularity, 6); // members/10000
        Assert.Equal(2009, t.Year);
        Assert.Equal("finished", t.Status);
        Assert.StartsWith("https://cdn.myanimelist.net/", t.PosterPath);

        // title_english відсутній → fallback на ромадзі; "Sci-Fi" → science fiction; airing-статус
        var t2 = JikanAnimeMapper.Build(page.Data[1]);
        Assert.Equal("Steins;Gate", t2.TitleEn);
        Assert.Contains("science fiction", t2.Genres);
        Assert.Equal("airing", t2.Status);
        Assert.Equal(260.0, t2.Popularity, 6);
    }

    [Fact]
    public void UserAnimeList_Parses_Entries()
    {
        var page = JsonSerializer.Deserialize<JikanPage<JikanUserEntry>>(UserAnimeListJson, TmdbJson.Options)!;
        Assert.Equal(4, page.Data.Count);

        var completed = page.Data[0];
        Assert.Equal("completed", completed.Status);
        Assert.Equal(10, completed.Score);
        Assert.NotNull(completed.Anime);

        var watching = page.Data[1];
        Assert.Equal("watching", watching.Status);
        Assert.Equal(0, watching.Score); // не оцінено

        Assert.Equal("on_hold", page.Data[2].Status);
        Assert.Equal("dropped", page.Data[3].Status);
    }

    [Fact]
    public void UserMangaList_Maps_Chapters_Volumes_Authors()
    {
        var page = JsonSerializer.Deserialize<JikanPage<JikanUserEntry>>(UserMangaListJson, TmdbJson.Options)!;

        var t = JikanMangaMapper.Build(page.Data[0].Manga!);
        Assert.Equal("manga", t.Type);
        Assert.Equal(13, t.MalId);
        Assert.Equal("One Piece", t.TitleEn);
        Assert.Equal("manga", t.Format);
        Assert.Equal(1130, t.Chapters);
        Assert.Equal(108, t.Volumes);
        Assert.Null(t.Episodes);
        Assert.Equal(new[] { "Oda, Eiichiro" }, t.Authors);
        Assert.Null(t.Studios);
        Assert.Equal(9.15, t.ScoreMal ?? 0, 6);
        Assert.Equal(20.0, t.Popularity, 6);
        Assert.Equal(1997, t.Year); // з published.from
        Assert.Contains("adventure", t.Genres);
        Assert.Contains("comedy", t.Genres);

        var lightNovel = JikanMangaMapper.Build(page.Data[2].Manga!);
        Assert.Equal("novel", lightNovel.Format);
    }

    [Fact]
    public void Mapper_Does_Not_Write_TitleUk() // у MAL/Jikan uk-назв нема — поле лишається порожнім
    {
        var page = JsonSerializer.Deserialize<JikanPage<JikanMedia>>(TopAnimeJson, TmdbJson.Options)!;
        var t = JikanAnimeMapper.Build(page.Data[0]);
        Assert.Null(t.TitleUk);
        Assert.Null(t.ScoreAnilist);
        Assert.Null(t.ScoreShikimori);
        Assert.Null(t.ShikimoriId);
        Assert.Null(t.AnilistId);
    }
}
