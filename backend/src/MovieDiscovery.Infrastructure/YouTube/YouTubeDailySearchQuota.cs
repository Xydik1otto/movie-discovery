using Microsoft.Extensions.Caching.Memory;

namespace MovieDiscovery.Infrastructure.YouTube;

/// <summary>
/// Денний ГЛОБАЛЬНИЙ лічильник YouTube search-викликів з on-demand ендпоінтів
/// (GET /api/titles/{id}/trailers та GET /api/titles/{id}/watch?enrich=youtube).
/// 1 search.list ≈ 100 units квоти YouTube Data API — анонім з перебором id здатен
/// випалити денну квоту проєкту за хвилину. IMemoryCache-ключ youtube:search-count:YYYY-MM-DD
/// (UTC-день), атомарний +1 під lock (IMemoryCache не має атомарного інкременту; спільний
/// lock коректний, бо кеш і стек — один процес). Ліміт 90/день — свідомо нижче реальної
/// квоти (~100 searches): перевищення кидає YouTubeQuotaExceededException, контролери
/// маплять його на 429 {error:"quota_exceeded"}. Батч-синхи (адмінські черги) гейт минають —
/// вони під власними лімітами й не є анонімованою поверхнею.
/// </summary>
public sealed class YouTubeDailySearchQuota(IMemoryCache cache)
{
    public const int DailyLimit = 90;

    private readonly object _sync = new();

    /// <summary>Спробувати зарезервувати 1 search. false → денний ліміт уже вичерпано.</summary>
    public bool TryConsume()
    {
        lock (_sync)
        {
            var key = KeyFor(DateTime.UtcNow);
            var counter = cache.GetOrCreate(key, entry =>
            {
                // Живе до кінця UTC-доби із запасом: сам ключ містить дату, тому старі
                // записи «протухають» і без цього, а expiration прибирає сміття з пам'яті.
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(25);
                return new Counter();
            })!;
            if (counter.Value >= DailyLimit)
                return false;
            counter.Value++;
            return true;
        }
    }

    /// <summary>Скільки search-викликів уже зараховано сьогодні (діагностика/тести).</summary>
    public int UsedToday()
    {
        lock (_sync)
        {
            return cache.TryGetValue<Counter>(KeyFor(DateTime.UtcNow), out var counter)
                ? counter.Value
                : 0;
        }
    }

    /// <summary>Ключ лічильника: youtube:search-count:YYYY-MM-DD (UTC).</summary>
    public static string KeyFor(DateTime utcNow) => $"youtube:search-count:{utcNow:yyyy-MM-dd}";

    private sealed class Counter
    {
        public int Value;
    }
}

/// <summary>
/// Денний ліміт YouTube search-викликів вичерпано. СВІДОМО не успадковує YoutubeApiException —
/// м'які catch-и сервісів (YoutubeApiException → «недоступний, 0 результатів») не мають його
/// проковтувати: контролер мапить цей виняток на 429 quota_exceeded.
/// </summary>
public sealed class YouTubeQuotaExceededException()
    : Exception("Денна квота трейлерів вичерпана, спробуйте завтра");
