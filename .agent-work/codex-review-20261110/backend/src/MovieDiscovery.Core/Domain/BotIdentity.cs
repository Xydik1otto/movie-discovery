namespace MovieDiscovery.Core.Domain;

/// <summary>
/// Telegram-ідентичність бота (MD-05): безпечна заміна hardcoded ProfileId=1.
/// Кожен telegram-юзер отримує ВЛАСНИЙ профіль списків; PK = telegram_user_id
/// (унікальний — race-safe створення за патерном TasteProfileService.ResolveAsync).
/// </summary>
public class BotIdentity
{
    public long TelegramUserId { get; set; }
    public int ProfileId { get; set; }
    public Profile Profile { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
