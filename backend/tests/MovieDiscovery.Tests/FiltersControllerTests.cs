using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MovieDiscovery.Api.Controllers;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Tests;

/// <summary>
/// WAF-фабрика для метадати фільтрів (стиль PosterSourceTestFactory): InMemory-БД на
/// екземпляр фабрики, hosted-служби вимкнені (Testing), без зовнішніх сервісів.
/// </summary>
public sealed class FiltersTestFactory : WebApplicationFactory<Program>
{
    public string DbName { get; } = $"filters-api-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GUEST_PROFILE_ID"] = "1",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
            services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(DbName));
        });
    }

    /// <summary>Засіяти тайтли фільтрів (10-15 фейкових записів — покривають усі категорії).</summary>
    public async Task SeedAsync(params Title[] titles)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        db.Titles.AddRange(titles);
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// GET /api/filters?category=... — метадата фільтрів за категорією. InMemory-семантика:
/// 1) валідна категорія повертає ЛИШЕ релевантні фільтри (anime має studios, movie НЕ має
///    statuses/studios — ключі відсутні в JSON, не порожні масиви);
/// 2) фасети (genres/countries) — по частоті всередині КАТЕГОРІЇ, з лімітом і тай-брейком;
/// 3) counts — розбивка по типах (shorts = movie з RuntimeMin≤40; animation_movies = movie
///    з жанром animation; animation_series = (series|show + animation) АБО type='anime');
/// 4) невалідна НЕПОРОЖНЯ категорія → 400; відсутня/порожня = 'all' (200, фікс «Головна
///    шле /api/filters без category»);
/// 5) TTL-кеш: другий виклик — той самий об'єкт БЕЗ ходіння в БД, ключі різних категорій
///    не перетинаються;
/// 6) ліміти фасетів (genres ≤40, countries ≤20);
/// 7) SQL-трансляція unnest-агрегатів (Npgsql ToQueryString — без БД): WHERE категорії
///    всередині unnest (агрегати по категорійному сету, не по всьому каталогу).
/// </summary>
public class FiltersControllerTests
{
    // ── фікстура: 14 тайтлів, очікувані значення нижче рахуються вручну ──

    internal static readonly Title[] SeedTitles =
    [
        new() { Id = 1, Type = "movie", TitleEn = "Alpha", Genres = ["action", "adventure"], Countries = ["US"], Country = "US", Year = 2020, RuntimeMin = 120, Popularity = 10 },
        new() { Id = 2, Type = "movie", TitleEn = "Beta", Genres = ["action"], Countries = ["US"], Year = 2021, RuntimeMin = 30, Popularity = 9 }, // short
        new() { Id = 3, Type = "movie", TitleEn = "Gamma", Genres = ["animation", "family"], Countries = ["US"], Year = 2022, RuntimeMin = 90, Popularity = 8 }, // animation movie
        new() { Id = 4, Type = "movie", TitleEn = "Delta", Genres = ["drama"], Countries = ["GB"], Year = 1990, RuntimeMin = 100, Popularity = 7 },
        new() { Id = 5, Type = "series", TitleEn = "Epsilon", Genres = ["drama", "comedy"], Countries = ["US"], Year = 2019, Status = "ended", Popularity = 9 },
        new() { Id = 6, Type = "series", TitleEn = "Zeta", Genres = ["animation", "comedy"], Countries = ["US"], Year = 2021, Status = "returning", Popularity = 8 }, // animation series
        new() { Id = 7, Type = "show", TitleEn = "Eta", Genres = ["reality-tv"], Countries = ["US"], Year = 2022, Status = "returning", Popularity = 6 },
        new() { Id = 8, Type = "anime", TitleEn = "Theta", Genres = ["action", "animation"], Country = "JP", Year = 2023, Status = "returning", Studios = ["Toei", "Madhouse"], Popularity = 10 },
        new() { Id = 9, Type = "anime", TitleEn = "Iota", Genres = ["adventure"], Year = 2024, Studios = ["Toei"], Popularity = 5 },
        new() { Id = 10, Type = "manga", TitleEn = "Kappa", Genres = ["action", "romance"], Year = 2020, Popularity = 4 },
        new() { Id = 11, Type = "manga", TitleEn = "Lambda", Genres = ["action"], Year = 2021, Popularity = 3 },
        new() { Id = 12, Type = "movie", TitleEn = "Mu", Genres = ["drama", "animation"], Countries = ["UA"], Country = "UA", Year = 1985, RuntimeMin = 35, Popularity = 2 }, // short + animation movie
        new() { Id = 13, Type = "series", TitleEn = "Nu", Genres = ["drama"], Country = "KR", Year = 2020, Status = "ended", Popularity = 7 }, // dorama
        new() { Id = 14, Type = "movie", TitleEn = "Xi", Genres = ["drama"], Country = "KR", Year = 2019, RuntimeMin = 95, Popularity = 5 }, // dorama movie
    ];

