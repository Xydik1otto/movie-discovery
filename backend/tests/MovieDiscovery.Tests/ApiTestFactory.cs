using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Tests;

/// <summary>
/// Інтеграційна фабрика API (MD-10): реальний HTTP-сервер у процесі, InMemory-БД
/// на кожен екземпляр, без зовнішніх сервісів. Реєстрація AddDbContext ПІСЛЯ
/// Program.cs перезаписує Npgsql → тести йдуть на ізольованій БД.
/// </summary>
public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public string DbName { get; } = $"api-tests-{Guid.NewGuid():N}";

    public static readonly (string Email, string User, string Pass) A = ("a@t.ua", "aliceA1", "Passw0rdA");
    public static readonly (string Email, string User, string Pass) B = ("b@t.ua", "bobB1", "Passw0rdB");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            // MD-01: гостьовий серверний профіль — id 1 (засіється нижче).
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GUEST_PROFILE_ID"] = "1",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
            services.AddDbContext<MovieDiscoveryDbContext>(o =>
                o.UseInMemoryDatabase(DbName));
        });
    }

    /// <summary>Засіяти статичні фікстури: гостьовий профіль 1 (без акаунта) з записом 900,
    /// тайтли 5000 (з tmdbId) і 5001 (IMDb-only, tmdbId=null). Акаунти реєструються через API.</summary>
    public async Task SeedAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();

        db.Profiles.Add(new Core.Domain.Profile { Id = 1, Name = "Guest", IsDefault = true });

        db.Titles.AddRange(
            new Core.Domain.Title { Id = 5000, Type = "movie", TitleEn = "Alpha", Year = 2001, Genres = ["drama"], Popularity = 10 },
            new Core.Domain.Title { Id = 5001, Type = "movie", TitleEn = "Beta", Year = 2002, Genres = ["drama"], Popularity = 9 });

        db.ListEntries.Add(
            new Core.Domain.ListEntry { Id = 900, ProfileId = 1, Kind = "watchlist", TitleId = 5000, CreatedAt = DateTime.UtcNow });

        await db.SaveChangesAsync();
    }

    private bool _seeded;
    private bool _usersReady;
    private readonly Dictionary<string, bool> _userSeeded = new();
    // Повторний login на кожен тест упирається у rate limiter (login 5/хв/IP) → 429
    // на 6-му тесті. Кешуємо автентифікований клієнт: один login на юзера на фабрику.
    private readonly Dictionary<string, HttpClient> _userClients = new();

    private async Task EnsureSeedAsync()
    {
        if (_seeded) return;
        _seeded = true;
        await SeedAsync();
    }

    /// <summary>HTTP-клієнт із cookie-банком (окремий для гостя/юзера).</summary>
    public HttpClient NewClient() => CreateDefaultClient(new CookieHandler());

    /// <summary>Гість (без cookie-сесії) на засіяній БД; юзери A/B гарантовано є.</summary>
    public async Task<HttpClient> GuestClientAsync()
    {
        await EnsureEnvironmentAsync();
        return NewClient();
    }

    /// <summary>Реєстрація (або логін) акаунта; після першої реєстрації — фікстурний запис
    /// списку (901 для A, 902 для B) у ВЛАСНИЙ профіль, створений застосунком.</summary>
    public async Task<HttpClient> UserClientAsync((string Email, string User, string Pass) u)
    {
        await EnsureEnvironmentAsync();
        if (_userClients.TryGetValue(u.User, out var cached)) return cached;
        var client = NewClient();
        await EnsureAuthenticatedAsync(client, u);
        _userClients[u.User] = client;
        return client;
    }

    private async Task EnsureEnvironmentAsync()
    {
        await EnsureSeedAsync();
        if (_usersReady) return;
        _usersReady = true;
        // A і B мають існувати для тестів у ЛЮБОМУ порядку (xunit не гарантує послідовність).
        await EnsureUserAsync(ApiTestFactory.A);
        await EnsureUserAsync(ApiTestFactory.B);
    }

    private async Task EnsureUserAsync((string Email, string User, string Pass) u)
    {
        if (_userSeeded.TryGetValue(u.User, out var done) && done) return;
        _userSeeded[u.User] = true;

        using var regScope = Services.CreateScope();
        var probeDb = regScope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        if (!probeDb.Users.Any(x => x.UserName == u.User))
        {
            var c = NewClient();
            var reg = await PostJson(c, "/api/auth/register",
                new { email = u.Email, username = u.User, password = u.Pass });
            if ((int)reg.StatusCode is 409 or 400)
                await PostJson(c, "/api/auth/login", new { email = u.Email, password = u.Pass });
        }

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var identityId = db.Users.First(x => x.UserName == u.User).Id;
        var profileId = db.UserProfiles.First(x => x.UserId == identityId).ProfileId;
        var entryId = u.User == A.User ? 901 : 902;
        if (!db.ListEntries.Any(e => e.Id == entryId))
        {
            db.ListEntries.Add(new Core.Domain.ListEntry
            {
                Id = entryId, ProfileId = profileId, Kind = "watchlist",
                TitleId = u.User == A.User ? 5000 : 5001,
                Rating = u.User == A.User ? null : 7,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task EnsureAuthenticatedAsync(HttpClient client, (string Email, string User, string Pass) u)
    {
        var me = await client.GetAsync("/api/auth/me");
        if ((int)me.StatusCode == 200) return; // cookie вже живі
        var login = await PostJson(client, "/api/auth/login", new { email = u.Email, password = u.Pass });
        if ((int)login.StatusCode == 200) return;
        // Логін не вдався (наприклад, інший пароль) — контрольована помилка fixture'ів.
        throw new InvalidOperationException($"Не вдалось автентифікувати {u.User}: HTTP {(int)login.StatusCode}");
    }

    /// <summary>Legacy-сценарій (MD-01): акаунт C, приріплений на гостьовий профіль 1 —
    /// після цього гостьовий серверний шлях має вимикатись.</summary>
    public async Task<HttpClient> GuestLinkedLegacyAsync()
    {
        await EnsureSeedAsync();
        var client = NewClient();
        var reg = await PostJson(client, "/api/auth/register",
            new { email = "c@t.ua", username = "carolC1", password = "Passw0rdC" });
        if ((int)reg.StatusCode is 409 or 400)
            await PostJson(client, "/api/auth/login", new { email = "c@t.ua", password = "Passw0rdC" });

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var identityId = db.Users.First(x => x.UserName == "carolC1").Id;
        db.UserProfiles.Add(new Core.Domain.UserProfile { UserId = identityId, ProfileId = 1 });
        await db.SaveChangesAsync();
        return client;
    }

    public static async Task<HttpResponseMessage> PostJson(HttpClient c, string path, object body)
    {
        var r = await c.PostAsync(path, new StringContent(Json(body), Encoding.UTF8, "application/json"));
        return r;
    }

    /// <summary>Справжній HTTP PATCH (PostJson шле POST — для PATCH /api/lists/{itemId} потрібен окремий хелпер).</summary>
    public static Task<HttpResponseMessage> PatchJson(HttpClient c, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, path)
        { Content = new StringContent(Json(body), Encoding.UTF8, "application/json") };
        return c.SendAsync(req);
    }

    public static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);
}

/// <summary>Cookie-банк між запитами TestServer-клієнта (auth-сесії в інтеграційних тестах).</summary>
public sealed class CookieHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var cookieHeader = _cookies.GetCookieHeader(uri);
        if (cookieHeader.Length > 0)
            request.Headers.Add("Cookie", cookieHeader);

        var response = await base.SendAsync(request, ct);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
                _cookies.SetCookies(uri, sc);
        }
        return response;
    }
}
