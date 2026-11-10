using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Api;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>MD-05: BotIdentityService — telegram-ідентичність → ВЛАСНИЙ профіль (заміна ProfileId=1).</summary>
public sealed class BotIdentityServiceTests
{
    private static (MovieDiscoveryDbContext Db, BotIdentityService Svc) New(string name, string? apiKey = "k")
    {
        var db = TestDb.Create(name);
        var svc = new BotIdentityService(db, Options.Create(new BotOptions { ApiKey = apiKey }));
        return (db, svc);
    }

    [Fact]
    public async Task Unknown_Telegram_Id_Creates_Own_NonDefault_Profile()
    {
        var (db, svc) = New($"bot-new-{Guid.NewGuid():N}");
        var profile = await svc.ResolveAsync(123456789);
        Assert.False(profile.IsDefault);
        Assert.StartsWith("TG «", profile.Name);
        Assert.Equal(1, await db.BotIdentities.CountAsync());
        Assert.Equal(1, await db.Profiles.CountAsync());
    }

    [Fact]
    public async Task Second_Call_Returns_Same_Profile_No_Duplicate()
    {
        var (db, svc) = New($"bot-same-{Guid.NewGuid():N}");
        var first = await svc.ResolveAsync(42);
        var second = await svc.ResolveAsync(42);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.BotIdentities.CountAsync());
        Assert.Equal(1, await db.Profiles.CountAsync());
    }

    [Fact]
    public async Task Different_Telegram_Ids_Get_Different_Profiles()
    {
        var (db, svc) = New($"bot-diff-{Guid.NewGuid():N}");
        var a = await svc.ResolveAsync(111);
        var b = await svc.ResolveAsync(222);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, await db.BotIdentities.CountAsync());
        Assert.Equal(2, await db.Profiles.CountAsync());
    }

    [Theory]
    [InlineData(5, "TG «5»")]
    [InlineData(12345678, "TG «12345678»")]
    [InlineData(123456789, "TG «12345678»")] // довгий id — перші 8 символів
    public async Task Profile_Name_Guards_Short_Ids(long tgId, string expectedName)
    {
        var (_, svc) = New($"bot-name-{tgId}-{Guid.NewGuid():N}");
        var profile = await svc.ResolveAsync(tgId);
        Assert.Equal(expectedName, profile.Name);
    }

    [Fact]
    public async Task Guest_Default_Profile_Is_Not_Touched()
    {
        var (db, svc) = New($"bot-guest-{Guid.NewGuid():N}");
        db.Profiles.Add(new Core.Domain.Profile { Id = 1, Name = "Головний", IsDefault = true });
        await db.SaveChangesAsync();

        await svc.ResolveAsync(777);

        var guest = await db.Profiles.FirstAsync(p => p.Id == 1);
        Assert.True(guest.IsDefault);
        Assert.Equal(0, await db.BotIdentities.CountAsync(x => x.ProfileId == 1));
    }

    [Fact]
    public void KeyMatches_Disabled_Or_Wrong_Key_Is_False()
    {
        var (_, disabled) = New($"bot-key-off-{Guid.NewGuid():N}", apiKey: null);
        Assert.False(disabled.Enabled);
        Assert.False(disabled.KeyMatches("anything"));

        var (_, blank) = New($"bot-key-blank-{Guid.NewGuid():N}", apiKey: "   ");
        Assert.False(blank.Enabled);
        Assert.False(blank.KeyMatches("   "));

        var (_, enabled) = New($"bot-key-on-{Guid.NewGuid():N}", apiKey: "k");
        Assert.True(enabled.Enabled);
        Assert.True(enabled.KeyMatches("k"));
        Assert.False(enabled.KeyMatches("wrong"));
        Assert.False(enabled.KeyMatches(null));
    }
}

/// <summary>WAF-фабрика для bot-header тестів: Bot:ApiKey керується тестом (валідний або вимкнений).
/// Безпараметровий ctor (вимога xunit IClassFixture); для "вимкненого ключа" встановіть BotKey=null
/// ДО першого клієнта (ConfigureWebHost читає його ліниво при першому старті сервера).</summary>
public sealed class BotApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Бот-ключ для Bot:ApiKey (null/порожній → bot-header auth вимкнено).</summary>
    public string? BotKey { get; set; } = "test-bot-key";

    public string DbName { get; } = $"bot-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GUEST_PROFILE_ID"] = "1",
                // Вищий пріоритет за env BOT_API_KEY хоста — детермінований ключ у тестах.
                ["Bot:ApiKey"] = BotKey ?? "",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
            services.AddDbContext<MovieDiscoveryDbContext>(o => o.UseInMemoryDatabase(DbName));
        });
    }

    public HttpClient BotClient(long telegramUserId, string? key = null)
    {
        var c = CreateDefaultClient();
        c.DefaultRequestHeaders.Add("X-Bot-Key", key ?? (BotKey ?? "test-bot-key"));
        c.DefaultRequestHeaders.Add("X-Telegram-User-Id", telegramUserId.ToString());
        return c;
    }

    public async Task SeedAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        if (!db.Profiles.Any(p => p.Id == 1))
            db.Profiles.Add(new Core.Domain.Profile { Id = 1, Name = "Guest", IsDefault = true });
        if (!db.Titles.Any(t => t.Id == 5000))
        {
            db.Titles.AddRange(
                new Core.Domain.Title { Id = 5000, Type = "movie", TitleEn = "Alpha", Year = 2001, Genres = ["drama"], Popularity = 10 },
                new Core.Domain.Title { Id = 5001, Type = "movie", TitleEn = "Beta", Year = 2002, Genres = ["comedy"], Popularity = 9 });
        }
        if (!db.ListEntries.Any(e => e.Id == 900))
            db.ListEntries.Add(new Core.Domain.ListEntry
            { Id = 900, ProfileId = 1, Kind = "watchlist", TitleId = 5000, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public static async Task<HttpResponseMessage> PostJson(HttpClient c, string path, object body)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        return await c.PostAsync(path, content);
    }

    /// <summary>Справжній HTTP PATCH (PostJson шле POST).</summary>
    public static Task<HttpResponseMessage> PatchJson(HttpClient c, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, path)
        { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
        return c.SendAsync(req);
    }
}

