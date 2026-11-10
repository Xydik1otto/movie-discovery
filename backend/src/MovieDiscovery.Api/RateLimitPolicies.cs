using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
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
                    return isTrailersEndpoint || enrichYouTube
                        ? RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 10,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                        : RateLimitPartition.GetNoLimiter(ip);
                });
            });
}
