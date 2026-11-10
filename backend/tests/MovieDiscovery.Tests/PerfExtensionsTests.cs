using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MovieDiscovery.Api.Extensions;
using MovieDiscovery.Infrastructure.Persistence;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// Перф-хвиля (11.11): PerfExtensions.AddPerfOptimizations — стиснення відповідей і
/// output-cache каталожних видань — через РЕАЛЬНИЙ HTTP-пайплайн (WebApplicationFactory).
///
/// Program.cs у зоні оркестратора (правка заборонена), тому middleware підключається
/// тестовим IStartupFilter — ТОЧНО в тому порядку, який приписано для Program.cs:
/// UseResponseCompression ЗОВНІ UseOutputCache (кеш зберігає не-стиснутий
/// боді, стиснення — per-запит на виході).
///
/// Нюанси, які тримають тести нефлакі:
/// - фабрика своя (не ApiTestFactory): чистий OutputCache-стор на кожен тест;
/// - кеш-хіт доводиться СТАЛОЮ «несвіжістю»: між двома GET змінюємо TitleEn в InMemory-БД
///   і перевіряємо, що відповідь лишається старою (кеш), а для /api/lists — навпаки,
///   що відповідь СВІЖА (персональний ендпоїнт поза політикою);
/// - q-пошуки у WAF-тестах НЕ використовуються: EF.Functions.ILike не транслюється
///   InMemory-провайдером (усталена адаптація — див. SearchPagingTests).
/// </summary>
public sealed class PerfExtensionsTests
{
    private readonly PerfApiFactory _f = new();

    public PerfExtensionsTests() => _f.SeedAsync().GetAwaiter().GetResult();

    // ─── Pure-предикат політики: що саме входить у «каталог» ───

    [Fact]
    public void CatalogPredicate_MatchesOnlyCatalogGets()
    {
        static OutputCacheContext Ctx(string method, string path)
        {
            var ctx = new OutputCacheContext { HttpContext = new DefaultHttpContext() };
            ctx.HttpContext.Request.Method = method;
            ctx.HttpContext.Request.Path = path;
            return ctx;
        }

        // Каталог: GET-видання під трьома базами (з query і без).
        Assert.True(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/search")));
        Assert.True(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/search?type=movie&lang=ru")));
        Assert.True(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/similar/by-seeds?ids=1&lang=en")));
        Assert.True(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/title/517682")));
        Assert.True(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/title/517682/similar")));
        Assert.True(PerfExtensions.IsCatalogGet(Ctx("get", "/api/SEARCH"))); // case-insensitive

        // Не GET — не кешується (POST-форми, legacy-fallback, admin-синки).
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("POST", "/api/search")));
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("POST", "/api/similar/by-seeds")));
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("POST", "/api/similar/by-text")));

        // Персональні/службові зони — поза політикою взагалі.
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/lists")));
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/lists/stats")));
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/auth/me")));
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/profiles")));
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/admin/sync/justwatch")));

