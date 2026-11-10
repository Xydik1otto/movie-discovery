using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MovieDiscovery.Infrastructure.Persistence;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// Переписана система еталонів «Схоже на...» (10.11): еталони ОПЦІЙНІ — запит валідний
/// з 0 еталонів, коли є жанри або настрій (пресет MoodPresets). Настрій — м'який жанровий
/// вхід (вага MoodGenreWeight у жанровому сигналі агрегата + soft-boost). Невідомий ключ
/// настрою → 400 invalid_request з переліком ключів. Жанри/настрій не вимагають сідів.
/// </summary>
public sealed class SimilarMoodTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;
    public SimilarMoodTests(ApiTestFactory f) => _f = f;

    // Alpha.Id == Beta.TmdbId == 7001 (та сама колізійна пара, що в SimilarNamespaceTests).
    // Delta (mystery) — кандидат під tense=[thriller,crime,mystery]; Echo (comedy) — під
    // cozy=[comedy,family,romance,animation]; Foxtrot (mystery, series) — для hard-фільтра типів.
    private const int AlphaId = 7001;
    private const int DeltaId = 7004;
    private const int EchoId = 7005;
    private const int FoxtrotId = 7006;

    private async Task<HttpClient> ClientAsync()
    {
        var c = await _f.GuestClientAsync(); // гарантований одноразовий базовий сід фабрики
        await EnsureSeedAsync();
        return c;
    }

    private async Task EnsureSeedAsync()
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        if (db.Titles.Any(t => t.Id == AlphaId)) return; // ідемпотентно (xunit: новий інстанс на тест)
        db.Titles.AddRange(
            new Core.Domain.Title
            {
                Id = AlphaId, Type = "movie", TitleEn = "Alpha", Year = 2001,
                Genres = ["drama"], Popularity = 10,
            },
            new Core.Domain.Title
            {
                Id = 7002, Type = "movie", TitleEn = "Beta", TmdbId = AlphaId, Year = 1995,
                Genres = ["horror"], Popularity = 10,
            },
            new Core.Domain.Title
            {
                Id = 7003, Type = "movie", TitleEn = "Gamma", Year = 1995,
                Genres = ["horror"], Popularity = 5,
            },
            // Delta: mystery ∈ tense — єдиний тайтл сиду, що перетинається з пресетом tense.
            new Core.Domain.Title
            {
                Id = DeltaId, Type = "movie", TitleEn = "Delta", Year = 1996,
                Genres = ["mystery"], Popularity = 8,
            },
            // Echo: comedy ∈ cozy — кандидат для перевірки злиття настрою з еталонами.
            new Core.Domain.Title
            {
                Id = EchoId, Type = "movie", TitleEn = "Echo", Year = 1998,
                Genres = ["comedy"], Popularity = 6,
            },
            // Foxtrot: mystery, але series — hard-фільтр types=movie мусить його відсікти.
            new Core.Domain.Title
            {
                Id = FoxtrotId, Type = "series", TitleEn = "Foxtrot", Year = 1997,
                Genres = ["mystery"], Popularity = 7,
            });
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ItemsAsync(HttpResponseMessage r)
    {
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("items");
    }

    private static bool HasId(JsonElement items, int id)
    {
        foreach (var it in items.EnumerateArray())
            if (it.GetProperty("id").GetInt32() == id) return true;
        return false;
    }

    [Fact]
    public async Task Mood_Only_No_Seeds_Returns_Items()
    {
        // 0 еталонів + настрій — самодостатній запит; tense=[thriller,crime,mystery],
        // тому єдиний очікуваний кандидат сиду — Delta (mystery). Beta/Gamma horror у tense не входять.
        var c = await ClientAsync();
        var r = await c.GetAsync("/api/similar/by-seeds?ids=&mood=tense");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = await ItemsAsync(r);
        Assert.True(items.GetArrayLength() > 0);
        Assert.True(HasId(items, DeltaId), "mood=tense має підтягнути Delta (mystery) у кандидати");
    }

    [Fact]
    public async Task Genres_Only_No_Seeds_Returns_Items()
    {
        var c = await ClientAsync();
        var r = await c.GetAsync("/api/similar/by-seeds?ids=&genres=horror");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = await ItemsAsync(r);
        Assert.True(items.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Mood_And_Genres_Combine()
    {
        var c = await ClientAsync();
        var r = await c.GetAsync("/api/similar/by-seeds?ids=&mood=tense&genres=horror");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = await ItemsAsync(r);
        Assert.True(items.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Mood_Unknown_Key_Is_400()
    {
        var c = await ClientAsync();
        var r = await c.GetAsync("/api/similar/by-seeds?ids=&mood=happy");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        // message має перелік доступних ключів пресетів.
        Assert.Contains("cozy", body.GetProperty("message").GetString());
        Assert.Contains("tense", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Seeds_And_Mood_Are_Empty_Is_400()
    {
        var c = await ClientAsync();
        var r = await c.GetAsync("/api/similar/by-seeds?ids=");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        // НОВЕ повідомлення (еталони опційні) — не старе «ids є обов'язковим».
        Assert.Contains("настрій", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Mood_With_Seeds_Merges()
    {
        // Еталон Alpha (drama) + настрій cozy (comedy/family/romance/animation): Echo (comedy)
        // має пройти через жанровий сигнал агрегата (вага MoodGenreWeight).
        var c = await ClientAsync();
        var r = await c.GetAsync($"/api/similar/by-seeds?ids={AlphaId}&mood=cozy");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = await ItemsAsync(r);
        Assert.True(items.GetArrayLength() > 0);
        Assert.True(HasId(items, EchoId), "mood=cozy має злитися з еталоном і підтягнути Echo (comedy)");
    }

    [Fact]
    public async Task Mood_Only_Types_Filter_Applies()
    {
        // Hard-фільтр типів працює і на mood-only запиті: types=movie відсікає Foxtrot (series),
        // Delta (movie) лишається.
        var c = await ClientAsync();
        var r = await c.GetAsync("/api/similar/by-seeds?ids=&mood=tense&types=movie");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = await ItemsAsync(r);
        Assert.True(items.GetArrayLength() > 0);
        Assert.True(HasId(items, DeltaId), "Delta (movie) має залишитись при types=movie");
        Assert.False(HasId(items, FoxtrotId), "Foxtrot (series) має бути відсіканий hard-фільтром types=movie");
    }

    [Fact]
    public async Task Post_BySeeds_With_Mood_No_TmdbIds()
    {
        // Нове правило POST: 0 еталонів валідно, коли є настрій.
        var c = await ClientAsync();
        var r = await ApiTestFactory.PostJson(c, "/api/similar/by-seeds",
            new { tmdbIds = Array.Empty<int>(), mood = "tense" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = await ItemsAsync(r);
        Assert.True(items.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Old_Rule_Still_Holds()
    {
        // Стара семантика не зламана: еталони без жанрів/настрою працюють як раніше.
        var c = await ClientAsync();
        var r = await ApiTestFactory.PostJson(c, "/api/similar/by-seeds", new { tmdbIds = new[] { AlphaId } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = await ItemsAsync(r);
        Assert.True(items.GetArrayLength() > 0);
    }
}
