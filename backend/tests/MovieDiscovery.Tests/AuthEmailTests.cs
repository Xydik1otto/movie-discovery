using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Api.Auth;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services.Emailing;

namespace MovieDiscovery.Tests;

/// <summary>
/// Auth-email (10.11): підтвердження пошти, resend, forgot/reset password, антиенумерація, ліміти.
/// КОЖЕН тест — власний WebApplicationFactory (як у ImportProfileTests): власна InMemory-БД,
/// власні бакети rate-limiter'ів (register 3/год/IP не заважає між тестами), власний fake-збирач листів.
/// IEmailSender підмінюється fake-збирачем ПІСЛЯ AddEmailing (RemoveAll + Add — останній виграє),
/// тому тести не залежать від того, чи оркестратор уже додав AddEmailing у Program.cs.
/// </summary>
public class AuthEmailTests
{
    // ─── (a) register надсилає лист з лінком підтвердження (userId + url-encoded токен) ───
    [Fact]
    public async Task Register_Sends_Confirmation_Email_With_Link_And_UserId()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        var body = await RegisterAsync(client, "confirm-me@t.ua", "confirmUser1", "Passw0rdX");
        var userId = body.GetProperty("userId").GetString();
        Assert.False(string.IsNullOrEmpty(userId));
        Assert.True(body.GetProperty("emailConfirmationRequired").GetBoolean());

        var mail = Assert.Single(factory.Emails.Sent);
        Assert.Equal("confirm-me@t.ua", mail.To);
        Assert.Contains("підтвердіть", mail.Subject, StringComparison.OrdinalIgnoreCase);
        // лінк на фронт з userId і НЕПОРОЖНІМ url-encoded токеном
        Assert.Contains($"/confirm-email?userId={userId}", mail.Html);
        var (_, token) = Extract(mail, "confirm-email");
        Assert.False(string.IsNullOrWhiteSpace(token));
        // токен url-encoded: жодних сирих '+'/'/'/'=' в query (base64-токен закодовано)
        Assert.DoesNotContain("token=+", mail.Html);

        // у режимі гейту авто-сесії немає: register-cookie не обходить 403 логіна
        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // ─── (c) логін НЕпідтвердженого → 403 email_not_confirmed ───
    [Fact]
    public async Task Login_Before_Confirmation_Returns_403_EmailNotConfirmed()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        await RegisterAsync(client, "unconfirmed@t.ua", "unconfUser1", "Passw0rdX");

