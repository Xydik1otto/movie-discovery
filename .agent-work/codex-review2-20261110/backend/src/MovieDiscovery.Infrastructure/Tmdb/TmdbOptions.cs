namespace MovieDiscovery.Infrastructure.Tmdb;

/// <summary>Опції TMDB-клієнта: ключ та TTL спискових викликів (деталі — ttl=0, безстроково).</summary>
public sealed class TmdbOptions
{
    public string ApiKey { get; set; } = "";
    public int ListTtlSeconds { get; set; } = 21600; // 6 годин для popular/discover
    public int MaxRetries { get; set; } = 3;
}
