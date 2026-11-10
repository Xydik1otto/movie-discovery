using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api;

/// <summary>
/// Імена rate-limit політик для on-demand YouTube-ендпоінтів (1 search.list ≈ 100 units
/// квоти YouTube Data API — анонім з перебором id міг би випалити денну квоту проєкту).
/// Політика живе ОКРЕМИМ файлом (НЕ в Program.cs), щоб реєстрація була одним рядком:
/// <code>builder.Services.AddYouTubeRateLimiting();</code>
/// </summary>
public static class YouTubeRatePolicy
{
    public const string PolicyName = "youtube-ondemand";
}

internal static class YouTubeRateLimitingExtensions
{
    /// <summary>
    /// Партіція 10/хв на IP для GET /api/titles/{id}/trailers і watch-enrich
    /// (?enrich=youtube у GET /api/titles/{id}/watch) — та сама схема, що auth-політики
    /// в Program.cs: RemoteIpAddress (вже розпізнаний з X-Forwarded-For) + FixedWindow без черги.
    /// ЛІМІТУЮТЬСЯ: trailers-ендпоінт — ЗАВЖДИ (кожен cache-miss може стати search'ом);
    /// watch — ЛИШЕ ?enrich=youtube (єдиний шлях, що може викликати YouTube search):
    /// звичайні читання «де подивитись» — дешеві БД-запити, 10/хв зламав би звичайний перегляд.
    /// OnRejected/RejectionStatusCode НЕ перепризначаємо — вони спільні з Program.cs
    /// (429 з розпізнаваним тілом {error:"rate_limited", retryAfterMinutes}).
    /// </summary>
    internal static IServiceCollection AddYouTubeRateLimiting(this IServiceCollection services) =>
        services
            .AddSingleton<YouTubeDailySearchQuota>()
            .AddRateLimiter(o =>
            {
                o.AddPolicy(YouTubeRatePolicy.PolicyName, ctx =>
                {
                    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var path = ctx.Request.Path;
                    var isTrailersEndpoint =
                        path.StartsWithSegments("/api/titles") && path.Value!.EndsWith("/trailers", StringComparison.Ordinal);
                    var enrichYouTube = string.Equals(
                        ctx.Request.Query["enrich"].ToString().Trim(),
                        "youtube", StringComparison.OrdinalIgnoreCase);
                    // КЛЮЧІ ПАРТІЦІЙ РІЗНІ для лімітованого і необмеженого шляхів: ключ —
                    // ідентичність лімітера, а не лише IP. Колізія («yt-ondemand:{ip}» == «{ip}»)
                    // змушувала NoLimiter-запит підхоплювати ВЖЕ СТВОРЕНИЙ FixedWindow-лімітер
                    // під тим самим ключем → звичайні watch-читання 429-ились після 10 трейлерів
                    // (живий замір 11.11).
                    return isTrailersEndpoint || enrichYouTube
                        ? RateLimitPartition.GetFixedWindowLimiter($"yt-ondemand:{ip}", _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                        : RateLimitPartition.GetNoLimiter($"open:{ip}");
                });
            });
}

/// <summary>
/// Точковий per-IP ліміт ДОРОГОГО шляху на IMemoryCache (атомарний під lock).
/// На відміну від middleware-політики (лімітує ВЕСЬ ендпоінт), цей гейт ставиться
/// перед конкретною дорогою операцією — кешовані/дешеві читання проходять без ліміту.
/// Вікно фіксоване (ключ містить номер вікна), запис автоекспайриться через 2 вікна.
/// Мінімальний перебір під гонкою неможливий: інкремент під спільним lock.
/// </summary>
internal static class ExpensivePathGate
{
    private static readonly object Lock = new();

    internal static bool TryAcquire(
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
        HttpContext http,
        string scope,
        int permitLimit,
        TimeSpan window)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var windowIndex = DateTimeOffset.UtcNow.UtcTicks / window.Ticks;
        var bucketKey = $"gate:{scope}:{ip}:{windowIndex}";
        lock (Lock)
        {
            var current = cache.Get(bucketKey) is int c && c > 0 ? c : 0;
            if (current >= permitLimit)
                return false;
            cache.Set(
                bucketKey, current + 1,
                new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = window + window });
            return true;
        }
    }
}
