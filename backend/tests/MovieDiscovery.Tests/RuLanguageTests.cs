using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Api.Controllers;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// Зміна власника 10.11: російська — ПОВНОЦІННА мова відповідей API (uk|en|ru).
/// (a) SearchController.Lang — канонізація en|ru|uk (кейс-незалежно, інше → uk);
/// (b) TitleMapper.ToCard у ru-режимі віддає title_ru;
/// (c) порожній title_ru → фолбек ru → uk → en (не ламає uk-ланцюг uk → ru → en);
/// (d) живий GET /api/search?lang=ru через ApiTestFactory — назва з title_ru фікстури
/// (фікстура сіється у ВЛАСНОМУ тесті через db-скоуп, ApiTestFactory.SeedAsync не змінено).
/// </summary>
public class RuLanguageTests
{
    // ── (a) SearchController.Lang ──
    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("RU", "ru")]
    [InlineData("Ru", "ru")]
    [InlineData(null, "uk")]
    [InlineData("", "uk")]
    [InlineData("de", "uk")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    public void Lang_Canonicalizes_To_En_Ru_uk(string? lang, string expected) =>
        Assert.Equal(expected, SearchController.Lang(lang));

    // ── (b) ru-режим віддає title_ru / overview_ru ──
    [Fact]
    public void ToCard_Ru_Returns_TitleRu_And_OverviewRu()
    {
        var t = new Title
        {
            TitleEn = "Lucifer",
            TitleUk = "Люцифер (укр)",
            TitleRu = "Люцифер",
            OverviewEn = "English overview",
            OverviewUk = "Український опис",
            OverviewRu = "Російський опис",
        };
        var card = TitleMapper.ToCard(t, "ru");
        Assert.Equal("Люцифер", card.Title);
        Assert.Equal("Російський опис", card.Overview);
        Assert.Equal("Lucifer", card.OriginalTitle); // оригінал не змінюється
    }

    // ── (c) порожній title_ru → той самий фолбек, що й зараз: uk → en ──
    [Fact]
    public void ToCard_Ru_Empty_TitleRu_Falls_Back_Uk_Then_En()
    {
        var emptyRu = new Title { TitleEn = "Lucifer", TitleUk = "Люцифер (укр)", TitleRu = "   " };
        Assert.Equal("Люцифер (укр)", TitleMapper.ToCard(emptyRu, "ru").Title);

        var noRuNoUk = new Title { TitleEn = "Lucifer" };
        Assert.Equal("Lucifer", TitleMapper.ToCard(noRuNoUk, "ru").Title);

        // Опис: порожній overview_ru → overview_uk → overview_en
        var o = new Title { TitleEn = "Lucifer", OverviewEn = "English", OverviewUk = "Укр опис", OverviewRu = "" };
        Assert.Equal("Укр опис", TitleMapper.ToCard(o, "ru").Overview);
        Assert.Equal("English", TitleMapper.ToCard(new Title { TitleEn = "X", OverviewEn = "English" }, "ru").Overview);
    }

    // ── Регресія: uk-ланцюг uk → ru → en НЕ зламаний (WikidataNamesTests покриває, тут — дубль-страж) ──
    [Fact]
    public void ToCard_Uk_Chain_Unchanged()
    {
        var t = new Title { TitleEn = "Lucifer", TitleRu = "Люцифер", TitleUk = "Люцифер (укр)" };
        Assert.Equal("Люцифер (укр)", TitleMapper.ToCard(t, "uk").Title);
        Assert.Equal("Люцифер (укр)", TitleMapper.ToCard(t, null!).Title); // null → uk-режим → title_uk
        var noUk = new Title { TitleEn = "Lucifer", TitleRu = "Люцифер" };
        Assert.Equal("Люцифер", TitleMapper.ToCard(noUk, "uk").Title);
        Assert.Equal("Lucifer", TitleMapper.ToCard(t, "en").Title); // en-режим не локалізується
        Assert.Equal("English", TitleMapper.ToCard(new Title { TitleEn = "X", OverviewEn = "English", OverviewUk = "Укр" }, "en").Overview);
    }
}

/// <summary>(d) Живий ендпоінт: /api/search?lang=ru повертає назву з title_ru фікстури.</summary>
public class RuLanguageApiTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;

    public RuLanguageApiTests(ApiTestFactory f) => _f = f;

    [Fact]
    public async Task Search_Lang_Ru_Returns_TitleRu_From_Fixture()
    {
        // Фікстура У ВЛАСНОМУ тесті (db-скоуп; ApiTestFactory.SeedAsync не міняємо).
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Titles.Add(new Title
            {
                Id = 777001,
                Type = "movie",
                TitleEn = "Zorba the Greek",
                TitleRu = "Грек Зорба",
                TitleUk = "Грек Зорба (укр)",
                OverviewEn = "English overview",
                OverviewRu = "Російський опис Зорби",
                Year = 1964,
                Genres = ["drama"],
                Popularity = 9999, // топ видачі без q (ILIKE на InMemory не перекладається)
            });
            await db.SaveChangesAsync();
        }

        var client = _f.CreateDefaultClient();
        var r = await client.GetAsync("/api/search?lang=ru&sort=popularity&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items");
        var mine = items.EnumerateArray().First(i => i.GetProperty("id").GetInt32() == 777001);
        Assert.Equal("Грек Зорба", mine.GetProperty("title").GetString());
        Assert.Equal("Російський опис Зорби", mine.GetProperty("overview").GetString());
    }

    /// <summary>Контроль: той самий тайтл у uk-режимі лишається uk-назвою (не зламати існуючий фолбек).</summary>
    [Fact]
    public async Task Search_Lang_Uk_Still_Returns_TitleUk()
    {
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Titles.Add(new Title
            {
                Id = 777002,
                Type = "movie",
                TitleEn = "Zorba the Greek Two",
                TitleRu = "Грек Зорба 2",
                TitleUk = "Грек Зорба (укр) 2",
                Year = 1965,
                Genres = ["drama"],
                Popularity = 9998,
            });
            await db.SaveChangesAsync();
        }

        var client = _f.CreateDefaultClient();
        var r = await client.GetAsync("/api/search?lang=uk&sort=popularity&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var mine = body.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetInt32() == 777002);
        Assert.Equal("Грек Зорба (укр) 2", mine.GetProperty("title").GetString());
    }
}
