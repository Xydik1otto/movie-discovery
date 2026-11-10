using Microsoft.AspNetCore.Identity;

namespace MovieDiscovery.Core.Domain;

/// <summary>
/// Користувач (FR-16): Identity-сутність, таблиця AspNetUsers (Identity defaults).
/// Id — guid-рядок; пароль/lockout — з IdentityUser (PasswordHash, LockoutEnd, LockoutEnabled, AccessFailedCount).
/// </summary>
public class ApplicationUser : IdentityUser
{
    public ApplicationUser()
    {
        Id = Guid.NewGuid().ToString();
        // lockout активний для всіх нових юзерів (дублює LockoutOptions.AllowedForNewUsers)
        LockoutEnabled = true;
    }
}
