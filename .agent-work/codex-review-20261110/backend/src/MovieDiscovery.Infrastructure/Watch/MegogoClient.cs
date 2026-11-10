using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Infrastructure.AnimeManga;

namespace MovieDiscovery.Infrastructure.Watch;

/// <summary>
/// Опції Megogo-синка (розділ Megogo конфігу). Розвідка 09.11 (з машини власника, UA-мережа):
/// безкоштовний розділ — /ua/collection/free_films_megogo (фільми, ~30 стор × 30 карток)
/// та /ua/collection/free_series_megogo (серіали, ~11 стор); кінець видачі — 302 на /ua.
/// robots.txt: /ua/ дозволено, /search і /e/ заборонені — їх не чіпаємо.
/// </summary>
public sealed class MegogoOptions
{
    public string BaseUrl { get; set; } = "https://megogo.ua";

    /// <summary>Чесний UA бота (NFR-6).</summary>
    public string UserAgent { get; set; } = "KinoNavigatorBot/1.0";

    /// <summary>Максимум сторінок на ОДНУ колекцію за прогін (за замовчуванням 50 — покриває всю видачу).</summary>
    public int MaxPages { get; set; } = 50;

    /// <summary>Слаг колекції безкоштовних фільмів.</summary>
    public string FilmsCollectionPath { get; set; } = "free_films_megogo";

    /// <summary>Слаг колекції безкоштовних серіалів.</summary>
    public string SeriesCollectionPath { get; set; } = "free_series_megogo";
}

/// <summary>Megogo: 1 req / 700 мс (ввічливий тротлінг); тестам дозволено override.</summary>
public sealed class MegogoThrottle(TimeSpan? minInterval = null)
    : IntervalThrottle(minInterval ?? TimeSpan.FromMilliseconds(700));

/// <summary>
/// HTTP-клієнт Megogo: тротлінг через MegogoThrottle, ретраї лише на транзитні
/// відповіді/помилки (429/5xx, мережеві збої). 302 (кінець видачі колекції) і 404 —
/// структурні відповіді без ретраїв. Конструктор бере ПОХІДНИЙ тип тротла (DI-пастка
/// typed HttpClient: базовий IntervalThrottle не зареєстрований окремо).
/// </summary>
public sealed class MegogoClient(
    HttpClient http,
    MegogoThrottle throttle,
    IOptions<MegogoOptions> options,
    ILogger<MegogoClient> log)
{
    private const int MaxAttempts = 3;

    private readonly MegogoOptions _opt = options.Value;

    /// <summary>HTML сторінки (шлях+query без хоста) або null, якщо сторінки нема/недоступна.</summary>
    public async Task<string?> GetHtmlAsync(string pathAndQuery, CancellationToken ct = default)
    {
        var url = _opt.BaseUrl.TrimEnd('/') + pathAndQuery;
        return await throttle.RunAsync(async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var resp = await http.GetAsync(url, ct);
                    if (resp.IsSuccessStatusCode)
                        return await resp.Content.ReadAsStringAsync(ct);

                    if (attempt < MaxAttempts && IsTransient(resp.StatusCode))
                    {
                        log.LogWarning("megogo {Status} на {Path} — ретрай {Attempt}",
                            (int)resp.StatusCode, pathAndQuery, attempt);
                        await Task.Delay(700 * attempt, ct);
                        continue;
                    }

                    log.LogWarning("megogo {Status} на {Path} — пропущено", (int)resp.StatusCode, pathAndQuery);
                    return null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex))
                {
                    log.LogWarning(ex, "megogo транзитна помилка на {Path} (спроба {Attempt})", pathAndQuery, attempt);
                    await Task.Delay(700 * attempt, ct);
                }
            }
        }, ct);
    }

    private static bool IsTransient(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    private static bool IsTransient(Exception ex) =>
        ex is System.Net.Http.HttpRequestException or System.IO.IOException;
}
