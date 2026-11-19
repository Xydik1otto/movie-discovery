namespace MovieDiscovery.Infrastructure.Tmdb;

/// <summary>Опції TMDB-клієнта: ключ та TTL спискових викликів (деталі — ttl=0, безстроково).</summary>
public sealed class TmdbOptions
{
    public const int WatchProvidersTtlSeconds = 6 * 3600;

    public string ApiKey { get; set; } = "";
    public bool AutomaticWatchDiscoveryEnabled { get; set; } = true;
    public int WatchSweepIntervalMinutes { get; set; } = 30;
    public int WatchSweepBatchSize { get; set; } = 100;
    public int WatchActiveTitleLimit { get; set; } = 12000;
    public int ListTtlSeconds { get; set; } = 21600; // 6 годин для popular/discover
    public int MaxRetries { get; set; } = 3;
}
