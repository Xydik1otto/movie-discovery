using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// MD-07: чесне здоров'я. GET /api/health — завжди 200 (liveness), але status чесний
/// («ok» лише при живій БД, інакше «degraded»); GET /api/health/ready — readiness
/// (200 при живій БД / 503 при недоступній). Очікувана форма: {status, db, ollama, titlesCount}.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _f;
    public HealthEndpointTests(ApiTestFactory f) => _f = f;

    [Fact]
    public void Status_Computation_Is_Honest()
    {
        // MD-07: одиниця чесності статусу — витягнута статична функція.
        Assert.Equal("ok", MovieDiscovery.Api.Controllers.HealthController.Status(dbOk: true));
        Assert.Equal("degraded", MovieDiscovery.Api.Controllers.HealthController.Status(dbOk: false));
    }

    [Fact]
    public async Task Health_Endpoint_200_With_Honest_Fields_When_Db_Up()
    {
        await _f.GuestClientAsync(); // одноразовий сід фабрики
        var c = _f.NewClient();
        var r = await c.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // liveness: завжди 200

        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString()); // БД жива → чесний ok
        Assert.True(body.GetProperty("db").GetBoolean());
        Assert.True(body.GetProperty("titlesCount").GetInt32() >= 2); // засіяні фікстури
        // ollama — поле присутнє і чесне (у тестовому середовищі недоступне).
        Assert.False(body.GetProperty("ollama").GetBoolean());
    }

    [Fact]
    public async Task Ready_Endpoint_200_When_Db_Up()
    {
        await _f.GuestClientAsync(); // одноразовий сід фабрики
        var c = _f.NewClient();
        var r = await c.GetAsync("/api/health/ready");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("db").GetBoolean());
    }
}
