using System.Text.Json;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Tests;

/// <summary>
/// Російські ТА українські назви з імпорту аніме/манги (політика власника 10.11):
/// Shikimori «russian» → title_ru (fill-only), AniList synonyms → uk/ru/en розподіл
/// (кирилиця з ї/і/є/ґ → uk, кирилиця без них → ru, латиниця → en). Скрізь fill-only:
/// непорожні існуючі назви не перезаписуються, серед кількох синонімів однієї мови
/// виграє перший відповідний.
/// </summary>
public class ImportNamesTests
{
    // ── Shikimori: russian → title_ru ──

    private const string ShikimoriRatesJson = """
        [
          { "id": 1, "target_id": 21649, "score": 9, "status": "completed",
            "anime": { "id": 21649, "name": "Mahou Shoujo Madoka Magica",
                       "russian": "Волшебница", "kind": "tv", "episodes": 12,
                       "aired_on": "2011-01-06", "genres": [ { "name": "Drama" } ] } },
          { "id": 2, "target_id": 20, "score": 7, "status": "completed",
            "anime": { "id": 20, "name": "Bleach", "kind": "tv" } }
        ]
        """;

    [Fact]
    public void Shikimori_Russian_Fills_TitleRu_Romaji_Fills_TitleEn()
    {
        var rates = JsonSerializer.Deserialize<List<ShikimoriRate>>(ShikimoriRatesJson, TmdbJson.Options)!;
        var t = ShikimoriMapper.BuildAnime(rates[0]);

        Assert.Equal("Волшебница", t.TitleRu);                  // російська назва → title_ru
        Assert.Equal("Mahou Shoujo Madoka Magica", t.TitleEn);  // ромадзі → title_en
        Assert.Null(t.TitleUk);                                 // russian НІКОЛИ не пише title_uk
    }

    [Fact]
    public void Shikimori_Without_Russian_TitleRu_Not_Touched()
    {
        var rates = JsonSerializer.Deserialize<List<ShikimoriRate>>(ShikimoriRatesJson, TmdbJson.Options)!;

        var fresh = ShikimoriMapper.BuildAnime(rates[1]); // Bleach без russian
        Assert.Null(fresh.TitleRu);

        // злиття з наявним тайтлом, що вже має ru-назву: патч без russian НЕ міняє її
        var target = new Title { Type = "anime", ShikimoriId = 20, TitleEn = "Bleach", TitleRu = "Блич" };
        AnimeUpsertCore.Merge(target, fresh);
        Assert.Equal("Блич", target.TitleRu);
    }

    [Fact]
    public void Shikimori_Russian_Is_FillOnly_Never_Overwrites()
    {
        var rates = JsonSerializer.Deserialize<List<ShikimoriRate>>(ShikimoriRatesJson, TmdbJson.Options)!;

        // Apply на існуючий тайтл: непорожня title_ru не перезаписується
        var existing = new Title { Type = "anime", ShikimoriId = 21649, TitleEn = "Madoka", TitleRu = "Пуелла Маги Мадока" };
        ShikimoriMapper.ApplyAnime(existing, rates[0]);
        Assert.Equal("Пуелла Маги Мадока", existing.TitleRu);

        // і на шляху Merge (Build → Resolve → Merge, як у ImportService)
        var target = new Title { Type = "anime", ShikimoriId = 21649, TitleEn = "Madoka", TitleRu = "Пуелла Маги Мадока" };
        AnimeUpsertCore.Merge(target, ShikimoriMapper.BuildAnime(rates[0]));
        Assert.Equal("Пуелла Маги Мадока", target.TitleRu);
    }

    [Fact]
    public void Merge_Carries_TitleRu_Into_Empty_Target()
    {
        var target = new Title { Type = "anime", ShikimoriId = 21649, TitleEn = "Madoka" }; // title_ru порожня
        AnimeUpsertCore.Merge(target, ShikimoriMapper.BuildAnime(
            JsonSerializer.Deserialize<List<ShikimoriRate>>(ShikimoriRatesJson, TmdbJson.Options)![0]));

        Assert.Equal("Волшебница", target.TitleRu); // патч зберіг title_ru через upsert
    }

    // ── AniList: synonyms → uk/ru/en ──

    private const string AniListSynonymsJson = """
        {
          "data": {
            "MediaListCollection": {
              "lists": [
                { "name": "Watching", "entries": [
                  { "status": "CURRENT", "score": 8,
                    "media": { "id": 113141,
                               "title": { "romaji": "Jujutsu Kaisen", "english": null },
                               "synonyms": ["Магическая битва", "Jujutsu Kaisen", "魔法呪術"],
                               "format": "TV", "episodes": 24,
                               "genres": [ "Action" ],
                               "startDate": { "year": 2020 },
                               "averageScore": 86 } }
                ] }
              ]
            }
          }
        }
        """;

