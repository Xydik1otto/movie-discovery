using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MovieDiscovery.Api.Controllers;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Taste;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using Xunit;

namespace MovieDiscovery.Tests;

// ────────────────────────────────────────────────────────────────────────────────
// Хвиля фіксів контрактів і чесних помилок (7 завдань): по одному тест-класу на фікс.
// Імена класів — унікальні під discharge через --filter FullyQualifiedName~<Клас>.
// ────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Фікс 2: GET /api/lists/stats має повертати watchlistCount («Хочу глянути»), як і решту
/// лічильників kind'ів. Сервісний шар (точні числа) + WAF-перевірка поля у відповіді.
/// </summary>
public class ListStatsWatchlistCountTests
{
    private static (MovieDiscoveryDbContext Db, UserListService Lists) New(string name)
    {
        var db = TestDb.Create(name);
        var taste = new TasteProfileService(db, Options.Create(new ProfileOptions()));
        return (db, new UserListService(db, taste));
    }

    private static Title NewTitle(int id, string[] genres) => new()
    {
        Id = id, Type = "movie", TitleEn = $"T{id}", Year = 2020, Genres = genres, Popularity = 100 - id,
    };

    [Fact]
    public async Task Stats_Counts_Watchlist_Like_Other_Kinds()
    {
        var (db, lists) = New("stats-watchlist-count");
        db.Titles.Add(NewTitle(1, ["drama"]));
        db.Titles.Add(NewTitle(2, ["drama"]));
        await db.SaveChangesAsync();

        await lists.AddAsync("u1", ListKinds.Watchlist, 1, null, null);
        await lists.AddAsync("u1", ListKinds.Watchlist, 2, null, null);
        await lists.AddAsync("u1", ListKinds.Watched, 1, null, 7);
        await lists.AddAsync("u1", ListKinds.Liked, 2, null, null);

        var stats = await lists.StatsAsync("u1");
        Assert.Equal(2, stats.WatchlistCount); // «Хочу глянути»
        Assert.Equal(1, stats.WatchedCount);
        Assert.Equal(1, stats.LikedCount);
    }

    [Fact]
    public async Task Stats_Empty_Profile_Has_Zero_WatchlistCount()
    {
        var (db, lists) = New("stats-watchlist-empty");
        db.Titles.Add(NewTitle(1, ["drama"]));
        await db.SaveChangesAsync();

        var stats = await lists.StatsAsync("u-nobody");
        Assert.Equal(0, stats.WatchlistCount);
    }

    [Fact]
    public async Task Stats_Endpoint_Response_Contains_WatchlistCount()
    {
        var factory = new ApiTestFactory();
        var client = await factory.UserClientAsync(ApiTestFactory.A); // A має watchlist-запис 901
        var r = await client.GetAsync("/api/lists/stats");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("watchlistCount", out var wc), "watchlistCount відсутній у /api/lists/stats");
        Assert.True(wc.GetInt32() >= 1); // фіксурний watchlist-запис юзера A
        Assert.True(body.TryGetProperty("watchedCount", out _)); // старі поля на місці (адитивно)
    }
}

/// <summary>
/// Фікс 3: POST /api/lists/migrate-guest обмежує items (максимум 500) — більше →
/// 400 invalid_request «Занадто багато елементів (максимум 500)», рівно 500 → проходить.
/// </summary>
public class MigrateGuestCapTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;
    public MigrateGuestCapTests(ApiTestFactory f) => _f = f;

    // titleId 5001: у юзера A фіксстурний запис 901 — (watchlist, 5000), тому 5001 дає
    // детермінований результат: перший item moved, решта 499 — duplicate.
    private static object Body(int n) => new
    {
        items = Enumerable.Range(0, n).Select(_ => new { kind = "watchlist", titleId = 5001 }).ToArray(),
    };

    [Fact]
    public async Task Over_500_Items_Is_400_Invalid_Request()
    {
        var client = await _f.UserClientAsync(ApiTestFactory.A);
        var r = await ApiTestFactory.PostJson(client, "/api/lists/migrate-guest", Body(501));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal("Занадто багато елементів (максимум 500)", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Exactly_500_Items_Passes_Validation()
    {
        var client = await _f.UserClientAsync(ApiTestFactory.A);
        var r = await ApiTestFactory.PostJson(client, "/api/lists/migrate-guest", Body(500));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // валідацію пройдено (moved=1, решта duplicate)
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("moved").GetInt32());
        Assert.Equal(499, body.GetProperty("skipped").GetInt32());
    }
}

