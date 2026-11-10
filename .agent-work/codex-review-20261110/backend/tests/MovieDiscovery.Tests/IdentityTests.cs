using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Tests;

/// <summary>
/// FR-16 через сервісний шар (WebApplicationFactory у тестовому проєкті відсутній):
/// політика пароля (≥8, цифра, велика), lockout 5×15 хв, перевірка пароля UserManager-ом.
/// </summary>
public class IdentityTests
{
    private static MovieDiscoveryDbContext NewDb(string name) => TestDb.Create(name);

    private static UserManager<ApplicationUser> NewUserManager(MovieDiscoveryDbContext db)
    {
        var options = new IdentityOptions();
        // та сама політика, що в Program.cs (FR-16)
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.User.RequireUniqueEmail = true;

        return new UserManager<ApplicationUser>(
            new Microsoft.AspNetCore.Identity.EntityFrameworkCore.UserStore<ApplicationUser>(db),
            Options.Create(options),
            new PasswordHasher<ApplicationUser>(),
            [new UserValidator<ApplicationUser>()],
            [new PasswordValidator<ApplicationUser>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null,
            NullLogger<UserManager<ApplicationUser>>.Instance);
    }

    [Theory]
    [InlineData("short1A", "PasswordTooShort")]          // 7 символів
    [InlineData("nodigitupper", "PasswordRequiresUpper")] // нема великої
    [InlineData("nodigitupperx", "PasswordRequiresUpper")]
    [InlineData("NoDigitsHere", "PasswordRequiresDigit")] // нема цифри
    public async Task Weak_Passwords_Rejected_With_Identity_Codes(string password, string expectedCode)
    {
        await using var db = NewDb($"identity-{expectedCode}");
        using var um = NewUserManager(db);

        var result = await um.CreateAsync(new ApplicationUser
        {
            UserName = "tester", Email = "tester@example.com",
        }, password);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == expectedCode);
    }

    [Fact]
    public async Task Valid_Password_Creates_User_With_Guid_Id()
    {
        await using var db = NewDb("identity-valid");
        using var um = NewUserManager(db);

        var user = new ApplicationUser { UserName = "tester", Email = "tester@example.com" };
        var result = await um.CreateAsync(user, "Passw0rd!");

        Assert.True(result.Succeeded);
        Assert.True(Guid.TryParse(user.Id, out _));
        Assert.Equal("TESTER@EXAMPLE.COM", user.NormalizedEmail);
    }

    [Fact]
    public async Task Duplicate_Username_Rejected()
    {
        await using var db = NewDb("identity-dup-username");
        using var um = NewUserManager(db);

        var first = new ApplicationUser { UserName = "nolan", Email = "nolan@example.com" };
        Assert.True((await um.CreateAsync(first, "Passw0rd!")).Succeeded);

        var second = new ApplicationUser { UserName = "nolan", Email = "other@example.com" };
        var result = await um.CreateAsync(second, "Passw0rd!");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "DuplicateUserName");
    }

    [Fact]
    public async Task Five_Failed_Attempts_Lock_Out_For_15_Minutes()
    {
        await using var db = NewDb("identity-lockout");
        using var um = NewUserManager(db);

        var user = new ApplicationUser { UserName = "tester", Email = "tester@example.com" };
        Assert.True((await um.CreateAsync(user, "Passw0rd!")).Succeeded);

        for (var i = 0; i < 5; i++)
            await um.AccessFailedAsync(user);

        Assert.True(await um.IsLockedOutAsync(user));
        Assert.NotNull(user.LockoutEnd);
        var remaining = (user.LockoutEnd!.Value - DateTimeOffset.UtcNow).TotalMinutes;
        Assert.InRange(remaining, 10, 15.5); // ~15 хв, не більше
        // Identity скидає лічильник після спрацювання lockout (стан локауту сам по собі активний)
        Assert.Equal(0, user.AccessFailedCount);
    }

    [Fact]
    public async Task Failed_Attempts_Trigger_Lockout_Then_Reset_After_Successful_Login_Path()
    {
        await using var db = NewDb("identity-reset");
        using var um = NewUserManager(db);

        var user = new ApplicationUser { UserName = "tester", Email = "tester@example.com" };
        Assert.True((await um.CreateAsync(user, "Passw0rd!")).Succeeded);

        // CheckPasswordAsync НЕ рахує невдачі (це робить SignInManager/контролер через AccessFailedAsync)
        Assert.False(await um.CheckPasswordAsync(user, "WrongPass9"));
        Assert.False(await um.CheckPasswordAsync(user, "WrongPass9"));
        Assert.Equal(0, user.AccessFailedCount);

        // контролер логіна робить саме це при невдалому паролі:
        await um.AccessFailedAsync(user);
        await um.AccessFailedAsync(user);
        Assert.Equal(2, user.AccessFailedCount);

        // успішний вхід → ResetAccessFailedCount (контролер)
        Assert.True(await um.CheckPasswordAsync(user, "Passw0rd!"));
        await um.ResetAccessFailedCountAsync(user);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.False(await um.IsLockedOutAsync(user));
    }

    [Fact]
    public async Task Three_Attempts_Do_Not_Lock_Out_Yet()
    {
        await using var db = NewDb("identity-3attempts");
        using var um = NewUserManager(db);

        var user = new ApplicationUser { UserName = "tester", Email = "tester@example.com" };
        Assert.True((await um.CreateAsync(user, "Passw0rd!")).Succeeded);

        for (var i = 0; i < 3; i++)
            await um.AccessFailedAsync(user);

        Assert.False(await um.IsLockedOutAsync(user));
    }
}
