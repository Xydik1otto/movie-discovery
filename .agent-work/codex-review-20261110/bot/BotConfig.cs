namespace MovieDiscovery.Bot;

/// <summary>
/// Конфігурація бота з env: TG_BOT_TOKEN, API_URL (дефолт http://api:5200), BOT_API_KEY.
/// BOT_API_KEY порожній = авторизація API вимкнена (заголовок X-Bot-Key не надсилається);
/// при старті бота це логується як попередження.
/// </summary>
public sealed record BotConfig(string Token, string ApiUrl, string BotApiKey = "")
{
    public const string DefaultApiUrl = "http://api:5200";

    public static BotConfig FromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable("TG_BOT_TOKEN")?.Trim() ?? string.Empty;
        var apiUrl = Environment.GetEnvironmentVariable("API_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(apiUrl))
            apiUrl = DefaultApiUrl;
        var botApiKey = Environment.GetEnvironmentVariable("BOT_API_KEY")?.Trim() ?? string.Empty;
        return new BotConfig(token, apiUrl, botApiKey);
    }
}
