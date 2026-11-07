using System.Text.Json;

namespace MovieDiscovery.Core.Domain;

/// <summary>Кеш зовнішніх джерел — таблиця source_cache. ttl_seconds = 0 означає безстроковий кеш.</summary>
public class SourceCacheEntry
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public string CacheKey { get; set; } = "";
    public JsonDocument Payload { get; set; } = null!;
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public int TtlSeconds { get; set; }
}