        var login = await ApiTestFactory.PostJson(client, "/api/auth/login",
            new { email = "unconfirmed@t.ua", password = "Passw0rdX" });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        var body = await ParseAsync(login);
        Assert.Equal("email_not_confirmed", body.GetProperty("error").GetString());
    }

    // ─── (b) валідний токен → EmailConfirmed=true, логін 200 ───
    [Fact]
    public async Task Confirm_With_Valid_Token_Confirms_And_Allows_Login()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        var body = await RegisterAsync(client, "valid@t.ua", "validUser1", "Passw0rdX");
        var userId = body.GetProperty("userId").GetString()!;
        var (_, token) = Extract(Assert.Single(factory.Emails.Sent), "confirm-email");

        var confirm = await ApiTestFactory.PostJson(client, "/api/auth/confirm-email",
            new { userId, token });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.True((await ParseAsync(confirm)).GetProperty("confirmed").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        Assert.True(db.Users.First(u => u.Id == userId).EmailConfirmed);

        var login = await ApiTestFactory.PostJson(client, "/api/auth/login",
            new { email = "valid@t.ua", password = "Passw0rdX" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ─── (d) поганий токен → 400 invalid_token, юзер лишається непідтвердженим ───
    [Fact]
    public async Task Confirm_With_Bad_Token_Returns_400_InvalidToken()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        var body = await RegisterAsync(client, "badtoken@t.ua", "badtokUser1", "Passw0rdX");
        var userId = body.GetProperty("userId").GetString()!;

        var confirm = await ApiTestFactory.PostJson(client, "/api/auth/confirm-email",
            new { userId, token = "bogus-token" });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal("invalid_token", (await ParseAsync(confirm)).GetProperty("error").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        Assert.False(db.Users.First(u => u.Id == userId).EmailConfirmed);

        var login = await ApiTestFactory.PostJson(client, "/api/auth/login",
            new { email = "badtoken@t.ua", password = "Passw0rdX" });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    // ─── (e) resend понад ліміт 3/год на (email+IP) → 429 ───
    [Fact]
    public async Task Resend_Exceeding_Rate_Limit_Returns_429()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var ok = await ApiTestFactory.PostJson(client, "/api/auth/resend-confirmation",
                new { email = "spamme@t.ua" });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.True((await ParseAsync(ok)).GetProperty("sent").GetBoolean());
        }

        var limited = await ApiTestFactory.PostJson(client, "/api/auth/resend-confirmation",
            new { email = "spamme@t.ua" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var limitedBody = await ParseAsync(limited);
        Assert.Equal("rate_limited", limitedBody.GetProperty("error").GetString());
        Assert.True(limitedBody.GetProperty("retryAfterMinutes").GetInt32() >= 1);

        // бакет на (email+IP): ІНШИЙ email у тому самому запиті — не обмежений
        var other = await ApiTestFactory.PostJson(client, "/api/auth/resend-confirmation",
            new { email = "other@t.ua" });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // ─── (f) forgot-password для невідомого email → 200 {sent:true}, нічого не надіслано ───
    [Fact]
    public async Task Forgot_Unknown_Email_Is_Silent_Success_Without_Email()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        var resp = await ApiTestFactory.PostJson(client, "/api/auth/forgot-password",
            new { email = "ghost@t.ua" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True((await ParseAsync(resp)).GetProperty("sent").GetBoolean());
        Assert.Empty(factory.Emails.Sent); // антиенумерація: жодного листа, жодного витоку
    }

    // ─── (g) reset валідним токеном → новий пароль логіниться, старий 401 ───
    [Fact]
    public async Task Reset_With_Valid_Token_Logs_In_With_New_Password_Old_Is_401()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        var body = await RegisterAsync(client, "reset@t.ua", "resetUser1", "OldPassw0rd1");
        var userId = body.GetProperty("userId").GetString()!;
        var (_, confirmToken) = Extract(Assert.Single(factory.Emails.Sent), "confirm-email");
        Assert.Equal(HttpStatusCode.OK, (await ApiTestFactory.PostJson(client, "/api/auth/confirm-email",
            new { userId, token = confirmToken })).StatusCode);

        var forgot = await ApiTestFactory.PostJson(client, "/api/auth/forgot-password",
            new { email = "reset@t.ua" });
        Assert.Equal(HttpStatusCode.OK, forgot.StatusCode);
        Assert.Equal(2, factory.Emails.Sent.Count); // другий лист — про відновлення пароля
        Assert.Contains("відновлення", factory.Emails.Sent[1].Subject, StringComparison.OrdinalIgnoreCase);
        var (_, resetToken) = Extract(factory.Emails.Sent[1], "reset-password");

        var reset = await ApiTestFactory.PostJson(client, "/api/auth/reset-password",
            new { userId, token = resetToken, newPassword = "NewPassw0rd2" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True((await ParseAsync(reset)).GetProperty("reset").GetBoolean());

        var loginNew = await ApiTestFactory.PostJson(client, "/api/auth/login",
            new { email = "reset@t.ua", password = "NewPassw0rd2" });
        Assert.Equal(HttpStatusCode.OK, loginNew.StatusCode);

        var loginOld = await ApiTestFactory.PostJson(client, "/api/auth/login",
            new { email = "reset@t.ua", password = "OldPassw0rd1" });
        Assert.Equal(HttpStatusCode.Unauthorized, loginOld.StatusCode);
    }

    // ─── (h) reset з поганим токеном → 400 invalid_token ───
    [Fact]
    public async Task Reset_With_Bad_Token_Returns_400_InvalidToken()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        var body = await RegisterAsync(client, "badreset@t.ua", "badresetUser1", "Passw0rdX");
        var userId = body.GetProperty("userId").GetString()!;
        await ApiTestFactory.PostJson(client, "/api/auth/forgot-password", new { email = "badreset@t.ua" });

        var reset = await ApiTestFactory.PostJson(client, "/api/auth/reset-password",
            new { userId, token = "bogus-reset-token", newPassword = "NewPassw0rd2" });
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Equal("invalid_token", (await ParseAsync(reset)).GetProperty("error").GetString());
    }

    // ─── валідація newPassword — Identity-політика, формат errors як у register ───
    [Fact]
    public async Task Reset_With_Weak_Password_Returns_400_Validation_Shaped_Like_Register()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: true);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        var body = await RegisterAsync(client, "weakpw@t.ua", "weakpwUser1", "Passw0rdX");
        var userId = body.GetProperty("userId").GetString()!;
        await ApiTestFactory.PostJson(client, "/api/auth/forgot-password", new { email = "weakpw@t.ua" });
        var (_, resetToken) = Extract(factory.Emails.Sent[1], "reset-password");

        var reset = await ApiTestFactory.PostJson(client, "/api/auth/reset-password",
            new { userId, token = resetToken, newPassword = "short1A" }); // 7 символів
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        var parsed = await ParseAsync(reset);
        Assert.Equal("validation", parsed.GetProperty("error").GetString());
        Assert.True(parsed.GetProperty("errors").TryGetProperty("password", out _));
    }

    // ─── compat-пін: MAIL_ENABLED=false → авто-підтвердження + LoggingEmailSender (лінк у лог) ───
    // Саме ця поведінка тримає наявні фіксстури (AuthRememberTests та ін.) зеленими без правок.
    [Fact]
    public async Task Register_With_Mail_Disabled_Autoconfirms_And_Still_Emails_Link_To_Log()
    {
        using var factory = new AuthEmailTestFactory(mailEnabled: false);
        await factory.SeedAsync();
        var client = factory.CreateClient();

        var body = await RegisterAsync(client, "devmode@t.ua", "devmodeUser1", "Passw0rdX");
        var userId = body.GetProperty("userId").GetString()!;
        Assert.False(body.GetProperty("emailConfirmationRequired").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        Assert.True(db.Users.First(u => u.Id == userId).EmailConfirmed);

        // лист «надіслано» (у лог) — чесний dev-режим, не тихо
        var mail = Assert.Single(factory.Emails.Sent);
        Assert.Contains("/confirm-email?userId=", mail.Html);

        // логін працює одразу (гейту немає)
        var login = await ApiTestFactory.PostJson(client, "/api/auth/login",
            new { email = "devmode@t.ua", password = "Passw0rdX" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    // ─── помічники ───

    private static async Task<JsonElement> RegisterAsync(HttpClient client, string email, string username, string password)
    {
        var resp = await ApiTestFactory.PostJson(client, "/api/auth/register", new { email, username, password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await ParseAsync(resp);
    }

    private static async Task<JsonElement> ParseAsync(HttpResponseMessage resp)
    {
        var content = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private static (string UserId, string Token) Extract(SentEmail mail, string page)
    {
        // лінк вигляду https://kino.test/confirm-email?userId=..&token=..
        var match = Regex.Match(
            mail.Html,
            $"""https?://[^"<\s]+/{page}\?userId=([^"&<\s]+)&token=([^"&<\s]+)""",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"лінк /{page} відсутній у листі:\n{mail.Html}");
        return (Uri.UnescapeDataString(match.Groups[1].Value), Uri.UnescapeDataString(match.Groups[2].Value));
    }
}

/// <summary>Зібраний fake-лист.</summary>
public sealed class SentEmail
{
    public string To { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Html { get; init; } = "";
}

/// <summary>Fake IEmailSender — збирає листи в пам'ять для перевірок.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public List<SentEmail> Sent { get; } = new();

    public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        Sent.Add(new SentEmail { To = to, Subject = subject, Html = htmlBody });
        return Task.CompletedTask;
    }
}

/// <summary>
/// Локальний WebApplicationFactory для auth-email тестів: InMemory-БД + MAIL_ENABLED за
/// параметром + FakeEmailSender замість SMTP. AddEmailing викликається тут явно, тож
/// токен-провайдери/опції зведені навіть якщо Program.cs ще не має виклику від оркестратора.
/// </summary>
public sealed class AuthEmailTestFactory(bool mailEnabled = true) : WebApplicationFactory<Program>
{
    public FakeEmailSender Emails { get; } = new();

    // ІМ'Я БД — властивість (одна оцінка): NewGuid ВСЕРЕДИНІ options-ламбди дає
    // НОВУ InMemory-БД на кожен DI-scope (options створюються per-scope) —
    // тоді confirm у наступному запиті не бачить юзера з реєстрації.
    private string DbName { get; } = $"auth-email-{Guid.NewGuid():N}";

    public async Task SeedAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        db.Profiles.Add(new Core.Domain.Profile { Id = 1, Name = "Guest", IsDefault = true });
        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // серверний стек трейсу 5xx у тестовий вивід (xunit перехоплює Console)
        builder.ConfigureLogging(lb => lb.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));
        var emailConfig = new Dictionary<string, string?>
        {
            // явні значення в конфізі — старші за EmailEnvDefaults (машинні env не протікають у тести)
            ["Email:Enabled"] = mailEnabled ? "true" : "false",
            ["Email:FrontPublicUrl"] = "https://kino.test",
        };
        if (mailEnabled)
            emailConfig["Email:RequireConfirmedEmail"] = "true"; // гейт логіна як у prod (MAIL_ENABLED=true)

        var config = new ConfigurationBuilder().AddInMemoryCollection(emailConfig).Build();
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
            services.AddDbContext<MovieDiscoveryDbContext>(o =>
                o.UseInMemoryDatabase(DbName));
            services.AddEmailing(config);
            // fake-збирач листів замість SMTP/Logging (остання реєстрація виграє)
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }
}
