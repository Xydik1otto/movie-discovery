using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>
/// Розв'язує ЕДИНИЙ designated guest profile (MD-01): єдиний профіль, у який
/// анонімні запити можуть писати/читати. Профіль, приріплений до акаунта через
/// user_profiles, гостьовим НЕ вважається — його дані анонімним шляхом
/// недоступні (включно з legacy id=1, якщо колись був приріплений юзеру).
/// Гостьові списки основного сценарію живуть у localStorage клієнта (MD-04);
/// цей серверний профіль — сумісний legacy surface, обмежений одним профілем.
/// </summary>
public sealed class GuestProfileService(MovieDiscoveryDbContext db, IConfiguration config, ILogger<GuestProfileService> log)
{
    /// <summary>Id designated guest profile або null, якщо гостьовий серверний шлях вимкнено.</summary>
    public async Task<Profile?> ResolveAsync(CancellationToken ct = default)
    {
        var configured = config.GetValue<int?>("GUEST_PROFILE_ID") ?? 1;
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == configured, ct);
        if (profile is null)
        {
            log.LogWarning("GUEST_PROFILE_ID={Id} не знайдено — гостьовий серверний API списків вимкнено", configured);
            return null;
        }

        // Приватність (MD-01): профіль, лінкнутий на акаунт, ніколи не гостьовий.
        // Без кешу: два дешеві індексні запити, а свіжість перевірки власника —
        // на кожному запиті (реєстрація не має відкрити гостьовий профіль ретроспективно).
        var linkedToAccount = await db.UserProfiles.AsNoTracking()
            .AnyAsync(x => x.ProfileId == profile.Id, ct);
        if (linkedToAccount)
        {
            log.LogWarning(
                "Профіль {Id} приріплений до акаунта — гостьовий серверний API списків вимкнено (списки живуть у localStorage)",
                profile.Id);
            return null;
        }

        return profile;
    }
}
