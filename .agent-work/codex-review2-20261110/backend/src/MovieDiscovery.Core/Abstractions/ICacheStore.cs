namespace MovieDiscovery.Core.Abstractions;

/// <summary>
/// Кеш зовнішніх джерел у БД (source_cache). КОЖЕН зовнішній виклик має йти через нього.
/// ttlSeconds = 0 означає безстроковий кеш (метадані тайтла).
/// </summary>
public interface ICacheStore
{
    /// <summary>Сира JSON-строка з кешу або null (нема запису / TTL минув).</summary>
    Task<string?> GetAsync(string source, string cacheKey, CancellationToken ct = default);

    /// <summary>Зберегти/оновити запис кешу (upsert за source+cacheKey).</summary>
    Task SetAsync(string source, string cacheKey, string payload, int ttlSeconds, CancellationToken ct = default);
}
