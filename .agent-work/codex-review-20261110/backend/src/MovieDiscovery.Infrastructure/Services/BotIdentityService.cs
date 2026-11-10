using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>
/// MD-05: мапінг telegram-ідентичності бота → профіль списків (заміна hardcoded ProfileId=1).
/// Невідомий telegram_user_id → створюється ВЛАСНИЙ профіль (IsDefault=false, ім'я «TG «перші 8
/// символів id»») + рядок bot_identities; гонка безпечна: PK = telegram_user_id → при конфлікті
/// перечитуємо (патерн TasteProfileService.ResolveAsync). Ключ X-Bot-Key звіряється з Bot:ApiKey;
/// порожній/відсутній ключ у конфігу → bot-header авторизацію вимкнено (усі такі запити → 403).
/// </summary>
public sealed class BotIdentityService(MovieDiscoveryDbContext db, IOptions<BotOptions> options)
{
    public BotOptions Options { get; } = options.Value;

    /// <summary>Чи ввімкнено bot-header авторизацію (Bot:ApiKey непорожній).</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Options.ApiKey);

    /// <summary>X-Bot-Key збігається з налаштованим ключем (вимкнений ключ → завжди false).</summary>
    public bool KeyMatches(string? key) =>
        Enabled && !string.IsNullOrWhiteSpace(key) && string.Equals(key, Options.ApiKey, StringComparison.Ordinal);

    /// <summary>Профіль telegram-юзера; за потреби створює профіль + bot_identities (race-safe).</summary>
    public async Task<Core.Domain.Profile> ResolveAsync(long telegramUserId, CancellationToken ct = default)
    {
        var identity = await db.BotIdentities.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TelegramUserId == telegramUserId, ct);
        if (identity is not null)
            return await db.Profiles.AsNoTracking().FirstAsync(p => p.Id == identity.ProfileId, ct);

        // Кожен telegram-юзер — ВЛАСНИЙ профіль (ізоляція між юзерами; дефолтний guest-профіль id=1 не чіпаємо).
        var tg = telegramUserId.ToString();
        var name = $"TG «{(tg.Length <= 8 ? tg : tg[..8])}»";
        var profile = db.Profiles.Add(new Core.Domain.Profile { Name = name, IsDefault = false }).Entity;
        var row = db.BotIdentities.Add(new Core.Domain.BotIdentity
        {
            TelegramUserId = telegramUserId,
            Profile = profile,
            CreatedAt = DateTime.UtcNow,
        }).Entity;
        try
        {
            await db.SaveChangesAsync(ct);
            return profile;
        }
        catch (DbUpdateException)
        {
            // Паралельний запит уже створив — перечитуємо (unique PK telegram_user_id).
            db.Entry(row).State = EntityState.Detached;
            db.Entry(profile).State = EntityState.Detached;
            identity = await db.BotIdentities.AsNoTracking()
                .FirstAsync(x => x.TelegramUserId == telegramUserId, ct);
            return await db.Profiles.AsNoTracking().FirstAsync(p => p.Id == identity.ProfileId, ct);
        }
    }
}