        // /api/titles/{id}/watch і /api/titles/{id}/trailers — свіжість важливіша
        // (префікс /api/title/ зі слешем їх не чіпає).
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/titles/1/watch")));
        Assert.False(PerfExtensions.IsCatalogGet(Ctx("GET", "/api/titles/1/trailers")));
    }

    // ─── Response compression через реальний пайплайн ───

    [Fact]
    public async Task Search_WithGzipAcceptEncoding_CompressesResponse()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/search?type=movie");
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await _f.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);

        // Боді має бути валідним JSON з тайтлом Alpha ПІСЛЯ розпакування.
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var isRawGzip = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        using Stream body = isRawGzip
            ? new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)
            : new MemoryStream(bytes); // клієнт міг сам розпакувати — тоді це вже JSON
        using var doc = await JsonDocument.ParseAsync(body);
        var items = doc.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);
        Assert.Equal("Alpha", items[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Search_WithoutAcceptEncoding_NotCompressed()
    {
        using var response = await _f.CreateClient().GetAsync("/api/search?type=movie");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    // ─── OutputCache: стабільність + чесна «несвіжість» як доказ кеш-хіта ───

    [Fact]
    public async Task Search_TwoConsecutiveGets_SameQuery_StableResponses()
    {
        var client = _f.CreateClient();
        var first = await client.GetAsync("/api/search?type=movie&sort=popularity");
        var second = await client.GetAsync("/api/search?type=movie&sort=popularity");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body1 = await first.Content.ReadAsStringAsync();
        var body2 = await second.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body1));
        Assert.Equal(body1, body2); // middleware не ламає відповідь; другий GET — з кешу
    }

    [Fact]
    public async Task OutputCache_TitleDetail_ServesStaleWithinTtl()
    {
        // /api/title/{id} не має власного IMemoryCache — «несвіжість» другого GET
        // доводить саме OutputCache (base /api/title/).
        var client = _f.CreateClient();
        var first = await client.GetAsync("/api/title/5000");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("Alpha", await first.Content.ReadAsStringAsync());

        await _f.RenameTitleAsync(5000, "Renamed");

        var second = await client.GetAsync("/api/title/5000");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("Alpha", body);          // кеш-хіт: закешований кадр
        Assert.DoesNotContain("Renamed", body);  // свіжий білд дав би нову назву
    }

    [Fact]
    public async Task OutputCache_PersonalLists_NotCached()
    {
        // Персональна зона (lists) поза політикою: другий GET мусить побачити
        // СВІЖУ назву з БД, а не закешований кадр.
        var client = _f.CreateClient();
        var first = await client.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("Alpha", await first.Content.ReadAsStringAsync());

        await _f.RenameTitleAsync(5000, "Renamed");

        var second = await client.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("Renamed", body);        // свіжий читок — НЕ з кешу
        Assert.DoesNotContain("Alpha", body);    // і сліду закешованого кадру нема
    }

    [Fact]
    public async Task Post_ToCatalogBase_IsPassedThroughUnbroken()
    {
        // POST під каталожною базою не кешується і не ламається middleware-ланцюгом:
        // очікуємо природну валідаційну помилку MVC, а не 500.
        var response = await _f.CreateClient().PostAsync("/api/similar/by-seeds", content: null);
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType,
            $"Очікували 400/415, отримали {(int)response.StatusCode}");
    }
}

/// <summary>
/// Тест-фабрика перфової хвилі: та сама схема, що ApiTestFactory (Development + InMemory
/// на кожен інстанс + гостьовий профіль 1), ПЛЮС реєстрація AddPerfOptimizations
/// (middleware вмикає справжній Program.cs — ця фабрика його повторних врізок не робить).
/// </summary>
internal sealed class PerfApiFactory : WebApplicationFactory<Program>
{
    public string DbName { get; } = $"perf-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GUEST_PROFILE_ID"] = "1", // гостьовий серверний профіль — id 1
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
            services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(DbName));

            // Перф-реєстрації: справжній Program.cs тепер сам реєструє і сам вмикає
            // middleware — guard ідемпотентності робить цей повторний виклик no-op.
            services.AddPerfOptimizations(new ConfigurationBuilder().Build());
        });
    }

    /// <summary>Гостьовий профіль 1 + два тайтли (5000 Alpha / 5001 Beta) + запис списку 900.</summary>
    public async Task SeedAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        if (db.Titles.Any(t => t.Id == 5000)) return; // ідемпотентно
        db.Profiles.Add(new Core.Domain.Profile { Id = 1, Name = "Guest", IsDefault = true });
        db.Titles.AddRange(
            new Core.Domain.Title
            {
                Id = 5000, Type = "movie", TitleEn = "Alpha", Year = 2001,
                Genres = ["drama"], Popularity = 10,
            },
            new Core.Domain.Title
            {
                Id = 5001, Type = "movie", TitleEn = "Beta", Year = 2002,
                Genres = ["drama"], Popularity = 9,
            });
        db.ListEntries.Add(new Core.Domain.ListEntry
        {
            Id = 900, ProfileId = 1, Kind = "watchlist", TitleId = 5000, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Мутація назви тайтла МІЖ двома GET — доказ кеш-хіта/свіжості.</summary>
    public async Task RenameTitleAsync(int titleId, string newTitleEn)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var title = db.Titles.First(t => t.Id == titleId);
        title.TitleEn = newTitleEn;
        await db.SaveChangesAsync();
    }
}