    [Fact]
    public void AniList_Synonyms_Ru_Goes_To_TitleRu()
    {
        var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(AniListSynonymsJson, TmdbJson.Options)!;
        var t = AniListMapper.BuildAnime(resp.Data!.MediaListCollection!.Lists[0].Entries[0]);

        Assert.Equal("Магическая битва", t.TitleRu); // кирилиця без ї/і/є/ґ → title_ru
        Assert.Equal("Jujutsu Kaisen", t.TitleEn);   // ромадзі пріоритет: латиниця-синонім не перезаписує
        Assert.Null(t.TitleUk);                      // 魔法呪術 — ні кирилиця, ні латиниця → ігнор
    }

    [Fact]
    public void AniList_Ukrainian_Synonym_Goes_To_TitleUk()
    {
        // «Магічна битва» має українську «і» → uk; «Магическая битва» без неї → ru
        var media = new AniListMedia
        {
            Id = 113141,
            Title = new AniListTitle { Romaji = "Jujutsu Kaisen" },
            Synonyms = ["Магічна битва", "Магическая битва"],
        };
        var t = AniListMapper.BuildCatalogAnime(media);

        Assert.Equal("Магічна битва", t.TitleUk);
        Assert.Equal("Магическая битва", t.TitleRu);
        Assert.Equal("Jujutsu Kaisen", t.TitleEn);
    }

    [Fact]
    public void AniList_Synonyms_First_Per_Language_Wins()
    {
        var media = new AniListMedia
        {
            Id = 1,
            Title = new AniListTitle { Romaji = "Sakura" },
            Synonyms = ["Магічна битва", "Інша сакура", "Магическая битва", "Иная сакура"],
        };
        var t = AniListMapper.BuildCatalogAnime(media);

        Assert.Equal("Магічна битва", t.TitleUk);    // перший uk-синонім («Магічна» має «і»)
        Assert.Equal("Магическая битва", t.TitleRu); // перший ru-синонім («Магическая» без ї/і/є/ґ)
    }

    [Fact]
    public void AniList_Latin_Synonym_Fills_Only_Empty_TitleEn()
    {
        // ромадзі/english відсутні → латиниця-синонім заповнює title_en
        var media = new AniListMedia
        {
            Id = 2,
            Title = new AniListTitle(),
            Synonyms = ["Magical Battle"],
        };
        var t = AniListMapper.BuildCatalogAnime(media);
        Assert.Equal("Magical Battle", t.TitleEn);

        // але наявний title_en (навіть з іншого джерела) латиницею-синонімом не змінюється
        var existing = new Title { Type = "anime", AnilistId = 2, TitleEn = "Real Title" };
        AniListMapper.ApplyAnime(existing, new AniListEntry { Media = media });
        Assert.Equal("Real Title", existing.TitleEn);
    }

    [Fact]
    public void AniList_Synonyms_Are_FillOnly_Never_Overwrite()
    {
        var media = new AniListMedia
        {
            Id = 113141,
            Title = new AniListTitle { Romaji = "Jujutsu Kaisen" },
            Synonyms = ["Магическая битва", "Магічна битва"],
        };

        var existing = new Title
        {
            Type = "anime",
            AnilistId = 113141,
            TitleEn = "Jujutsu Kaisen",
            TitleRu = "Магия заклинаний",  // вже заповнено (Wikidata)
            TitleUk = "Магічна війна",     // вже заповнено
        };
        AniListMapper.ApplyAnime(existing, new AniListEntry { Media = media });

        Assert.Equal("Магия заклинаний", existing.TitleRu); // не затерто
        Assert.Equal("Магічна війна", existing.TitleUk);    // не затерто

        // і через Merge (каталог-синк / імпорт): fill-only у записі в БД
        var target = new Title { Type = "anime", AnilistId = 113141, TitleEn = "Jujutsu Kaisen", TitleRu = "Магия заклинаний" };
        AnimeUpsertCore.Merge(target, AniListMapper.BuildCatalogAnime(media));
        Assert.Equal("Магия заклинаний", target.TitleRu);
    }

    // ── GraphQL-запити мають просити synonyms ──

    [Fact]
    public void GraphQL_Queries_Request_Synonyms_Field()
    {
        Assert.Contains("synonyms", AniListClient.MediaListQuery);
        Assert.Contains("synonyms", AniListClient.CatalogQuery);
        // фолбек — страховка БЕЗ нового поля: якщо primary колись відхилено валідацією,
        // імпорт живе далі (просто без synonyms-назв)
        Assert.DoesNotContain("synonyms", AniListClient.MediaListQueryFallback);
    }
}
