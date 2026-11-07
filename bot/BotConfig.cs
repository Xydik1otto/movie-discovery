namespace MovieDiscovery.Bot;

/// <summary>Конфігурація бота з env: TG_BOT_TOKEN, API_URL (дефолт http://api:5200 — див. PLAN.md).</summary>
public sealed record BotConfig(string Token, string ApiUrl)
{
    public const string DefaultApiUrl = "http://api:5200";

    public static BotConfig FromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable("TG_BOT_TOKEN")?.Trim() ?? string.Empty;
        var apiUrl = Environment.GetEnvironmentVariable("API_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(apiUrl))
            apiUrl = DefaultApiUrl;
        return new BotConfig(token, apiUrl);
    }
}
