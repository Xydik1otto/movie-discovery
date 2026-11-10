using Microsoft.Extensions.Caching.Memory;

namespace MovieDiscovery.Infrastructure.Services.Emailing;

/// <summary>
/// Лімітер 3/год на (дія + email + IP) для resend-confirmation / forgot-password
/// (антиенумерація та анти-мейлбомбинг). Стан живе в СПІЛЬНОМУ IMemoryCache
/// (Program.cs робить AddMemoryCache) — контролер інстанціює лімітер з кешом запиту,
/// тому окрема DI-реєстрація не обов'язкова; стан переживає запити і скидається з рестартом api.
/// </summary>
public sealed class EmailRateLimiter(IMemoryCache cache)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private const int Limit = 3;

    /// <summary>true → дозвіл витрачено; false → перевищено ліміт, retryAfterMinutes ≥ 1.</summary>
    public bool TryAcquire(string action, string email, string? ip, out int retryAfterMinutes)
    {
        var key = $"email-rate:{action}:{email.Trim().ToLowerInvariant()}:{ip ?? "unknown"}";
        var now = DateTimeOffset.UtcNow;
        var bucket = cache.GetOrCreate(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Window;
            return new List<DateTimeOffset>();
        })!;

        lock (bucket)
        {
            bucket.RemoveAll(t => now - t >= Window);
            if (bucket.Count >= Limit)
            {
                // найдавніша витрата + вікно = коли звільниться слот
                retryAfterMinutes = Math.Max(1, (int)Math.Ceiling((bucket.Min() + Window - now).TotalMinutes));
                return false;
            }
            bucket.Add(now);
            retryAfterMinutes = 0;
            return true;
        }
    }
}
