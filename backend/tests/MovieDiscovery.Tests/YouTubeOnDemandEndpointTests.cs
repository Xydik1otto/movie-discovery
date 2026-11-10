using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MovieDiscovery.Api;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Tests;

/// <summary>
/// On-demand YouTube-ендпоінти під захистом (фінальний стан: AddYouTubeRateLimiting
/// зареєстровано — саме це робить оркестратор одним рядком у Program.cs):
///   • GET /api/titles/{id}/trailers — 10/хв/IP (11-й запит у хвилині → 429 rate_limited);
///   • денний ГЛОБАЛЬНИЙ ліміт search (90/день) → 429 quota_exceeded;
///   • GET /api/titles/{id}/watch БЕЗ enrich — не лімітується і не палимо квоту;
///   • GET /api/titles/{id}/watch?enrich=youtube — під лімітом і під квотою.
/// HTTP до YouTube — стаб (жодних реальних викликів).
/// </summary>
public class YouTubeOnDemandEndpointTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = $"yt-ondemand-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Ключ «налаштований» → on-demand search-шлях активний (гейти працюють);
                    // реальних HTTP-викликів нема — PrimaryHandler підмінено стабом нижче.
                    ["Youtube:ApiKey"] = "TESTKEY",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
                services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(DbName));
                // Реєстрація youtube-лімітерів іденє в реальному Program.cs
                // (AddYouTubeRateLimiting) — тут НЕ дублюємо: повторний AddPolicy
                // з тим самим ім'ям кидає виняток на старті хоста.
                // ВЕСІ typed-клієнти (YoutubeClient/TrailerVerifier) — через стаб: search → порожньо.
                services.ConfigureAll<Microsoft.Extensions.Http.HttpClientFactoryOptions>(o =>
                    o.HttpMessageHandlerBuilderActions.Add(b =>
                        b.PrimaryHandler = new StubHttpHandler(req =>
                        {
                            var url = req.RequestUri?.ToString() ?? "";
                            if (url.Contains("search?")) return StubHttp.Json("""{"items": []}""");
                            if (url.Contains("videos?")) return StubHttp.Json("""{"items": []}""");
                            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                        })));
            });
        }

        public async Task<Title> SeedAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            var title = new Title { Type = "movie", TitleEn = "Se7en", Year = 1995, ImdbId = "tt0114369" };
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            return title;
        }

        public YouTubeDailySearchQuota Quota => Services.GetRequiredService<YouTubeDailySearchQuota>();

        public void ExhaustQuota()
        {
            var quota = Quota;
            while (quota.TryConsume()) { }
        }
    }

    private static JsonElement Body(HttpResponseMessage response)
    {
        var json = response.Content.ReadAsStringAsync().Result;
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public async Task TrailersGet_RateLimited_10PerMinutePerIp()
    {
        // Ліміт стоїть на ДОРОЗІЙ ділянці (on-demand search перед SyncTitleAsync), а не
        // на всьому ендпоінті: кешовані відповіді і повторні читання не лімітуються.
        // Тож 11 РІЗНИХ cache-miss id → 10 search-ів проходять, 11-й гейт → 429.
        await using var factory = new Factory();
        var client = factory.CreateClient();
        var ids = new List<int>();
        for (var i = 0; i < 11; i++)
            ids.Add((await factory.SeedAsync()).Id);

        HttpResponseMessage last = null!;
        foreach (var id in ids)
            last = await client.GetAsync($"/api/titles/{id}/trailers");

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.Equal("rate_limited", Body(last).GetProperty("error").GetString());
    }

    [Fact]
    public async Task TrailersGet_ExhaustedDailyQuota_Returns429_QuotaExceeded()
    {
        await using var factory = new Factory();
        var title = await factory.SeedAsync();
        factory.ExhaustQuota();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/titles/{title.Id}/trailers");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var body = Body(response);
        Assert.Equal("quota_exceeded", body.GetProperty("error").GetString());
        Assert.Equal("Денна квота трейлерів вичерпана, спробуйте завтра",
            body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WatchGet_EnrichExhaustedQuota_Returns429_QuotaExceeded()
    {
        await using var factory = new Factory();
        var title = await factory.SeedAsync();
        factory.ExhaustQuota();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/titles/{title.Id}/watch?enrich=youtube");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("quota_exceeded", Body(response).GetProperty("error").GetString());
    }

    [Fact]
    public async Task WatchGet_WithoutEnrich_NotLimited_EvenWhenQuotaExhausted()
    {
        // Звичайне читання «де подивитись» — дешеве БД-читання: НІ per-IP ліміту, НІ квоти search.
        await using var factory = new Factory();
        var title = await factory.SeedAsync();
        factory.ExhaustQuota();
        var client = factory.CreateClient();

        for (var i = 0; i < 12; i++)
        {
            var response = await client.GetAsync($"/api/titles/{title.Id}/watch");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task WatchGet_WithEnrich_RateLimited_10PerMinutePerIp()
    {
        await using var factory = new Factory();
        var title = await factory.SeedAsync();
        var client = factory.CreateClient();

        HttpResponseMessage last = null!;
        for (var i = 0; i < 11; i++)
            last = await client.GetAsync($"/api/titles/{title.Id}/watch?enrich=youtube");

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.Equal("rate_limited", Body(last).GetProperty("error").GetString());
    }
}
