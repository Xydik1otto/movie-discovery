using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>Акаунти (FR-16): register/login/logout/me. Cookie HttpOnly SameSite=Lax.</summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<ApplicationUser> users,
    TasteProfileService taste) : ControllerBase
{
    public sealed record RegisterRequest(
        [property: Required, EmailAddress] string Email,
        [property: Required, MinLength(3)] string Username,
        [property: Required, MinLength(8)] string Password);

    public sealed record LoginRequest([property: Required] string Email, [property: Required] string Password);

    /// <summary>200 {userId} | 400 {errors:{поле:[помилки Identity]}}.</summary>
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
                errors = result.Errors
                    .GroupBy(e => FieldOf(e.Code), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Description).Distinct().ToArray()),
            });

        // зв'язка юзера з постійним профілем списків (FR-16)
        await taste.ResolveAsync(user.Id, ct);
        return Ok(new { userId = user.Id, email = user.Email, username = user.UserName });
    }

    /// <summary>200 {user} | 401 generic («невірний email або пароль») | 423 lockedout.</summary>
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
        await SignInCookieAsync(user);

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

    private static string FieldOf(string? code) => code switch
    {
        "DuplicateUserName" or "InvalidUserName" => "username",
        "DuplicateEmail" or "InvalidEmail" => "email",
        var c when c is not null && c.StartsWith("Password") => "password",
        _ => "other",
    };

    private async Task SignInCookieAsync(ApplicationUser user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? ""),
            new Claim(ClaimTypes.Email, user.Email ?? ""),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
    }
}
