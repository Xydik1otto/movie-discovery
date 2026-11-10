using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Caching;

/// <summary>
/// Реалізація кешу зовнішніх джерел у таблиці source_cache.
/// КОЖЕН зовнішній виклик TMDB/OMDb йде через цей кеш; ttl_seconds=0 → безстроково.
/// </summary>
public sealed class SourceCacheStore(MovieDiscoveryDbContext db) : ICacheStore
{
    public async Task<string?> GetAsync(string source, string cacheKey, CancellationToken ct = default)
    {
        var row = await db.SourceCache.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Source == source && x.CacheKey == cacheKey, ct);
        if (row is null) return null;
        if (row.TtlSeconds != 0 && row.FetchedAt.AddSeconds(row.TtlSeconds) < DateTime.UtcNow) return null;
        return row.Payload.RootElement.GetRawText();
    }

    public async Task SetAsync(string source, string cacheKey, string payload, int ttlSeconds, CancellationToken ct = default)
    {
        var json = JsonDocument.Parse(payload);
        var now = DateTime.UtcNow;

        var row = await db.SourceCache
            .FirstOrDefaultAsync(x => x.Source == source && x.CacheKey == cacheKey, ct);
        if (row is null)
        {
            db.SourceCache.Add(new SourceCacheEntry
            {
                Source = source,
                CacheKey = cacheKey,
                Payload = json,
                FetchedAt = now,
                TtlSeconds = ttlSeconds,
            });
        }
        else
        {
            row.Payload = json;
            row.FetchedAt = now;
            row.TtlSeconds = ttlSeconds;
        }

        await db.SaveChangesAsync(ct);
    }
}