    private static async Task<(FiltersTestFactory Factory, HttpClient Client)> NewSeededAsync()
    {
        var factory = new FiltersTestFactory();
        await factory.SeedAsync(SeedTitles);
        return (factory, factory.CreateDefaultClient());
    }

    private static async Task<(int Status, JsonDocument Json)> GetFiltersAsync(HttpClient client, string? category)
    {
        var url = string.IsNullOrEmpty(category) ? "/api/filters" : $"/api/filters?category={Uri.EscapeDataString(category)}";
        var resp = await client.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        return ((int)resp.StatusCode, JsonDocument.Parse(body));
    }

    /// <summary>Елемент фасету за значенням (value → count) або null.</summary>
    private static int? CountOf(JsonElement facet, string value)
    {
        foreach (var item in facet.EnumerateArray())
            if (item.GetProperty("value").GetString() == value)
                return item.GetProperty("count").GetInt32();
        return null;
    }

    /// <summary>Список (value, count) фасету.</summary>
    private static List<(string Value, int Count)> ListOf(JsonElement facet) =>
        [.. facet.EnumerateArray().Select(i => (i.GetProperty("value").GetString()!, i.GetProperty("count").GetInt32()))];

    /// <summary>Ключ фільтра відсутній у JSON (нерелевантний категорії — не порожній масив)?</summary>
    private static bool KeyAbsent(JsonElement filters, string key) =>
        filters.ValueKind == JsonValueKind.Object && !filters.TryGetProperty(key, out _);

    // ── movie: жанри/роки/країни є; statuses/studios — ВІДСУТНІ ──

    [Fact]
    public async Task Movie_Returns_Relevant_Filters_Without_Statuses_And_Studios()
    {
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, "movie");

        Assert.Equal(200, status);
        var root = json.RootElement;
        Assert.Equal("movie", root.GetProperty("category").GetString());

        var filters = root.GetProperty("filters");
        // жанри категорії (не всього каталогу): drama 3, action 2, animation 2, adventure 1, family 1
        var genres = ListOf(filters.GetProperty("genres"));
        Assert.Equal(
            [("drama", 3), ("action", 2), ("animation", 2), ("adventure", 1), ("family", 1)],
            genres);
        // роки — min/max категорії
        var years = filters.GetProperty("years");
        Assert.Equal(1985, years.GetProperty("min").GetInt32());
        Assert.Equal(2022, years.GetProperty("max").GetInt32());
        // країни: US 3, GB 1, KR 1 (Xi), UA 1 (title.country або countries[])
        Assert.Equal(
            [("US", 3), ("GB", 1), ("KR", 1), ("UA", 1)],
            ListOf(filters.GetProperty("countries")));
        // нерелевантні фільтри — ключів НЕМАЄ в JSON (не порожні масиви)
        Assert.True(KeyAbsent(filters, "statuses"), "movie не повинен мати statuses");
        Assert.True(KeyAbsent(filters, "studios"), "movie не повинен мати studios");
        Assert.Equal(["popularity", "rating", "year", "title"],
            filters.GetProperty("sorts").EnumerateArray().Select(s => s.GetString()!).ToList());