/// <summary>
/// Фікс 1: коли гостьовий серверний шлях ВИМКНЕНО (GUEST_PROFILE_ID вказує на неіснуючий
/// профіль), анонімний GET /api/lists віддає 401 unauthorized «Потрібен вхід, щоб бачити
/// свої списки» (узгоджено з /api/lists/stats), а не оманливий 404 «Профіль не знайдено».
/// На фабриці з ПРАЦЮЮЧИМ гостьовим шляхом поведінка не змінюється (покрито ListsOwnershipTests).
/// </summary>
public class ListGuestUnavailableTests
{
    /// <summary>Гостьовий шлях вимкнено: GUEST_PROFILE_ID → неіснуючий профіль 999.</summary>
    private sealed class NoGuestFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = $"lists-noguest-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["GUEST_PROFILE_ID"] = "999" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
                services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(DbName));
            });
        }

        public async Task SeedAsync()
        {
            // Профіль 1 НЕ сіємо: Testing-хост сам сіє дефолтний профіль (Program.cs);
            // гостьовий шлях тут і так вимкнено (GUEST_PROFILE_ID → неіснуючий 999).
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Titles.Add(new Core.Domain.Title { Id = 6001, Type = "movie", TitleEn = "G1", Year = 2020, Genres = ["drama"], Popularity = 5 });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Anonymous_Get_Lists_Is_401_When_Guest_Path_Disabled()
    {
        var factory = new NoGuestFactory();
        await factory.SeedAsync();
        var client = factory.CreateDefaultClient();

        var r = await client.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized", body.GetProperty("error").GetString());
        Assert.Equal("Потрібен вхід, щоб бачити свої списки", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Anonymous_Get_Lists_With_ProfileId_Also_401_When_Guest_Path_Disabled()
    {
        // Гостьового шляху нема → жоден profileId анонімові недоступний: чесний 401
        // (потрібен вхід), а не 404-підказка про існування/неіснування профілю.
        var factory = new NoGuestFactory();
        await factory.SeedAsync();
        var client = factory.CreateDefaultClient();

        var r = await client.GetAsync("/api/lists?profileId=999999");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized", body.GetProperty("error").GetString());
    }
}

/// <summary>
/// Фікс 4: legacy import-джоби з ProfileId == NULL НЕ належать анонімам — GET
/// /api/import/jobs/{id} віддає 404; власність — лише явний збіг із designated guest
/// profile (джоби, створені з чесним profile_id, читаються як і раніше).
/// </summary>
public class ImportNullProfileJobTests
{
    private sealed class NullJobImportFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = $"import-null-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["GUEST_PROFILE_ID"] = "1" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
                services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(DbName));
            });
        }

        public async Task SeedAsync()
        {
            // Профіль 1 НЕ сіємо: Testing-хост сам сіє дефолтний профіль (Program.cs).
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.ImportJobs.Add(new ImportJob { Source = ImportSourceCatalog.Shikimori, Username = "legacy-null", ProfileId = null, Status = "queued" });
            db.ImportJobs.Add(new ImportJob { Source = ImportSourceCatalog.Shikimori, Username = "guest-owned", ProfileId = 1, Status = "queued" });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Null_Profile_Job_Is_Not_Anonymous_Owned_404()
    {
        var factory = new NullJobImportFactory();
        await factory.SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var nullJobId = db.ImportJobs.AsNoTracking().Single(j => j.ProfileId == null).Id;

        var client = factory.CreateDefaultClient();
        var r = await client.GetAsync($"/api/import/jobs/{nullJobId}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode); // було 200 — «спільна» legacy-джоба
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Explicit_Guest_Profile_Job_Still_Readable_By_Anonymous()
    {
        var factory = new NullJobImportFactory();
        await factory.SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var guestJobId = db.ImportJobs.AsNoTracking().Single(j => j.ProfileId == 1).Id;

        var client = factory.CreateDefaultClient();
        var r = await client.GetAsync($"/api/import/jobs/{guestJobId}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // живий guest-флоу не зламано
    }
}

/// <summary>
/// Фікс 5: /api/filters БЕЗ category (або з порожньою) = 'all' (200) — фікс «Головна шле
/// /api/filters без category → 400»; невалідна НЕПОРОЖНЯ категорія — 400 як і раніше.
/// </summary>
public class FiltersDefaultCategoryTests
{
    private static async Task<(FiltersTestFactory Factory, HttpClient Client)> NewSeededAsync()
    {
        var factory = new FiltersTestFactory();
        await factory.SeedAsync(FiltersControllerTests.SeedTitles);
        return (factory, factory.CreateDefaultClient());
    }

    private static async Task<(int Status, JsonElement Body)> GetAsync(HttpClient client, string? category)
    {
        var url = category is null ? "/api/filters" : $"/api/filters?category={Uri.EscapeDataString(category)}";
        var resp = await client.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        return ((int)resp.StatusCode, JsonDocument.Parse(text).RootElement);
    }

    [Fact]
    public async Task Missing_Category_Is_Treated_As_All_200()
    {
        var (_, client) = await NewSeededAsync();
        var (status, body) = await GetAsync(client, null);
        Assert.Equal(200, status);
        Assert.Equal("all", body.GetProperty("category").GetString());
        Assert.Equal(14, body.GetProperty("counts").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Empty_Category_Value_Is_Treated_As_All_200()
    {
        var (_, client) = await NewSeededAsync();
        var (status, body) = await GetAsync(client, "");      // ?category= (порожньо)
        Assert.Equal(200, status);
        Assert.Equal("all", body.GetProperty("category").GetString());

        var (statusWs, bodyWs) = await GetAsync(client, "   "); // лише пробіли
        Assert.Equal(200, statusWs);
        Assert.Equal("all", bodyWs.GetProperty("category").GetString());
    }

    [Fact]
    public async Task Invalid_NonEmpty_Category_Still_400()
    {
        var (_, client) = await NewSeededAsync();
        var (status, body) = await GetAsync(client, "telenovela");
        Assert.Equal(400, status);
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }
}

/// <summary>
/// Фікс 7: minQuality дружній до людини. Рядок біндиться [FromQuery] як string і парситься
/// контролером (SearchQuery.MinQuality лишається int?): hd→720, fhd/fullhd→1080, uhd/4k→2160,
/// sd→480, Np→число, числове як є; невідоме → 400 з переліком допустимих (замість
/// беззмістовного ASP.NET-біндінгу "The value 'hd' is not valid").
/// </summary>
public class SearchMinQualityParserTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;
    public SearchMinQualityParserTests(ApiTestFactory f) => _f = f;

    private const string FriendlyList = "minQuality має бути 480 | 720 | 1080 | 2160 або hd | fhd | uhd";

    // ── парсер (чиста функція) ──

    [Theory]
    [InlineData("hd", 720)]
    [InlineData("HD", 720)]
    [InlineData("fhd", 1080)]
    [InlineData("fullhd", 1080)]
    [InlineData("FullHD", 1080)]
    [InlineData("uhd", 2160)]
    [InlineData("4k", 2160)]
    [InlineData("4K", 2160)]
    [InlineData("sd", 480)]
    [InlineData("480p", 480)]
    [InlineData("720p", 720)]
    [InlineData("1080p", 1080)]
    [InlineData("2160p", 2160)]
    [InlineData("1080", 1080)]
    [InlineData(" 720 ", 720)]
    public void Parse_Aliases_And_Numbers(string raw, int expected)
    {
        var (value, error) = SearchController.ParseMinQuality(raw);
        Assert.Null(error);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Missing_Is_No_Filter(string? raw)
    {
        var (value, error) = SearchController.ParseMinQuality(raw);
        Assert.Null(error);
        Assert.Null(value);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("hq")]
    [InlineData("8k")]
    [InlineData("1080i")]
    public void Parse_Unknown_Is_Error_With_Allowed_List(string raw)
    {
        var (value, error) = SearchController.ParseMinQuality(raw);
        Assert.Null(value);
        Assert.Equal(FriendlyList, error);
    }

    // ── крізь реальний HTTP-пайплайн (біндинг більше не 400-ить беззмістовно) ──

    [Fact]
    public async Task Search_With_MinQuality_Hd_Is_200()
    {
        var c = await _f.GuestClientAsync();
        var r = await c.GetAsync("/api/search?minQuality=hd");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Search_With_Numeric_MinQuality_Still_200()
    {
        var c = await _f.GuestClientAsync();
        var r = await c.GetAsync("/api/search?minQuality=1080");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Search_Unknown_MinQuality_Is_Friendly_400()
    {
        var c = await _f.GuestClientAsync();
        var r = await c.GetAsync("/api/search?minQuality=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
        Assert.Equal(FriendlyList, body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Search_Numeric_Out_Of_Allowlist_Still_400()
    {
        var c = await _f.GuestClientAsync();
        var r = await c.GetAsync("/api/search?minQuality=1000");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("error").GetString());
    }
}

/// <summary>
/// Фікс 6: /api/health швидкий завжди. ollama-проба — жорсткий кап 1 с (CTS CancelAfter
/// І WhenAny-таймер — навіть коли реалізація ігнорує токен) + кеш статусу 30 с; проби
/// паралельно (DbContext — окремо); titlesCount — свіже зі кешу, stale миттєво + фон,
/// холодний COUNT із бюджетом 0.75 с. Живий замір prod (до фіксу): 6.0 с на першому
/// виклику після паузи, 22-33 мс на наступних — винен холодний COUNT, не ollama.
/// </summary>
public class HealthOllamaTimeoutTests
{
    private sealed class SlowEmbedderHonoringCt : IEmbedder
    {
        public int Calls;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(5000, ct); // честь токен: зривається CancelAfter'ом контролера
            return true;
        }
    }

    private sealed class SlowEmbedderIgnoringCt : IEmbedder
    {
        public int Calls;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(5000); // токен ІГНОРУЄ — має рятувати WhenAny-таймер
            return true;
        }
    }

    private sealed class SlowOllamaHealthFactory(IEmbedder fake) : WebApplicationFactory<Program>
    {
        public string DbName { get; } = $"health-slow-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["GUEST_PROFILE_ID"] = "1" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
                services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(DbName));
                services.RemoveAll<IEmbedder>();
                services.AddSingleton(fake);
            });
        }

        public async Task SeedAsync()
        {
            // Профіль 1 НЕ сіємо: Testing-хост сам робить EnsureCreated + SeedDefaultProfileAsync
            // (Program.cs) — дубль Id=1 падає «same key already added».
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Titles.AddRange(
                new Core.Domain.Title { Id = 7001, Type = "movie", TitleEn = "H1", Year = 2020, Genres = ["drama"], Popularity = 5 },
                new Core.Domain.Title { Id = 7002, Type = "movie", TitleEn = "H2", Year = 2021, Genres = ["drama"], Popularity = 4 });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Health_Ollama_Probe_Capped_At_1s_And_Cached_30s()
    {
        var fake = new SlowEmbedderHonoringCt();
        var factory = new SlowOllamaHealthFactory(fake);
        await factory.SeedAsync();
        var client = factory.CreateDefaultClient();

        var sw = Stopwatch.StartNew();
        var r = await client.GetAsync("/api/health");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ollama").GetBoolean()); // 5s-проба зрізана → чесний false
        Assert.True(body.GetProperty("db").GetBoolean());
        Assert.Equal(2, body.GetProperty("titlesCount").GetInt32()); // холодний COUNT на InMemory миттєвий
        // Головне: відповідь повернулась ДО завершення 5s-проби (кап 1 с працює).
        Assert.True(sw.ElapsedMilliseconds < 4800, $"health занадто повільний: {sw.ElapsedMilliseconds}ms");

        // Повторні виклики в межах 30 с — кеш статусу: проба НЕ перезапускається.
        await client.GetAsync("/api/health");
        await client.GetAsync("/api/health/ready");
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task Health_Ollama_Probe_Capped_Even_When_Token_Ignored()
    {
        var fake = new SlowEmbedderIgnoringCt();
        var factory = new SlowOllamaHealthFactory(fake);
        await factory.SeedAsync();
        var client = factory.CreateDefaultClient();

        var sw = Stopwatch.StartNew();
        var r = await client.GetAsync("/api/health");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ollama").GetBoolean());
        Assert.True(sw.ElapsedMilliseconds < 4800,
            $"WhenAny-кап не спрацював на embedder'і без токена: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task TitlesCount_Stale_Value_Served_Instantly_With_Background_Refresh()
    {
        var fake = new SlowEmbedderHonoringCt();
        var factory = new SlowOllamaHealthFactory(fake);
        await factory.SeedAsync();
        var cache = factory.Services.GetRequiredService<IMemoryCache>();
        cache.Set(HealthController.CountCacheKey,
            new HealthController.TitlesCountValue(777, DateTimeOffset.UtcNow.AddMinutes(-5)),
            TimeSpan.FromHours(24)); // свіжість 60 с минула, ретенція ще жива
        var client = factory.CreateDefaultClient();

        var r = await client.GetAsync("/api/health");
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        // stale-serve: віддається останнє відоме значення (777), а НЕ перерахунок (2);
        // фоновий перерахунок дозапише кеш — асинхронно, без блокування відповіді.
        Assert.Equal(777, body.GetProperty("titlesCount").GetInt32());
    }

    [Fact]
    public async Task TitlesCount_Fresh_Cached_Value_Served_Without_Recompute()
    {
        var fake = new SlowEmbedderHonoringCt();
        var factory = new SlowOllamaHealthFactory(fake);
        await factory.SeedAsync();
        var cache = factory.Services.GetRequiredService<IMemoryCache>();
        cache.Set(HealthController.CountCacheKey,
            new HealthController.TitlesCountValue(555, DateTimeOffset.UtcNow),
            TimeSpan.FromHours(24));
        var client = factory.CreateDefaultClient();

        var r = await client.GetAsync("/api/health");
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(555, body.GetProperty("titlesCount").GetInt32());
    }
}
