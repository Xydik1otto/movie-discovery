namespace MovieDiscovery.Core.Domain;

/// <summary>Провайдер перегляду (стрімінг/прокат/покупка) — таблиця providers.</summary>
public class Provider
{
    public int Id { get; set; }
    public int TitleId { get; set; }
    public Title Title { get; set; } = null!;
    public string ProviderName { get; set; } = ""; // колонка provider
    public string Kind { get; set; } = "stream"; // stream | rent | buy | free
    public string? Url { get; set; }
    public string Source { get; set; } = "justwatch";
    public string Region { get; set; } = "UA";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