        var counts = root.GetProperty("counts");
        Assert.Equal(6, counts.GetProperty("total").GetInt32());
        Assert.Equal(6, counts.GetProperty("movies").GetInt32());
        Assert.Equal(0, counts.GetProperty("series").GetInt32());
        Assert.Equal(2, counts.GetProperty("shorts").GetInt32()); // Beta 30 хв + Mu 35 хв
        Assert.Equal(2, counts.GetProperty("animation_movies").GetInt32()); // Gamma + Mu
        Assert.Equal(0, counts.GetProperty("animation_series").GetInt32());
    }

    // ── anime: студії + статуси присутні ──

    [Fact]
    public async Task Anime_Has_Studios_And_Statuses()
    {
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, "anime");

        Assert.Equal(200, status);
        var filters = json.RootElement.GetProperty("filters");

        // studios з titles.studios по частоті: Toei 2, Madhouse 1
        Assert.Equal([("Toei", 2), ("Madhouse", 1)], ListOf(filters.GetProperty("studios")));
        // статуси серіального контенту: returning 1 (Theta), Iota без статусу
        Assert.Equal([("returning", 1)], ListOf(filters.GetProperty("statuses")));
        // жанри категорії (анімешні, не каталог): по 1, тай-брейк за value
        Assert.Equal(
            [("action", 1), ("adventure", 1), ("animation", 1)],
            ListOf(filters.GetProperty("genres")));
        var years = filters.GetProperty("years");
        Assert.Equal(2023, years.GetProperty("min").GetInt32());
        Assert.Equal(2024, years.GetProperty("max").GetInt32());
        // країна: JP (title.country) — 1
        Assert.Equal([("JP", 1)], ListOf(filters.GetProperty("countries")));

        var counts = json.RootElement.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("total").GetInt32());
        Assert.Equal(0, counts.GetProperty("movies").GetInt32());
        Assert.Equal(2, counts.GetProperty("animation_series").GetInt32()); // type='anime' — анімація за визначенням
    }

    // ── series: статуси + counts серіального блоку (type='series' СТРОГО, як у SearchService;
    // 'show' — окремий тип: у category=series не входить, але в counts-блок 'all' серій+шоу) ──

    [Fact]
    public async Task Series_Has_Statuses_And_Series_Counts()
    {
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, "series");

        Assert.Equal(200, status);
        var filters = json.RootElement.GetProperty("filters");
        Assert.True(KeyAbsent(filters, "studios"), "series не повинен мати studios");
        // сет: Epsilon, Zeta, Nu (Eta — type='show', у category=series не входить)
        Assert.Equal([("ended", 2), ("returning", 1)], ListOf(filters.GetProperty("statuses")));
        // жанри категорії: comedy 2, drama 2 (тай-брейк за value), animation 1
        Assert.Equal(
            [("comedy", 2), ("drama", 2), ("animation", 1)],
            ListOf(filters.GetProperty("genres")));
        var years = filters.GetProperty("years");
        Assert.Equal(2019, years.GetProperty("min").GetInt32());
        Assert.Equal(2021, years.GetProperty("max").GetInt32());
        Assert.Equal([("US", 2), ("KR", 1)], ListOf(filters.GetProperty("countries")));

        var counts = json.RootElement.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("total").GetInt32());
        Assert.Equal(0, counts.GetProperty("movies").GetInt32());
        Assert.Equal(3, counts.GetProperty("series").GetInt32());
        Assert.Equal(1, counts.GetProperty("animation_series").GetInt32()); // Zeta
        Assert.Equal(0, counts.GetProperty("shorts").GetInt32());
    }

    // ── cartoon: аніме НЕ дублюється (SearchService-контракт), studios ключ є (порожній) ──

    [Fact]
    public async Task Cartoon_Excludes_Anime_And_Counts_Animation()
    {
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, "cartoon");

        Assert.Equal(200, status);
        var filters = json.RootElement.GetProperty("filters");
        // аніме (Theta/Iota) виключене: жанру action з аніме НЕМАЄ
        var genres = ListOf(filters.GetProperty("genres"));
        Assert.Null(CountOf(filters.GetProperty("genres"), "action"));
        // сет cartoon: Gamma + Zeta + Mu (movie|series з жанром animation)
        Assert.Equal(
            [("animation", 3), ("comedy", 1), ("drama", 1), ("family", 1)],
            genres);
        // studios релевантні (ключ є), але в cartoon порожні (студії — аніме-специфіка)
        Assert.False(KeyAbsent(filters, "studios"));
        Assert.Empty(filters.GetProperty("studios").EnumerateArray());
        Assert.True(KeyAbsent(filters, "statuses"));

        var counts = json.RootElement.GetProperty("counts");
        Assert.Equal(3, counts.GetProperty("total").GetInt32());
        Assert.Equal(2, counts.GetProperty("movies").GetInt32());
        Assert.Equal(1, counts.GetProperty("series").GetInt32());
        Assert.Equal(2, counts.GetProperty("animation_movies").GetInt32()); // Gamma + Mu
        Assert.Equal(1, counts.GetProperty("animation_series").GetInt32()); // Zeta
        // Mu (1985) теж мультфільм-короткометражка
        Assert.Equal(1, counts.GetProperty("shorts").GetInt32());
        var years = filters.GetProperty("years");
        Assert.Equal(1985, years.GetProperty("min").GetInt32());
        Assert.Equal(2022, years.GetProperty("max").GetInt32());
        Assert.Equal([("US", 2), ("UA", 1)], ListOf(filters.GetProperty("countries")));
    }

    // ── tvshow: overlap-жанри, show рахується в series ──

    [Fact]
    public async Task Tvshow_Selects_Overlap_Genres()
    {
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, "tvshow");

        Assert.Equal(200, status);
        var filters = json.RootElement.GetProperty("filters");
        Assert.Equal([("reality-tv", 1)], ListOf(filters.GetProperty("genres")));
        Assert.Equal([("returning", 1)], ListOf(filters.GetProperty("statuses"))); // tvshow — серіальний контент

        var counts = json.RootElement.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("total").GetInt32());
        Assert.Equal(1, counts.GetProperty("series").GetInt32()); // show → series-блок
    }

    // ── dorama: title.country IN (KR,CN,JP) І drama ──

    [Fact]
    public async Task Dorama_Country_And_Drama()
    {
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, "dorama");

        Assert.Equal(200, status);
        var filters = json.RootElement.GetProperty("filters");
        Assert.Equal([("drama", 2)], ListOf(filters.GetProperty("genres")));
        Assert.Equal([("KR", 2)], ListOf(filters.GetProperty("countries")));
        var years = filters.GetProperty("years");
        Assert.Equal(2019, years.GetProperty("min").GetInt32());
        Assert.Equal(2020, years.GetProperty("max").GetInt32());
        Assert.True(KeyAbsent(filters, "statuses"));

        var counts = json.RootElement.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("total").GetInt32());
        Assert.Equal(1, counts.GetProperty("movies").GetInt32());
        Assert.Equal(1, counts.GetProperty("series").GetInt32());
    }

    // ── all: увесь каталог, один агрегат ──

    [Fact]
    public async Task All_Covers_Catalog()
    {
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, "all");

        Assert.Equal(200, status);
        var root = json.RootElement;
        var filters = root.GetProperty("filters");
        Assert.Equal(
            [("action", 5), ("drama", 5), ("animation", 4), ("adventure", 2),
             ("comedy", 2), ("family", 1), ("reality-tv", 1), ("romance", 1)],
            ListOf(filters.GetProperty("genres")));
        var years = filters.GetProperty("years");
        Assert.Equal(1985, years.GetProperty("min").GetInt32());
        Assert.Equal(2024, years.GetProperty("max").GetInt32());
        Assert.Equal(
            [("US", 6), ("KR", 2), ("GB", 1), ("JP", 1), ("UA", 1)],
            ListOf(filters.GetProperty("countries")));
        Assert.True(KeyAbsent(filters, "studios"), "all не повинен мати studios");
        Assert.True(KeyAbsent(filters, "statuses"), "all не повинен мати statuses");

        var counts = root.GetProperty("counts");
        Assert.Equal(14, counts.GetProperty("total").GetInt32());
        Assert.Equal(6, counts.GetProperty("movies").GetInt32());
        Assert.Equal(4, counts.GetProperty("series").GetInt32());
        Assert.Equal(2, counts.GetProperty("shorts").GetInt32());
        Assert.Equal(2, counts.GetProperty("animation_movies").GetInt32());
        Assert.Equal(3, counts.GetProperty("animation_series").GetInt32()); // Zeta + Theta + Iota
    }

    // ── захист: невалідна НЕПОРОЖНЯ категорія → 400; відсутня/порожня = 'all' (200) ──

    [Fact]
    public async Task Invalid_Category_Returns_400()
    {
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, "telenovela");
        Assert.Equal(400, status);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Missing_Category_Defaults_To_All()
    {
        // Фікс «Головна шле /api/filters без category → 400»: відсутня/порожня категорія —
        // валідний запит 'all' (200), а не помилка.
        var (factory, client) = await NewSeededAsync();
        var (status, json) = await GetFiltersAsync(client, null);
        Assert.Equal(200, status);
        Assert.Equal("all", json.RootElement.GetProperty("category").GetString());
        Assert.True(json.RootElement.GetProperty("counts").GetProperty("total").GetInt32() > 0);

        // Порожнє значення ?category= — та сама семантика 'all'.
        var (statusEmpty, jsonEmpty) = await GetFiltersAsync(client, "");
        Assert.Equal(200, statusEmpty);
        Assert.Equal("all", jsonEmpty.RootElement.GetProperty("category").GetString());
    }

    // ── кеш: другий виклик — той самий об'єкт, БД не чіпається; категорії не перетинаються ──

    [Fact]
    public async Task Caching_SecondCall_SameInstance_Without_Db()
    {
        var db = new MovieDiscoveryDbContext(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase("filters_cache_" + Guid.NewGuid().ToString("N")).Options);
        db.Titles.AddRange(SeedTitles);
        await db.SaveChangesAsync();

        var controller = new FiltersController(db, new MemoryCache(new MemoryCacheOptions()));

        var first = await controller.Get(category: "movie");
        await db.DisposeAsync(); // другий виклик має бути ПОВНІСТЮ з кешу

        var second = await controller.Get(category: "movie");

        // той самий екземпляр = кеш-хіт (свіжий білд дав би новий об'єкт і впав би
        // на disposed-контексті) → кешування не ламає коректність
        Assert.Same(Assert.IsType<OkObjectResult>(first).Value, Assert.IsType<OkObjectResult>(second).Value);
    }

    [Fact]
    public async Task Caching_Does_Not_Cross_Contaminate_Categories()
    {
        var (factory, client) = await NewSeededAsync();

        var (s1, j1) = await GetFiltersAsync(client, "movie");
        var (s2, j2) = await GetFiltersAsync(client, "movie"); // кеш-хіт
        var (s3, j3) = await GetFiltersAsync(client, "anime"); // інший ключ кешу

        Assert.Equal(200, s1);
        Assert.Equal(200, s2);
        Assert.Equal(200, s3);
        // кеш-хіт повертає ту саму метадату (total категорії не змінюється)
        Assert.Equal(
            j1.RootElement.GetProperty("counts").GetProperty("total").GetInt32(),
            j2.RootElement.GetProperty("counts").GetProperty("total").GetInt32());
        // різні категорії — різні відповіді (ключі кешу не перетинаються)
        Assert.NotEqual(
            j1.RootElement.GetProperty("counts").GetProperty("total").GetInt32(),
            j3.RootElement.GetProperty("counts").GetProperty("total").GetInt32());
        Assert.Equal("anime", j3.RootElement.GetProperty("category").GetString());
    }

    // ── ліміти фасетів (genres ≤40, countries ≤20) ──

    [Fact]
    public async Task Genres_Facet_Capped_At_40()
    {
        var db = new MovieDiscoveryDbContext(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase("filters_cap_" + Guid.NewGuid().ToString("N")).Options);
        for (var i = 1; i <= 45; i++)
            db.Titles.Add(new Title { Type = "manga", TitleEn = $"M{i:D2}", Genres = [$"g{i:D2}"] });
        await db.SaveChangesAsync();

        var controller = new FiltersController(db, new MemoryCache(new MemoryCacheOptions()));
        var result = Assert.IsType<OkObjectResult>(await controller.Get(category: "manga"));

        var json = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(json);
        var genres = doc.RootElement.GetProperty("filters").GetProperty("genres");
        Assert.Equal(40, genres.GetArrayLength());
        var values = genres.EnumerateArray().Select(g => g.GetProperty("value").GetString()!).ToList();
        // усі по 1 → тай-брейк за value (ordinal): перші 40
        Assert.Equal("g01", values[0]);
        Assert.Equal("g40", values[^1]);
        Assert.DoesNotContain("g45", values);
    }

    [Fact]
    public async Task Countries_Facet_Capped_At_20()
    {
        var db = new MovieDiscoveryDbContext(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase("filters_cc_" + Guid.NewGuid().ToString("N")).Options);
        for (var i = 1; i <= 25; i++)
            db.Titles.Add(new Title { Type = "movie", TitleEn = $"C{i:D2}", Country = $"c{i:D2}" });
        await db.SaveChangesAsync();

        var controller = new FiltersController(db, new MemoryCache(new MemoryCacheOptions()));
        var result = Assert.IsType<OkObjectResult>(await controller.Get(category: "movie"));

        var json = JsonSerializer.Serialize(result.Value);
        using var doc = JsonDocument.Parse(json);
        var countries = doc.RootElement.GetProperty("filters").GetProperty("countries");
        Assert.Equal(20, countries.GetArrayLength());
        Assert.Equal("c01", countries[0].GetProperty("value").GetString());
        Assert.Equal("c20", countries[19].GetProperty("value").GetString());
        Assert.Null(CountOf(countries, "c25"));
    }

    // ── SQL-трансляція unnest-агрегатів (Npgsql ToQueryString — без БД) ──

    private static Infrastructure.Persistence.MovieDiscoveryDbContext CreateNpgsql() =>
        new(new DbContextOptionsBuilder<Infrastructure.Persistence.MovieDiscoveryDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=filters_translation_check", npg => npg.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    [Fact]
    public void Sql_Genres_Facet_Translates_With_Category_Inside()
    {
        using var db = CreateNpgsql();
        var sql = db.Database
            .SqlQueryRaw<FacetRow>(FiltersController.FacetSql("movie", "genres", 40))
            .ToQueryString();
        Assert.Contains("unnest", sql);
        Assert.Contains("t.genres", sql);
        Assert.Contains("t.type = 'movie'", sql);
        // WHERE (категорія) стоїть ДО GROUP BY — агрегат по категорійному сету
        Assert.True(
            sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase) >= 0
            && sql.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase) > sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase),
            $"Очікується WHERE → GROUP BY: {sql}");
    }

    [Fact]
    public void Sql_Studios_Facet_Translates_For_Anime()
    {
        using var db = CreateNpgsql();
        var sql = db.Database
            .SqlQueryRaw<FacetRow>(FiltersController.FacetSql("anime", "studios", 40))
            .ToQueryString();
        Assert.Contains("unnest", sql);
        Assert.Contains("t.studios", sql);
        Assert.Contains("t.type = 'anime'", sql);
    }

    [Fact]
    public void Sql_Countries_Facet_Prefers_Scalar_Over_Array()
    {
        using var db = CreateNpgsql();
        var sql = db.Database
            .SqlQueryRaw<FacetRow>(FiltersController.FacetSql("movie", "countries", 20))
            .ToQueryString();
        // scalar-or-array: country (akas) з фолбеком на countries[] — без подвійного обліку
        Assert.Contains("CASE WHEN t.country IS NOT NULL THEN ARRAY[t.country] ELSE t.countries END", sql);
        Assert.Contains("unnest", sql);
    }

    [Fact]
    public void Sql_CategoryFragments_Mirror_SearchService_Contract()
    {
        // movie|series|anime|manga → type (та сама умова, що в SearchService)
        Assert.Equal("t.type = 'movie'", FiltersController.CategorySql("movie"));
        Assert.Equal("t.type = 'series'", FiltersController.CategorySql("series"));
        Assert.Equal("t.type = 'anime'", FiltersController.CategorySql("anime"));
        Assert.Equal("t.type = 'manga'", FiltersController.CategorySql("manga"));
        // tvshow: overlap по GenreSets (константи з Core — єдине джерело правди)
        Assert.Equal(
            "t.genres && ARRAY['reality-tv', 'talk-show', 'game-show', 'variety']",
            FiltersController.CategorySql("tvshow"));
        // cartoon: type IN (movie,series) І animation; аніме НЕ дублюється
        Assert.Equal(
            "t.type IN ('movie','series') AND t.genres @> ARRAY['animation']",
            FiltersController.CategorySql("cartoon"));
        // dorama: country IN (KR,CN,JP) І drama
        Assert.Equal(
            "t.country = ANY(ARRAY['KR', 'CN', 'JP']) AND t.genres @> ARRAY['drama']",
            FiltersController.CategorySql("dorama"));
        // all — без умови (один агрегат по всьому)
        Assert.Equal("TRUE", FiltersController.CategorySql("all"));
    }

    [Fact]
    public async Task InMemory_CategoryPredicate_Matches_SearchService_CategorySelection()
    {
        // семантика предикатів категорії = семантика SearchService (контракт FR-19):
        // той самий засів, той самий розподіл, що у VirtualCategorySearchTests
        var db = new MovieDiscoveryDbContext(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase("filters_pred_" + Guid.NewGuid().ToString("N")).Options);
        db.Titles.AddRange(
            new Title { Type = "series", TitleEn = "Big Brother", Genres = ["reality-tv", "game-show"] },
            new Title { Type = "anime", TitleEn = "Naruto", Genres = ["animation", "action"] },
            new Title { Type = "movie", TitleEn = "Toy Story", Genres = ["animation", "family"] },
            new Title { Type = "series", TitleEn = "SpongeBob", Genres = ["animation", "comedy"] },
            new Title { Type = "movie", TitleEn = "Oldboy", Genres = ["drama", "thriller"], Country = "KR" },
            new Title { Type = "movie", TitleEn = "Parasite", Genres = ["drama"], Countries = ["KR"] });
        await db.SaveChangesAsync();

        async Task<List<string>> NamesAsync(string category)
        {
            var rows = await db.Titles
                .Where(FiltersController.CategoryPredicate(category))
                .Select(t => t.TitleEn)
                .ToListAsync();
            return [.. rows.OrderBy(n => n)];
        }

        Assert.Equal(["Big Brother"], await NamesAsync("tvshow")); // лише overlap-жанри
        Assert.Equal(["SpongeBob", "Toy Story"], await NamesAsync("cartoon")); // аніме НЕ cartoon
        Assert.Equal(["Oldboy"], await NamesAsync("dorama")); // лише title.country: countries[]-only (Parasite) НЕ dorama
        Assert.Equal(["Oldboy", "Parasite", "Toy Story"], await NamesAsync("movie"));
        Assert.Equal(["Big Brother", "SpongeBob"], await NamesAsync("series"));
        Assert.Equal(["Naruto"], await NamesAsync("anime"));
        Assert.Equal(["Big Brother", "Naruto", "Oldboy", "Parasite", "SpongeBob", "Toy Story"],
            await NamesAsync("all"));
    }
}
