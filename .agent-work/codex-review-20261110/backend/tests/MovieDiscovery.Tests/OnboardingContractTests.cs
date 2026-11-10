using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MovieDiscovery.Core;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// MD-02: контракт онбордингу — nullable масиви без NRE (відсутні поля в JSON → 200, не 500),
/// серверна валідація (favoriteTitles ≤ 3 і > 0, picks/mood/era ≤ 20 непорожніх рядків),
/// форма відповіді {profileUpdated, onboardingDone}. WebApplicationFactory: реальний binder.
/// </summary>
public sealed class OnboardingContractTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;
    public OnboardingContractTests(ApiTestFactory f) => _f = f;

    private Task<HttpClient> UserAsync() => _f.UserClientAsync(ApiTestFactory.A);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string json)
    {
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await c.PostAsync("/api/onboarding/answers", content);
    }

    private static string Arr(int n, string prefix = "g") =>
        "[" + string.Join(",", Enumerable.Range(1, n).Select(i => $"\"{prefix}{i}\"")) + "]";

    [Fact]
    public async Task Missing_Arrays_Are_NullSafe_Not_500()
    {
        var c = await UserAsync();
        // Критичний регрес: JSON без favoriteTitles раніше падав NRE → 500.
        var r = await PostAsync(c, "{}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("profileUpdated").GetBoolean());
        Assert.True(body.GetProperty("onboardingDone").GetBoolean());
    }

    [Fact]
    public async Task Missing_Some_Arrays_Still_Ok()
    {
        var c = await UserAsync();
        var r = await PostAsync(c, """{"picks":["action"],"era":null}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Valid_Minimal_Payload_Passes()
    {
        var c = await UserAsync();
        var r = await PostAsync(c,
            """{"picks":["action","comedy"],"mood":["dark"],"era":["1990s"],"favoriteTitles":[7],"skipped":false}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("profileUpdated").GetBoolean());
    }

    [Fact]
    public async Task More_Than_3_FavoriteTitles_Is_400()
    {
        var c = await UserAsync();
        var r = await PostAsync(c, """{"favoriteTitles":[1,2,3,4]}""");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData("[0]")]
    [InlineData("[-5]")]
    public async Task Non_Positive_FavoriteTitles_Are_400(string favorites)
    {
        var c = await UserAsync();
        var r = await PostAsync(c, $$"""{"favoriteTitles":{{favorites}}}""");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Duplicate_FavoriteTitles_Are_Deduped()
    {
        var c = await UserAsync();
        // 5 значень, але 3 унікальних → після дедуплікації валідно (детермінована поведінка).
        var r = await PostAsync(c, """{"favoriteTitles":[7,7,8,8,9]}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task More_Than_20_Picks_Are_400()
    {
        var c = await UserAsync();
        var r = await PostAsync(c, $$"""{"picks":{{Arr(21)}}}""");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Twenty_Picks_Are_Ok()
    {
        var c = await UserAsync();
        var r = await PostAsync(c, $$"""{"picks":{{Arr(20)}}}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Theory]
    [InlineData("""{"picks":["  "]}""")]
    [InlineData("""{"mood":[""]}""")]
    [InlineData("""{"era":["ok","   "]}""")]
    public async Task Blank_Array_Values_Are_400(string json)
    {
        var c = await UserAsync();
        var r = await PostAsync(c, json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData("""{"picks":{"a":1}}""")]
    [InlineData("""{"era":"1990s"}""")]
    [InlineData("""{"favoriteTitles":"7"}""")]
    public async Task Wrong_JSON_Shape_Is_400_By_Binder(string json)
    {
        var c = await UserAsync();
        // [ApiController]: об'єкт/скаляр замість масиву → автоматичний 400 (контракт збережено).
        var r = await PostAsync(c, json);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Anonymous_Is_401()
    {
        var c = await _f.GuestClientAsync();
        var r = await PostAsync(c, "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
