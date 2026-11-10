using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Tests;

/// <summary>
/// MD-01 (P0): матриця ownership списків/профілів — guest, A, B, anonymous;
/// IDOR на всіх verbs (GET/POST/PATCH/DELETE) за відомими чужими ID.
/// </summary>
public sealed class ListsOwnershipTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;

    public ListsOwnershipTests(ApiTestFactory f) => _f = f;

    // Стабільні itemId фікстур: гість → 900, A → 901, B → 902 (див. фабрику).
    private const int GuestItemId = 900;
    private const int AItemId = 901;
    private const int BItemId = 902;

    private Task<HttpClient> GuestAsync() => _f.GuestClientAsync();
    private Task<HttpClient> AAsync() => _f.UserClientAsync(ApiTestFactory.A);
    private Task<HttpClient> BAsync() => _f.UserClientAsync(ApiTestFactory.B);

    private int ProfileIdOf(string username)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var identityId = db.Users.First(x => x.UserName == username).Id;
        return db.UserProfiles.First(x => x.UserId == identityId).ProfileId;
    }

    // ─── GET ───

    [Fact]
    public async Task Anonymous_Get_Default_Guest_Lists_Only()
    {
        var c = await GuestAsync();
        var r = await c.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("profileId").GetInt32());
        var items = body.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1);
        // Порядок — за createdAt desc; інші тести додають записи до guest-профілю,
        // тому перевіряємо НАЯВНІСТЬ фіксурного запису 900, а не позицію [0].
        Assert.Contains(items.EnumerateArray(),
            i => i.GetProperty("itemId").GetInt32() == 900);
    }

    [Fact]
    public async Task Anonymous_Get_Foreign_ProfileId_Is_403_No_Leak()
    {
        var c = await GuestAsync();
        // Відомий чужий ID профілю B — 403 forbidden (MD-01: існуючий не-гостьовий профіль),
        // без елементів B у відповіді.
        var r = await c.GetAsync($"/api/lists?profileId={ProfileIdOf(ApiTestFactory.B.User)}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(BItemId.ToString(), text); // itemId B не просочується
        Assert.DoesNotContain("Beta", text);
    }

    [Fact]
    public async Task Anonymous_Get_Unknown_ProfileId_Is_404()
    {
        var c = await GuestAsync();
        var r = await c.GetAsync("/api/lists?profileId=999999");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Get_Guest_ProfileId_Explicit_Is_Ok()
    {
        var c = await GuestAsync();
        var r = await c.GetAsync("/api/lists?profileId=1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // ─── POST ───

    [Fact]
    public async Task Anonymous_Post_Into_Foreign_Profile_Is_403()
    {
        var c = await GuestAsync();
        var r = await ApiTestFactory.PostJson(c, "/api/lists",
            new { profileId = ProfileIdOf(ApiTestFactory.B.User), kind = "watchlist", titleId = 5000 });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Post_Default_Lands_In_Guest_Profile()
    {
        var c = await GuestAsync();
        var r = await ApiTestFactory.PostJson(c, "/api/lists",
            new { kind = "watchlist", titleId = 5001 });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("profileId").GetInt32());
    }

    // ─── PATCH/DELETE (IDOR за itemId) ───

    [Fact]
    public async Task Anonymous_Patch_Foreign_Item_Is_404_And_Untouched()
    {
        var c = await GuestAsync();
        var r = await ApiTestFactory.PatchJson(c, $"/api/lists/{BItemId}", new { rating = 1 });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        // Дані B не змінились.
        Assert.Equal(7, RatingOf(BItemId));
    }

    [Fact]
    public async Task Anonymous_Delete_Foreign_Item_Is_404_And_Untouched()
    {
        var c = await GuestAsync();
        var r = await c.DeleteAsync($"/api/lists/{BItemId}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.True(EntryExists(BItemId));
    }

    [Fact]
    public async Task Anonymous_Can_Patch_And_Delete_Own_Guest_Item()
    {
        var c = await GuestAsync();
        // Створюємо ВЛАСНИЙ запис (фікстурні 900/903 не споживати; kind=liked — унікальний для guest).
        var created = await ApiTestFactory.PostJson(c, "/api/lists", new { kind = "liked", titleId = 5000 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var patch = await ApiTestFactory.PatchJson(c, $"/api/lists/{itemId}", new { rating = 5 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var del = await c.DeleteAsync($"/api/lists/{itemId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
    }

    // ─── Акаунти: ізоляція і власні операції ───

    [Fact]
    public async Task User_A_Sees_Only_Own_Lists_ProfileId_Ignored()
    {
        var c = await AAsync();
        var r = await c.GetAsync($"/api/lists?profileId={ProfileIdOf(ApiTestFactory.B.User)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProfileIdOf(ApiTestFactory.A.User), body.GetProperty("profileId").GetInt32());
        var items = body.GetProperty("items");
        Assert.All(items.EnumerateArray(),
            i => Assert.NotEqual(BItemId, i.GetProperty("itemId").GetInt32()));
        Assert.Contains(items.EnumerateArray(), i => i.GetProperty("itemId").GetInt32() == AItemId);
    }

    [Fact]
    public async Task User_A_Cannot_Patch_Or_Delete_B_Items()
    {
        var c = await AAsync();
        var patch = await ApiTestFactory.PatchJson(c, $"/api/lists/{BItemId}", new { rating = 1 });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        var del = await c.DeleteAsync($"/api/lists/{BItemId}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
        Assert.Equal(7, RatingOf(BItemId));
    }

    [Fact]
    public async Task User_A_Can_Patch_Own_Item()
    {
        var c = await AAsync();
        var patch = await ApiTestFactory.PatchJson(c, $"/api/lists/{AItemId}", new { rating = 9 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
    }

    [Fact]
    public async Task User_A_Post_With_Foreign_ProfileId_Writes_To_Own_Profile()
    {
        var c = await AAsync();
        var r = await ApiTestFactory.PostJson(c, "/api/lists/liked",
            new { profileId = ProfileIdOf(ApiTestFactory.B.User), titleId = 5001, rating = 8 });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var entry = db.ListEntries.Where(e => e.TitleId == 5001 && e.Kind == "liked")
            .OrderBy(e => e.Id).Last();
        Assert.Equal(ProfileIdOf(ApiTestFactory.A.User), entry.ProfileId); // власний профіль A, не B
    }

    // ─── /api/profiles ───

    [Fact]
    public async Task Profiles_Anonymous_Get_Is_401_No_Account_Leak()
    {
        var c = await GuestAsync();
        var r = await c.GetAsync("/api/profiles");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("A\"", text);
    }

    [Fact]
    public async Task Profiles_Anonymous_Post_Is_401()
    {
        var c = await GuestAsync();
        var r = await ApiTestFactory.PostJson(c, "/api/profiles", new { name = "evil" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Profiles_User_Sees_Only_Own()
    {
        var c = await AAsync();
        var r = await c.GetAsync("/api/profiles");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal(ProfileIdOf(ApiTestFactory.A.User), body[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Profiles_User_Second_Create_Is_409()
    {
        var c = await AAsync();
        var r = await ApiTestFactory.PostJson(c, "/api/profiles", new { name = "other" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    // ─── Legacy guest profile, приріплений до акаунта → гостьовий серверний шлях вимкнено ───

    [Fact]
    public async Task Guest_Surface_Disabled_When_Legacy_Profile_Linked_To_Account()
    {
        // Окрема фабрика: линк carol→profile 1 не має отруювати спільний фіксчер класу.
        using var f = new ApiTestFactory();
        var carol = await f.GuestLinkedLegacyAsync(); // реєстрація авто-логінить carol (cookie)
        var anon = f.NewClient();                     // саме АНОНІМНИЙ клієнт перевіряє guest-поверхню

        var get = await anon.GetAsync("/api/lists");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        var post = await ApiTestFactory.PostJson(anon, "/api/lists", new { kind = "watchlist", titleId = 5000 });
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        var patch = await ApiTestFactory.PatchJson(anon, $"/api/lists/{GuestItemId}", new { rating = 3 });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
    }

    // ─── helpers ───

    private int? RatingOf(int itemId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        return db.ListEntries.FirstOrDefault(e => e.Id == itemId)?.Rating;
    }

    private bool EntryExists(int itemId)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        return db.ListEntries.Any(e => e.Id == itemId);
    }
}
