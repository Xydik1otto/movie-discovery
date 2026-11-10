using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MovieDiscovery.Api;

namespace MovieDiscovery.Tests;

/// <summary>
/// Спільний AdminGate.Verify (колишні дублі CheckAdmin в AdminController/TrailersController).
/// Контракт 1:1 з колишньою логікою: порожній/відсутній ADMIN_KEY → 500 admin_not_configured
/// (fail-closed), невірний/відсутній заголовок → 401 unauthorized (ordinal-порівняння),
/// валідний ключ → null (доступ дозволено).
/// </summary>
public class AdminGateTests
{
    private static (IActionResult? Result, string BodyJson) Verify(string? configKey, string? headerValue)
    {
        var http = new DefaultHttpContext();
        if (headerValue is not null)
            http.Request.Headers["X-Admin-Key"] = headerValue;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:Key"] = configKey })
            .Build();

        var result = AdminGate.Verify(http, configuration);
        var json = result is ObjectResult objectResult
            ? JsonSerializer.Serialize(objectResult.Value)
            : "";
        return (result, json);
    }

    [Fact]
    public void EmptyConfigKey_Returns500_AdminNotConfigured()
    {
        var (result, json) = Verify(configKey: "", headerValue: "anything");

        var status = Assert.IsType<ObjectResult>(result).StatusCode;
        Assert.Equal(500, status);
        Assert.Contains("admin_not_configured", json);
    }

    [Fact]
    public void MissingConfigKey_Returns500_AdminNotConfigured()
    {
        var (result, json) = Verify(configKey: null, headerValue: "anything");

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("admin_not_configured", json);
    }

    [Fact]
    public void EmptyConfigKey_FailsClosed_EvenWithAnyHeader()
    {
        // Fail-closed: поки ключ не налаштовано, ЖОДЕН заголовок не проходить (не 401 «не вгадав», а 500).
        var (result, json) = Verify(configKey: "", headerValue: "");

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("admin_not_configured", json);
    }

    [Fact]
    public void ValidKey_MatchingHeader_ReturnsNull()
    {
        var (result, _) = Verify(configKey: "s3cret", headerValue: "s3cret");

        Assert.Null(result);
    }

    [Fact]
    public void ValidKey_WrongHeader_Returns401_Unauthorized()
    {
        var (result, json) = Verify(configKey: "s3cret", headerValue: "wrong");

        Assert.Equal(401, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("unauthorized", json);
    }

    [Fact]
    public void ValidKey_MissingHeader_Returns401_Unauthorized()
    {
        var (result, json) = Verify(configKey: "s3cret", headerValue: null);

        Assert.Equal(401, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("unauthorized", json);
    }

    [Fact]
    public void ValidKey_EmptyHeader_Returns401_Unauthorized()
    {
        var (result, json) = Verify(configKey: "s3cret", headerValue: "");

        Assert.Equal(401, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Contains("unauthorized", json);
    }

    [Fact]
    public void KeyComparison_IsOrdinalCaseSensitive()
    {
        // «SECRET» ≠ «secret»: колишня логіка — StringComparison.Ordinal; поведінка не змінювалась.
        var (result, _) = Verify(configKey: "s3cret", headerValue: "S3CRET");

        Assert.Equal(401, Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
