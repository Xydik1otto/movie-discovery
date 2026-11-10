using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MovieDiscovery.Api.Auth;
using MovieDiscovery.Api.Controllers;

namespace MovieDiscovery.Tests;

/// <summary>
/// FR-PERSIST (запам'ятовування логіна між перествореннями контейнера):
/// 1) cookie-опції з РЕАЛЬНОГО Program.cs (через ApiTestFactory + IOptionsMonitor,
///    а не з копії конфіга): md_auth, HttpOnly, Lax, persistent 30 діб, sliding,
///    401/403 замість redirect;
/// 2) rememberMe у LoginRequest — опційний, дефолт false (старі клієнти не ламаються);
/// 3) стабільний ApplicationDiscriminator DataProtection і запис ключів у каталог
///    ASPNETKEYS_DIRECTORY (з тимчасовим каталогом — Windows-хост не чіпає /root);
/// 4) login з rememberMe=true видає persistent-cookie (expires ≈30 діб), без — сесійну.
/// </summary>
public class AuthRememberTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _host;

    public AuthRememberTests(ApiTestFactory host) => _host = host;

    private CookieAuthenticationOptions CookieOptions() =>
        _host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

    [Fact]
    public async Task Cookie_Options_Match_Contract_And_Keep_Api_Status_Codes()
    {
        var o = CookieOptions();

        Assert.Equal(CookieSetup.CookieName, o.Cookie.Name);
        Assert.True(o.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, o.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.None, o.Cookie.SecurePolicy);
        // persistent-cookie «запам'ятати мене» — 30 діб; sliding продовжує активні сесії
        Assert.Equal(TimeSpan.FromDays(30), o.ExpireTimeSpan);
        Assert.True(o.SlidingExpiration);

        // 401/403 замість redirect на сторінку логіна (поведінка API не змінена)
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme,
            displayName: null, handlerType: typeof(CookieAuthenticationHandler));

        var login = new RedirectContext<CookieAuthenticationOptions>(
            new DefaultHttpContext(), scheme, o, new AuthenticationProperties(), "/login");
        await o.Events.OnRedirectToLogin(login);
        Assert.Equal(StatusCodes.Status401Unauthorized, login.Response.StatusCode);

        var denied = new RedirectContext<CookieAuthenticationOptions>(
            new DefaultHttpContext(), scheme, o, new AuthenticationProperties(), "/forbidden");
        await o.Events.OnRedirectToAccessDenied(denied);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response.StatusCode);
    }

    [Fact]
    public void LoginRequest_RememberMe_Is_Optional_And_Defaults_False()
    {
        // серіалізатор — САМЕ той, що біндить тіла запитів MVC (не довільні опції)
        var serializer = _host.Services
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;

        var without = JsonSerializer.Deserialize<AuthController.LoginRequest>(
            "{\"email\":\"a@t.ua\",\"password\":\"p\"}", serializer);
        Assert.NotNull(without);
        Assert.False(without!.RememberMe);

        var with = JsonSerializer.Deserialize<AuthController.LoginRequest>(
            "{\"email\":\"a@t.ua\",\"password\":\"p\",\"rememberMe\":true}", serializer);
        Assert.NotNull(with);
        Assert.True(with!.RememberMe);
    }

    [Fact]
    public void DataProtection_ApplicationName_Wired_From_Program()
    {
        Assert.Equal(CookieSetup.ApplicationName,
            _host.Services.GetRequiredService<IOptions<DataProtectionOptions>>()
                .Value.ApplicationDiscriminator);
    }

    [Fact]
    public void DataProtection_Keys_Persist_To_Aspnetkeys_Directory()
    {
        var keysDir = Path.Combine(Path.GetTempPath(), "md-dp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keysDir);
        var prev = Environment.GetEnvironmentVariable(CookieSetup.KeysDirectoryEnv);
        Environment.SetEnvironmentVariable(CookieSetup.KeysDirectoryEnv, keysDir);
        try
        {
            using var factory = new ApiTestFactory();

            Assert.Equal(CookieSetup.ApplicationName,
                factory.Services.GetRequiredService<IOptions<DataProtectionOptions>>()
                    .Value.ApplicationDiscriminator);

            // перший захист створює ключ і ЗАПИСУЄ його в каталог (як у контейнері з volume)
            var protector = factory.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("auth-remember-probe");
            var payload = protector.Protect("probe");
            Assert.NotEqual("probe", payload);
            Assert.NotEmpty(Directory.GetFiles(keysDir, "key-*.xml"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CookieSetup.KeysDirectoryEnv, prev);
            // каталог НЕ видаляємо: паралельні тест-класи могли збудувати свій хост
            // у цьому вікні й писати ключі сюди — видалення дало б file-in-use на Windows.
        }
    }

    [Fact]
    public async Task Login_RememberMe_True_Issues_Persistent_Cookie_Session_Otherwise()
    {
        // юзер A гарантовано існує (ApiTestFactory сам реєструє/логінить) — до наших логінів
        await _host.UserClientAsync(ApiTestFactory.A);

        var persistent = _host.NewClient();
        var login1 = await ApiTestFactory.PostJson(persistent, "/api/auth/login",
            new { email = ApiTestFactory.A.Email, password = ApiTestFactory.A.Pass, rememberMe = true });
        Assert.Equal(HttpStatusCode.OK, login1.StatusCode);

        var setCookie1 = string.Join("; ", login1.Headers.GetValues("Set-Cookie"));
        Assert.Contains("md_auth", setCookie1);
        Assert.Contains("expires=", setCookie1, StringComparison.OrdinalIgnoreCase);
        var expiresValue = setCookie1.Split(';')
            .Select(s => s.Trim())
            .First(s => s.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..];
        Assert.True(DateTimeOffset.TryParse(expiresValue, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires));
        Assert.InRange((expires - DateTimeOffset.UtcNow).TotalDays, 29, 31);

        // /api/auth/me — істина з persistent-cookie
        var me1 = await persistent.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me1.StatusCode);

        var session = _host.NewClient();
        var login2 = await ApiTestFactory.PostJson(session, "/api/auth/login",
            new { email = ApiTestFactory.A.Email, password = ApiTestFactory.A.Pass });
        Assert.Equal(HttpStatusCode.OK, login2.StatusCode);

        var setCookie2 = string.Join("; ", login2.Headers.GetValues("Set-Cookie"));
        Assert.Contains("md_auth", setCookie2);
        // сесійна cookie — без Expires/Max-Age (живе до закриття браузера)
        Assert.DoesNotContain("expires=", setCookie2, StringComparison.OrdinalIgnoreCase);
        var me2 = await session.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me2.StatusCode);
    }
}
