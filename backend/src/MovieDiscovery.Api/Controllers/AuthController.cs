using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MovieDiscovery.Api.Auth;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Services.Emailing;
using IEmailSender = MovieDiscovery.Infrastructure.Services.Emailing.IEmailSender;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// Акаунти (FR-16): register/login/logout/me; auth-email (10.11): confirm-email,
/// resend-confirmation, forgot-password, reset-password. Cookie HttpOnly SameSite=Lax.
/// Антиенумерація: resend/forgot завжди 200 {sent:true}; логін непідтвердженого — 403
/// email_not_confirmed лише з ВІРНИМ паролем (після CheckPassword).
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> users,
    TasteProfileService taste,
    ILogger<AuthController> logger) : ControllerBase
{
    // ВАЖЛИВО: атрибути валідації — на ПАРАМЕТРИ конструктора record'а (без [property:]):
    // MVC-валідація для record'ів читає тільки параметри; [property:...] дає
    // InvalidOperationException "validation metadata must be associated with the constructor parameter".
    public sealed record RegisterRequest(
        [Required, EmailAddress] string Email,
        [Required, MinLength(3)] string Username,
        [Required, MinLength(8)] string Password);

    // FR-PERSIST: RememberMe опційний (дефолт false) — старі клієнти та фіксстури,
    // що шлють лише {email, password}, не ламаються; true → persistent-cookie 30 діб.
    public sealed record LoginRequest([Required] string Email, [Required] string Password, bool RememberMe = false);

    public sealed record ConfirmEmailRequest([Required] string UserId, [Required] string Token);
    public sealed record ResendConfirmationRequest([Required, EmailAddress] string Email);
    public sealed record ForgotPasswordRequest([Required, EmailAddress] string Email);
    // newPassword валідує Identity (та сама політика, що register: ≥8 + цифра + велика).
    public sealed record ResetPasswordRequest([Required] string UserId, [Required] string Token, [Required] string NewPassword);

    /// <summary>200 {userId, emailConfirmationRequired} | 400 {errors:{поле:[помилки Identity]}}.</summary>
    [HttpPost("register")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> Register(RegisterRequest req, CancellationToken ct)
    {
        var user = new ApplicationUser
        {
            UserName = req.Username.Trim(),
            Email = req.Email.Trim(),
        };
        var result = await users.CreateAsync(user, req.Password);
        if (!result.Succeeded)
            return BadRequest(new
            {
                error = "validation_failed",
                errors = GroupErrors(result.Errors),
            });

        // зв'язка юзера з постійним профілем списків (FR-16)
        await taste.ResolveAsync(user.Id, ct);

        var options = MailOptions;
        var enforceConfirmation = options.EffectiveRequireConfirmedEmail;
        // MAIL_ENABLED=false (dev/тести без SMTP): лист все одно «йде» (LoggingEmailSender —
        // лінк у логах), а юзер авто-підтверджується, щоб локальний онбординг не глухнув.
        var autoConfirm = !options.Enabled && !enforceConfirmation;

        EnsureTokenProviders();
        var confirmToken = await users.GenerateEmailConfirmationTokenAsync(user);
        var (subject, html) = AuthEmails.Confirmation(
            AuthEmails.ConfirmationLink(options.FrontPublicUrl, user.Id, confirmToken));
        await TrySendAsync(user.Email ?? req.Email, subject, html, ct);

        if (autoConfirm)
        {
            user.EmailConfirmed = true;
            await users.UpdateAsync(user);
            logger.LogInformation(
                "MAIL_ENABLED=false → авто-підтверджено пошту {Email} (лінк підтвердження у логах вище)", user.Email);
        }

        // Гейт підтвердження: без авто-сесії (інакше register-cookie обійшов би 403 логіна).
        if (!enforceConfirmation)
            await SignInCookieAsync(user);

        return Ok(new
        {
            userId = user.Id,
            email = user.Email,
            username = user.UserName,
            // чесне значення: front показує екран «перевірте пошту» лише коли логін реально гейтований
            emailConfirmationRequired = enforceConfirmation,
        });
    }

    /// <summary>200 {user} | 401 generic («невірний email або пароль») | 403 email_not_confirmed | 423 lockedout.</summary>
    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login(LoginRequest req, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        // generic 401 без розкриття існування акаунта (FR-16)
        if (user is null)
            return Unauthorized(GenericError);
        if (await users.IsLockedOutAsync(user))
            return StatusCode(423, new { error = "locked_out", message = "Акаунт тимчасово заблоковано (5 невдалих спроб). Спробуйте за 15 хвилин." });

        if (!await users.CheckPasswordAsync(user, req.Password))
        {
            await users.AccessFailedAsync(user);
            if (await users.IsLockedOutAsync(user))
                return StatusCode(423, new { error = "locked_out", message = "Акаунт тимчасово заблоковано (5 невдалих спроб). Спробуйте за 15 хвилин." });
            return Unauthorized(GenericError);
        }

        await users.ResetAccessFailedCountAsync(user);

        // Гейт підтвердження ПІСЛЯ перевірки пароля: 403 лише з валідними creds
        // (невірний пароль лишається generic 401 — існування акаунта не розкривається).
        if (MailOptions.EffectiveRequireConfirmedEmail && !user.EmailConfirmed)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "email_not_confirmed",
                message = "Підтвердіть email, щоб увійти — ми надіслали листа з лінком.",
            });

        // FR-PERSIST: rememberMe → persistent-cookie (ExpireTimeSpan 30 діб у Program.cs);
        // без rememberMe — сесійна cookie, як раніше.
        await SignInCookieAsync(user, req.RememberMe);

        var up = await taste.ResolveAsync(user.Id, ct);
        return Ok(new
        {
            user = new
            {
                id = user.Id, email = user.Email, username = user.UserName,
                onboardingDone = up.OnboardingDone,
            },
        });
    }

    /// <summary>200 {confirmed:true}; невідомий userId/невалідний або прострочений токен → 400 {error:"invalid_token"}.</summary>
    [HttpPost("confirm-email")]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest req, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(req.UserId);
        if (user is null)
            return BadRequest(new { error = "invalid_token" }); // без розкриття існування userId
        if (user.EmailConfirmed)
            return Ok(new { confirmed = true }); // ідемпотентно (повторний клік по лінку)

        EnsureTokenProviders();
        var result = await users.ConfirmEmailAsync(user, req.Token);
        if (!result.Succeeded)
        {
            // невдалі підтвердження — потенційний зловживаний перебір токенів: лог на рівні Warning
            logger.LogWarning("confirm-email відхилено для {UserId}: {Codes}",
                req.UserId, string.Join(',', result.Errors.Select(e => e.Code)));
            return BadRequest(new { error = "invalid_token" });
        }
        return Ok(new { confirmed = true });
    }

    /// <summary>ЗАВЖДИ 200 {sent:true} (антиенумерація); перевищення ліміту → 429 {error:"rate_limited"}.</summary>
    [HttpPost("resend-confirmation")]
    public async Task<IActionResult> ResendConfirmation(ResendConfirmationRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim();
        if (!Limiter.TryAcquire("resend", email, ClientIp, out var retryAfterMinutes))
            return RateLimited(retryAfterMinutes);

        var user = await users.FindByEmailAsync(email);
        // Silent: невідомий email або вже підтверджений — виглядає як успіх (антиенумерація).
        if (user is null || user.EmailConfirmed)
            return Ok(new { sent = true });

        EnsureTokenProviders();
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var (subject, html) = AuthEmails.Confirmation(
            AuthEmails.ConfirmationLink(MailOptions.FrontPublicUrl, user.Id, token));
        await TrySendAsync(user.Email ?? email, subject, html, ct);
        return Ok(new { sent = true });
    }

    /// <summary>ЗАВЖДИ 200 {sent:true} (антиенумерація); перевищення ліміту → 429 {error:"rate_limited"}.</summary>
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest req, CancellationToken ct)
    {
        var email = req.Email.Trim();
        if (!Limiter.TryAcquire("forgot", email, ClientIp, out var retryAfterMinutes))
            return RateLimited(retryAfterMinutes);

        var user = await users.FindByEmailAsync(email);
        if (user is null)
            return Ok(new { sent = true }); // Silent (антиенумерація)

        EnsureTokenProviders();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var (subject, html) = AuthEmails.Reset(
            AuthEmails.ResetLink(MailOptions.FrontPublicUrl, user.Id, token));
        await TrySendAsync(user.Email ?? email, subject, html, ct);
        return Ok(new { sent = true });
    }

    /// <summary>200 {reset:true} | 400 {error:"invalid_token"} | 400 {error:"validation", errors:{...}}.</summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest req, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(req.UserId);
        if (user is null)
            return BadRequest(new { error = "invalid_token" }); // без розкриття існування userId

        EnsureTokenProviders();
        var result = await users.ResetPasswordAsync(user, req.Token, req.NewPassword);
        if (result.Succeeded)
        {
            // скидання пароля доводить володіння поштою — заодно підтверджує її
            // (розблокує loop «забув пароль → не можу залогінитись, бо не підтвердив»).
            if (!user.EmailConfirmed)
            {
                user.EmailConfirmed = true;
                await users.UpdateAsync(user);
            }
            return Ok(new { reset = true });
        }

        if (result.Errors.Any(e => e.Code is "InvalidToken" or "SecurityTokenExpired"))
            return BadRequest(new { error = "invalid_token" });
        return BadRequest(new
        {
            error = "validation",
            errors = GroupErrors(result.Errors),
        });
    }

    /// <summary>Вихід (cookie очищається); без сесії — ідемпотентний no-op.</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { ok = true });
    }

    /// <summary>200 {id, email, username, onboardingDone} | 401.</summary>
    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(GenericError);
        var user = await users.FindByIdAsync(userId);
        if (user is null)
            return Unauthorized(GenericError);
        var up = await taste.ResolveAsync(userId, ct);
        return Ok(new
        {
            id = user.Id, email = user.Email, username = user.UserName, onboardingDone = up.OnboardingDone,
        });
    }

    private static object GenericError => new
    {
        error = "invalid_credentials",
        message = "невірний email або пароль",
    };

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private IActionResult RateLimited(int retryAfterMinutes) => StatusCode(StatusCodes.Status429TooManyRequests,
        new { error = "rate_limited", retryAfterMinutes }); // та сама форма, що OnRejected в Program.cs

    private static string FieldOf(string? code) => code switch
    {
        "DuplicateUserName" or "InvalidUserName" => "username",
        "DuplicateEmail" or "InvalidEmail" => "email",
        var c when c is not null && c.StartsWith("Password") => "password",
        _ => "other",
    };

    private static Dictionary<string, string[]> GroupErrors(IEnumerable<IdentityError> errors) => errors
        .GroupBy(e => FieldOf(e.Code), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(e => e.Description).Distinct().ToArray());

    private async Task<bool> TrySendAsync(string to, string subject, string html, CancellationToken ct)
    {
        try
        {
            await Mailer.SendAsync(to, subject, html, ct);
            return true;
        }
        catch (Exception ex)
        {
            // register/сесії не ламаємо — повторна відправка доступна через resend-confirmation
            logger.LogError(ex, "Не вдалося відправити листа {To} ({Subject})", to, subject);
            return false;
        }
    }

    private async Task SignInCookieAsync(ApplicationUser user, bool isPersistent = false)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? ""),
            new Claim(ClaimTypes.Email, user.Email ?? ""),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        // isPersistent (login + rememberMe) → persistent-cookie на ExpireTimeSpan (30 діб);
        // без нього — session-cookie: авто-сесія реєстрації та звичайний логін, як раніше.
        var props = new AuthenticationProperties { IsPersistent = isPersistent };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity), props);
    }

    // ─── Emailing: доступ до IEmailSender/опцій/лімітера з фолбеком ───
    // Рекомендований шлях — builder.Services.AddEmailing(builder.Configuration) у Program.cs;
    // фолбек потрібен, щоб auth-ендпоінти лишалися робочими (dev/test) ще ДО того wiring'у
    // і щоб жоден хост тестів не падав на відсутній реєстрації.

    private EmailOptions MailOptions =>
        HttpContext.RequestServices.GetService<IOptionsMonitor<EmailOptions>>()?.CurrentValue
        ?? EmailOptions.FromEnvironment();

    private IEmailSender Mailer
    {
        get
        {
            var sp = HttpContext.RequestServices;
            if (sp.GetService<IEmailSender>() is { } registered)
                return registered;
            // фолбек: той самий вибір, що в AddEmailing (env MAIL_ENABLED)
            var options = MailOptions;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return options.Enabled
                ? new SmtpEmailSender(options, loggerFactory.CreateLogger<SmtpEmailSender>())
                : new LoggingEmailSender(loggerFactory.CreateLogger<LoggingEmailSender>(),
                    sp.GetService<IHostEnvironment>()); // null (фолбек-хост) → як до маскування
        }
    }

    private EmailRateLimiter Limiter =>
        HttpContext.RequestServices.GetService<EmailRateLimiter>()
        ?? new EmailRateLimiter(HttpContext.RequestServices.GetRequiredService<IMemoryCache>());

    /// <summary>
    /// Identity-токен-провайдери для confirm/reset. Офіційний шлях — AddEmailing →
    /// AddDefaultTokenProviders (тоді ProviderMap заповнений і цей метод — no-op);
    /// фолбек — реєстрація DataProtectorTokenProvider прямо в інстанс UserManager
    /// (userManager scoped на запит, DataProtection key-ring спільний — токени,
    /// згенеровані до/після переходу на DI-шлях, залишаються взаємно валідними).
    /// </summary>
    private void EnsureTokenProviders()
    {
        if (users.Options.Tokens.ProviderMap.Count > 0)
            return;
        var sp = HttpContext.RequestServices;
        var provider = new DataProtectorTokenProvider<ApplicationUser>(
            sp.GetRequiredService<IDataProtectionProvider>(),
            Options.Create(new DataProtectionTokenProviderOptions()),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DataProtectorTokenProvider<ApplicationUser>>());
        users.RegisterTokenProvider(TokenOptions.DefaultEmailProvider, provider); // "EmailConfirmation"
        users.RegisterTokenProvider(TokenOptions.DefaultProvider, provider);      // "Default" (password reset)
    }
}