/// <summary>MD-05 через HTTP: X-Bot-Key/X-Telegram-User-Id керують усіма list-verb'ами; чужі дані недосяжні.</summary>
public sealed class BotHeaderApiTests : IClassFixture<BotApiFactory>
{
    private readonly BotApiFactory _f;
    public BotHeaderApiTests(BotApiFactory f) => _f = f;

    private async Task<int> BotProfileIdAsync(long tgId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var identity = await db.BotIdentities.FirstAsync(x => x.TelegramUserId == tgId);
        return identity.ProfileId;
    }

    [Fact]
    public async Task Wrong_Bot_Key_Is_403()
    {
        await _f.SeedAsync();
        var c = _f.BotClient(100500, key: "wrong-key");
        var r = await c.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Valid_Key_Without_Telegram_Id_Is_400()
    {
        await _f.SeedAsync();
        var c = _f.CreateDefaultClient();
        c.DefaultRequestHeaders.Add("X-Bot-Key", "test-bot-key");
        var r = await c.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Valid_Key_NonNumeric_Telegram_Id_Is_400()
    {
        await _f.SeedAsync();
        var c = _f.CreateDefaultClient();
        c.DefaultRequestHeaders.Add("X-Bot-Key", "test-bot-key");
        c.DefaultRequestHeaders.Add("X-Telegram-User-Id", "abc");
        var r = await c.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Bot_Get_Operates_On_Own_Profile_Ignoring_Client_ProfileId()
    {
        await _f.SeedAsync();
        var c = _f.BotClient(1001);
        var r = await c.GetAsync("/api/lists?profileId=1"); // profileId клієнта ігнорується
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var profileId = body.GetProperty("profileId").GetInt32();
        Assert.NotEqual(1, profileId); // не гостьовий профіль
        Assert.Equal(await BotProfileIdAsync(1001), profileId);
    }

    [Fact]
    public async Task Bot_Post_Writes_To_Own_Profile()
    {
        await _f.SeedAsync();
        var c = _f.BotClient(1002);
        var r = await BotApiFactory.PostJson(c, "/api/lists/watched",
            new { profileId = 1, titleId = 5000, rating = 8 }); // profileId в body ігнорується
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(await BotProfileIdAsync(1002), body.GetProperty("profileId").GetInt32());
    }

    [Fact]
    public async Task Different_Bot_Users_Cannot_See_Each_Others_Entries()
    {
        await _f.SeedAsync();
        var a = _f.BotClient(2001);
        var added = await BotApiFactory.PostJson(a, "/api/lists", new { kind = "watchlist", titleId = 5001 });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var itemId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // B не бачить запис A ні у списку, ні через PATCH/DELETE за відомим itemId.
        var b = _f.BotClient(2002);
        var list = await b.GetAsync("/api/lists");
        var body = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.All(body.GetProperty("items").EnumerateArray(),
            i => Assert.NotEqual(itemId, i.GetProperty("itemId").GetInt32()));

        var patch = await BotApiFactory.PatchJson(b, $"/api/lists/{itemId}", new { rating = 1 });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        var del = await b.DeleteAsync($"/api/lists/{itemId}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);

        // Гостьовий запис (profile 1, itemId 900) теж недосяжний для бота.
        var guestItem = await BotApiFactory.PatchJson(b, "/api/lists/900", new { rating = 3 });
        Assert.Equal(HttpStatusCode.NotFound, guestItem.StatusCode);
    }

    [Fact]
    public async Task Bot_Cannot_Touch_Guest_Or_Account_Entries()
    {
        await _f.SeedAsync();
        var c = _f.BotClient(3001);
        var del = await c.DeleteAsync("/api/lists/900"); // запис гостя (profile 1)
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }
}

/// <summary>Bot:ApiKey порожній → bot-header авторизацію вимкнено: будь-який X-Bot-Key → 403.</summary>
public sealed class BotDisabledKeyTests
{
    [Fact]
    public async Task Disabled_ApiKey_Makes_Any_Bot_Header_403()
    {
        await using var f = new BotApiFactory { BotKey = null };
        await f.SeedAsync();
        var c = f.BotClient(100500);
        var r = await c.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", body.GetProperty("error").GetString());
    }
}
