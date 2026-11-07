namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Послідовний тротлінг зовнішніх джерел: ≥ minInterval між початками запитів одного джерела.
/// Один екземпляр на джерело (singleton) — тротлінг спільний для синку каталогу та імпортів.
/// </summary>
public class IntervalThrottle(TimeSpan minInterval)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAtUtc = DateTimeOffset.MinValue;

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _nextAtUtc - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var result = await action();
            _nextAtUtc = DateTimeOffset.UtcNow + minInterval;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunAsync(Func<Task> action, CancellationToken ct = default) =>
        await RunAsync<object?>(async () => { await action(); return null; }, ct);
}

/// <summary>Jikan (дзеркало MyAnimeList): 3 req/s → інтервал ≥400 мс.</summary>
public sealed class JikanThrottle() : IntervalThrottle(TimeSpan.FromMilliseconds(400));

/// <summary>Shikimori: ~2 req/s → інтервал 500 мс.</summary>
public sealed class ShikimoriThrottle() : IntervalThrottle(TimeSpan.FromMilliseconds(500));

/// <summary>AniList: 90 req/min → інтервал ~700 мс.</summary>
public sealed class AniListThrottle() : IntervalThrottle(TimeSpan.FromMilliseconds(700));

/// <summary>Осмислена помилка джерела (403 Shikimori, приватний/незнайдений профіль) → у stats/error джоби.</summary>
public sealed class SourceAccessException(string userMessage) : Exception(userMessage)
{
    /// <summary>Повідомлення українською для stats/error (без стек-трейсу).</summary>
    public string UserMessage { get; } = userMessage;
}
