using System.Text.Json;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Tests;

/// <summary>
/// Парсинг rates Shikimori + мапінг. КЛЮЧОВИЙ кейс: поле «russian» НІКОЛИ не потрапляє
/// у title_uk (свідоме рішення власника — російської мови немає ніде).
/// </summary>
public class ShikimoriMappingTests
{
    private const string AnimeRatesJson = """
        [
          { "id": 1001, "target_id": 5114, "score": 10, "status": "completed", "episodes": 64,
            "anime": { "id": 5114, "name": "Hagane no Renkinjutsushi: Fullmetal Alchemist",
                       "russian": "Стальной алхимик: Братство",
                       "image": "/system/animes/original/5114.jpg",
                       "kind": "tv", "score": 9.1, "status": "released", "episodes": 64,
                       "aired_on": "2009-04-05",
                       "genres": [ { "id": 1, "name": "Action", "russian": "Экшен" },
                                   { "id": 8, "name": "Drama", "russian": "Драма" } ],
                       "studios": [ { "name": "Bones" } ] } },
          { "id": 1002, "target_id": 16498, "score": 8, "status": "watching",
            "anime": { "id": 16498, "name": "Shingeki no Kyojin", "russian": "Атака титанов",
                       "image": { "original": "/system/animes/original/16498.jpg" },
                       "kind": "tv", "episodes": 25, "aired_on": "2013-04-07",
                       "genres": [ { "name": "Action" } ] } },
          { "id": 1003, "target_id": 20, "score": 0, "status": "dropped",
            "anime": { "id": 20, "name": "Bleach", "russian": "Блич", "kind": "tv" } },
          { "id": 1004, "target_id": 1, "score": 9, "status": "completed",
            "manga": { "id": 1, "name": "Berserk", "russian": "Берсерк", "kind": "manga",
                       "chapters": 374, "volumes": 42, "score": 9.4, "status": "released",
                       "aired_on": "1989-08-25", "genres": [ { "name": "Drama", "russian": "Драма" } ] } }
        ]
        """;

    private const string UserResolveJson = """{ "id": 12345, "nickname": "someuser", "name": "Some User" }""";

    [Fact]
    public void AnimeRates_Parse()
    {
        var rates = JsonSerializer.Deserialize<List<ShikimoriRate>>(AnimeRatesJson, TmdbJson.Options)!;
        Assert.Equal(4, rates.Count);
        Assert.Equal("completed", rates[0].Status);
        Assert.Equal(10, rates[0].Score);
        Assert.Equal(5114, rates[0].Anime!.Id);
        Assert.Equal("manga", rates[3].Manga!.Kind);
    }

    [Fact]
    public void Russian_Never_Goes_Into_TitleUk()
    {
        var rates = JsonSerializer.Deserialize<List<ShikimoriRate>>(AnimeRatesJson, TmdbJson.Options)!;

        var t = ShikimoriMapper.BuildAnime(rates[0]);
        Assert.Null(t.TitleUk); // «Стальной алхимик: Братство» НЕ потрапило в title_uk
        Assert.Equal("Hagane no Renkinjutsushi: Fullmetal Alchemist", t.TitleEn); // ромадзі
        Assert.DoesNotContain("Стальной", t.TitleEn ?? "");
        Assert.Equal(5114, t.ShikimoriId);
        Assert.Equal("tv", t.Format);
        Assert.Equal(64, t.Episodes);
        Assert.Equal(2009, t.Year);
        Assert.Equal(9.1, t.ScoreShikimori ?? 0, 6);
        Assert.Equal(new[] { "action", "drama" }, t.Genres);
        Assert.Equal(new[] { "Bones" }, t.Studios);
        Assert.Equal("finished", t.Status);
        Assert.Equal("https://shikimori.one/system/animes/original/5114.jpg", t.PosterPath);
    }

    [Fact]
    public void Image_Object_Form_Also_Works()
    {
        var rates = JsonSerializer.Deserialize<List<ShikimoriRate>>(AnimeRatesJson, TmdbJson.Options)!;
        var t = ShikimoriMapper.BuildAnime(rates[1]); // image — об'єкт {original}
        Assert.Equal("https://shikimori.one/system/animes/original/16498.jpg", t.PosterPath);
        Assert.Equal("watching", rates[1].Status);
        // watching-запис — валідний патч, але kind вирішує ImportService (watchlist)
        Assert.Equal("Shingeki no Kyojin", t.TitleEn);
    }

    [Fact]
    public void Manga_Rate_Maps_Chapters_Volumes()
    {
        var rates = JsonSerializer.Deserialize<List<ShikimoriRate>>(AnimeRatesJson, TmdbJson.Options)!;
        var t = ShikimoriMapper.BuildManga(rates[3]);
        Assert.Equal("manga", t.Type);
        Assert.Equal(1, t.ShikimoriId);
        Assert.Equal("Berserk", t.TitleEn);
        Assert.Null(t.TitleUk); // «Берсерк» — неuk
        Assert.Equal("manga", t.Format);
        Assert.Equal(374, t.Chapters);
        Assert.Equal(42, t.Volumes);
        Assert.Equal(1989, t.Year);
        Assert.Equal(["drama"], t.Genres);
    }

    [Fact]
    public void User_Resolve_Parses()
    {
        var user = JsonSerializer.Deserialize<ShikimoriUser>(UserResolveJson, TmdbJson.Options)!;
        Assert.Equal(12345, user.Id);
        Assert.Equal("someuser", user.Nickname);
    }
}
