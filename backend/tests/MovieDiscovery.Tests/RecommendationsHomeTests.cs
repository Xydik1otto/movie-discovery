using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Tests;

/// <summary>
/// Персональні рекомендації Головної (GET /api/recommendations/home): залогінений юзер
/// з лайками отримує релевантне (жанр лайкнутого — у відповіді), жорстке виключення всього,
/// що в списках, гість → 401, IMemoryCache per-user не ламає відповідь.
/// </summary>
public sealed class RecommendationsHomeTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;

    public RecommendationsHomeTests(ApiTestFactory f) => _f = f;

    private Task<HttpClient> GuestAsync() => _f.GuestClientAsync();
    private Task<HttpClient> AAsync() => _f.UserClientAsync(ApiTestFactory.A);

    /// <summary>Кандидати поза списками фікстур: drama (жанр лайків A) і comedy (шум-жанр).</summary>
    private static async Task SeedCandidatesAsync(ApiTestFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        if (db.Titles.Any(t => t.Id == 5100)) return; // ідемпотентно (тести класу послідовні)
        db.Titles.AddRange(
            new Core.Domain.Title { Id = 5100, Type = "movie", TitleEn = "Drama One", Year = 2001, Genres = ["drama"], Popularity = 8 },
            new Core.Domain.Title { Id = 5101, Type = "movie", TitleEn = "Drama Two", Year = 2003, Genres = ["drama"], Popularity = 7 },
            new Core.Domain.Title { Id = 5102, Type = "movie", TitleEn = "Comedy One", Year = 2020, Genres = ["comedy"], Popularity = 6 });
        await db.SaveChangesAsync();
    }

    private static int[] ItemIds(JsonElement body) =>
        body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetInt32()).ToArray();

    [Fact]
    public async Task User_With_Likes_Gets_Relevant_Items_And_Lists_Excluded()
    {
        await SeedCandidatesAsync(_f);
        var c = await AAsync();

        // A лайкає drama-тайтл 5001 (фікстурний watchlist 901 — теж drama 5000).
        var like = await ApiTestFactory.PostJson(c, "/api/lists/liked", new { titleId = 5001 });
        Assert.True((int)like.StatusCode is 201 or 409, $"like → {(int)like.StatusCode}");

        var r = await c.GetAsync("/api/recommendations/home?limit=20&lang=en");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var items = body.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0, "очікувано хоча б один кандидат drama");

        var ids = ItemIds(body);
        // (c) ЖОРТКЕ виключення: тайтли з watchlist-фікстури (5000) і щойно лайкнутий (5001) — немає.
        Assert.DoesNotContain(5000, ids);
        Assert.DoesNotContain(5001, ids);
        // (b) Релевантність: кандидати — лише топ-жанр юзера (drama); comedy-тайтл не потрапляє.
        Assert.All(ids, id => Assert.True(id is 5100 or 5101, $"зайвий кандидат {id}"));
        Assert.Contains(items.EnumerateArray(),
            i => i.GetProperty("genres").EnumerateArray().Any(g => g.GetString() == "drama"));
        // reason — опційний строковий мотив (uk-назва жанру в uk-режимі; en-режим → en-ключ).
        var reason = body.GetProperty("reason").GetString();
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("drama", reason!);
    }

    [Fact]
    public async Task Anonymous_Is_401()
    {
        var c = await GuestAsync();
        var r = await c.GetAsync("/api/recommendations/home?limit=20&lang=uk");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Cache_Second_Call_Stable_Not_Broken()
    {
        await SeedCandidatesAsync(_f);
        var c = await AAsync(); // кеш пер-юзерний: A вже з лайками (тест 1 або власний лайк нижче)

        var like = await ApiTestFactory.PostJson(c, "/api/lists/liked", new { titleId = 5001 });
        Assert.True((int)like.StatusCode is 201 or 409);

        var r1 = await c.GetAsync("/api/recommendations/home?limit=20&lang=en");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var b1 = await r1.Content.ReadFromJsonAsync<JsonElement>();
        var ids1 = ItemIds(b1);
        Assert.True(ids1.Length > 0);

        // Повторний виклик (та сама сесія, той самий кеш-ключ) — стабільний, 200, той самий набір.
        var r2 = await c.GetAsync("/api/recommendations/home?limit=20&lang=en");
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var b2 = await r2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ids1, ItemIds(b2));

        // Інший limit → інший кеш-ключ → такий самий пул, обрізаний до ліміту (не ламається).
        var r3 = await c.GetAsync("/api/recommendations/home?limit=1&lang=en");
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
        var b3 = await r3.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, b3.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void Home_Weights_Match_Spec()
    {
        Assert.Equal(1.0, DiscoverService.HomeWeightOfKind("liked", null));
        Assert.Equal(0.8, DiscoverService.HomeWeightOfKind("watched", 7));
        Assert.Equal(0.5, DiscoverService.HomeWeightOfKind("watched", null));
        Assert.Equal(0.5, DiscoverService.HomeWeightOfKind("watched", 6));
        Assert.Equal(0.6, DiscoverService.HomeWeightOfKind("watchlist", null));
        Assert.Equal(0.6, DiscoverService.HomeWeightOfKind("watching", null));
        Assert.Equal(0.6, DiscoverService.HomeWeightOfKind("read", null));
        Assert.Equal(0.6, DiscoverService.HomeWeightOfKind("read", 9));
        Assert.Equal(0.6, DiscoverService.HomeWeightOfKind("reading", null));
        Assert.Equal(0.3, DiscoverService.HomeWeightOfKind("on_hold", null));
        Assert.Equal(-0.8, DiscoverService.HomeWeightOfKind("disliked", null));
        Assert.Equal(0.0, DiscoverService.HomeWeightOfKind("dropped", null));
    }
}
