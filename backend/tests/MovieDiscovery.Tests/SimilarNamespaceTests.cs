using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MovieDiscovery.Infrastructure.Persistence;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// MD-03: розділення ID-просторів similar/by-seeds.
/// GET ?ids= — ЛИШЕ внутрішні title id (резолв строго по t.Id); POST {tmdbIds} — legacy
/// подвійний резолв (tmdb-перший, fallback на внутрішній id). Колізія A.Id == B.TmdbId:
/// GET ids=A → A, POST tmdbIds=A → B. Невідомі id → 404 titles_not_found без часткових результатів.
/// </summary>
public sealed class SimilarNamespaceTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;
    public SimilarNamespaceTests(ApiTestFactory f) => _f = f;

    // Колізійний фіксчер: Alpha.Id == Beta.TmdbId == 7001.
    // Alpha(2001, drama) — резолвиться GET-ом лише по внутрішньому id;
    // Beta(1995, horror) — резолвиться POST-ом першою (tmdb-пріоритет);
    // Gamma(1995, horror) — кандидат у пулі.
    private const int AlphaId = 7001;   // Alpha.Id = Beta.TmdbId
    private const int BetaId = 7002;
    private const int GammaId = 7003;

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
                Id = BetaId, Type = "movie", TitleEn = "Beta", TmdbId = AlphaId, Year = 1995,
                Genres = ["horror"], Popularity = 10,
            },
            new Core.Domain.Title
            {
                Id = GammaId, Type = "movie", TitleEn = "Gamma", Year = 1995,
                Genres = ["horror"], Popularity = 5,
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Collision_Get_Internal_Id_Resolves_Internal_Title()
    {
        var c = await ClientAsync();
        // GET: seed = Alpha (внутрішній id). Hard-фільтр року 1990–1999 відсіює Alpha (2001),
        // у пулі лишаються Beta і Gamma → 2 items.
        var r = await c.GetAsync($"/api/similar/by-seeds?ids={AlphaId}&yearFrom=1990&yearTo=1999");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Collision_Post_TmdbId_Resolves_Tmdb_Title()
    {
        var c = await ClientAsync();
        // POST legacy: tmdb-пріоритет → seed = Beta (TmdbId=7001). Пул 1990–1999 мінус Beta
        // → лише Gamma → 1 item (Alpha відфільтрована роком).
        var r = await ApiTestFactory.PostJson(c, "/api/similar/by-seeds",
            new { tmdbIds = new[] { AlphaId }, yearFrom = 1990, yearTo = 1999 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Unknown_Id_Get_Is_404_Titles_Not_Found()
    {
        var c = await ClientAsync();
        var r = await c.GetAsync("/api/similar/by-seeds?ids=888888");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("titles_not_found", body.GetProperty("error").GetString());
        Assert.Equal(1, body.GetProperty("missing").GetArrayLength());
        Assert.Equal(888888, body.GetProperty("missing")[0].GetInt32());
    }

    [Fact]
    public async Task Mixed_Unknown_Id_Get_Is_404_No_Partial_Results()
    {
        var c = await ClientAsync();
        // Змішаний список з невідомим id → 404 з missing, БЕЗ часткової видачі.
        var r = await c.GetAsync($"/api/similar/by-seeds?ids={AlphaId},888888");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("items", text);
        Assert.Contains("888888", text);
    }

    [Fact]
    public async Task Zero_Ids_Are_400_For_Both_Verbs()
    {
        var c = await ClientAsync();
        var getEmpty = await c.GetAsync("/api/similar/by-seeds?ids=");
        Assert.Equal(HttpStatusCode.BadRequest, getEmpty.StatusCode);
        var getMissing = await c.GetAsync("/api/similar/by-seeds");
        Assert.Equal(HttpStatusCode.BadRequest, getMissing.StatusCode);
        var post = await ApiTestFactory.PostJson(c, "/api/similar/by-seeds", new { tmdbIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Eleven_Ids_Are_400_For_Both_Verbs()
    {
        var c = await ClientAsync();
        var eleven = string.Join(",", Enumerable.Range(1, 11));
        var get = await c.GetAsync($"/api/similar/by-seeds?ids={eleven}");
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        var post = await ApiTestFactory.PostJson(c, "/api/similar/by-seeds",
            new { tmdbIds = Enumerable.Range(1, 11) });
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task Post_Legacy_Internal_Id_Fallback_Still_Works()
    {
        var c = await ClientAsync();
        // Gamma має TmdbId=null — legacy POST резолвить її по внутрішньому id (fallback збережено).
        var r = await ApiTestFactory.PostJson(c, "/api/similar/by-seeds", new { tmdbIds = new[] { GammaId } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Get_Internal_Title_Without_Filters_Returns_Items()
    {
        var c = await ClientAsync();
        // Alpha (IMDb-like, tmdbId=null) резолвиться GET-ом без фільтрів — регрес MD-03.
        var r = await c.GetAsync($"/api/similar/by-seeds?ids={AlphaId}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("items").GetArrayLength() > 0);
    }
}
