namespace MovieDiscovery.Core;

/// <summary>Опції бота (MD-05): секція "Bot". ApiKey з env BOT_API_KEY через MapEnvDefaults.
/// Порожній/відсутній ApiKey → bot-header авторизацію вимкнено (усі bot-header запити → 403).</summary>
public sealed class BotOptions
{
    public string? ApiKey { get; set; }
}
