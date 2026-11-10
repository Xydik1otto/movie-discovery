using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.AnimeManga; // IntervalThrottle — спільний патерн тротлінга джерел
using MovieDiscovery.Infrastructure.Embeddings; // IHttpClientFactoryAccessor — без нових реєстрацій у Program.cs

namespace MovieDiscovery.Infrastructure.Tmdb;

/// <summary>
/// Результат резолву картинок TMDB: сирі шляхи (формат TMDB "/abc.jpg" — саме їх пишемо в
/// titles.poster_path/backdrop_path, щоб TitleMapper.ImgUrl міг обирати розмір) + готові URL
/// (постер w780 — достатньо для деталей; backdrop — original). Порожні поля → null.
/// </summary>
public sealed record TmdbResolvedImages(string? PosterPath, string? BackdropPath)
{
    /// <summary>Повний URL постера (w780) або null.</summary>
    public string? PosterUrl => TmdbImagesService.PosterUrl(PosterPath);

    /// <summary>Повний URL бекдропа (original) або null.</summary>
    public string? BackdropUrl => TmdbImagesService.BackdropUrl(BackdropPath);
}

/// <summary>Резолв постера/бекдропа тайтла за tmdb_id (джерело №1 — TMDB, ключ уже в TmdbOptions).</summary>
public interface ITmdbImagesService
{
    /// <summary>TMDB-ключ заданий (без нього резолв неможливий — викликач пропускає доціплення).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// poster_path/backdrop_path тайтла TMDB: спершу безстроковий кеш деталей
    /// (TmdbCacheKeys.Details(type, tmdbId, "en-US") — той самий патерн, що SimilarityService),
    /// якщо в кеші нема — ОДИН легкий HTTP-запит GET /3/movie/{id} (або /3/tv/{id}; без
    /// append_to_response — нам потрібні лише шляхи картинок). Нема картинок/джерело недоступне → null.
    /// </summary>
    Task<TmdbResolvedImages?> ResolveAsync(int tmdbId, string type, CancellationToken ct = default);
}

/// <summary>
/// On-demand і масовий резолв постерів/бекдропів з TMDB. Легкий запит деталей (без
/// append_to_response — важка відповідь із credits/similar не потрібна і НЕ кешується під
/// ключ Details, щоб не отруювати повний кеш частковою відповіддю). Пауза ≥300мс між
/// HTTP-запитами (спільна для споживачів цього сервісу), 429 → ретрай з повагою Retry-After
/// (кап 3с), 404/інші 4xx → null без ретраїв, 5xx/таймаут → обмежені ретраї. Жодних виключень
/// назовні (викликається в hot path контролера) — невдача = null.
/// </summary>
public sealed class TmdbImagesService : ITmdbImagesService
{
    /// <summary>Розмір постера для деталей (w780 достатньо; у БД пишемо сирий шлях — розмір обирає ImgUrl).</summary>
    public const string PosterSize = "w780";
    /// <summary>Розмір бекдропа для деталей (hero-блок сторінки тайтла — original).</summary>
    public const string BackdropSize = "original";

    private static readonly TimeSpan DefaultThrottleInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan DefaultRetryAfterCap = TimeSpan.FromSeconds(3);
    private const int DefaultMaxAttempts = 3;
    private const int RequestTimeoutSeconds = 15;

    public const string Source = "tmdb-images";

    private readonly ICacheStore _cache;
    private readonly TmdbOptions _opt;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private readonly IntervalThrottle _throttle;
    private readonly TimeSpan _retryAfterCap;
    private readonly int _maxAttempts;

    /// <summary>DI-конструктор: клієнт через фабрику (без нової реєстрації в Program.cs) + кеш.</summary>
    public TmdbImagesService(
        IHttpClientFactoryAccessor httpFactory,
        ICacheStore cache,
        IOptions<TmdbOptions> options,
        ILogger<TmdbImagesService> log)
        : this(httpFactory.CreateClient("tmdb-images"), cache, options, log,
            DefaultThrottleInterval, DefaultRetryAfterCap, DefaultMaxAttempts)
    {
    }

    /// <summary>Внутрішній конструктор (тести): явні HttpClient і параметри ввічливості.</summary>
    internal TmdbImagesService(
        HttpClient http,
        ICacheStore cache,
        IOptions<TmdbOptions> options,
        ILogger<TmdbImagesService> log,
        TimeSpan throttleInterval,
        TimeSpan retryAfterCap,
        int maxAttempts)
    {
        _http = http;
        _cache = cache;
        _opt = options.Value;
        _log = log;
        _throttle = new IntervalThrottle(throttleInterval);
        _retryAfterCap = retryAfterCap;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ApiKey);

    /// <summary>Сирий шлях TMDB → повний URL image.tmdb.org; абсолютні URL — як є; порожнє → null.</summary>
    public static string? PosterUrl(string? posterPath) => ImgUrl(posterPath, PosterSize);

    /// <summary>Сирий шлях TMDB → повний URL image.tmdb.org (original) або null.</summary>
    public static string? BackdropUrl(string? backdropPath) => ImgUrl(backdropPath, BackdropSize);

    private static string? ImgUrl(string? path, string size) =>
        string.IsNullOrWhiteSpace(path) ? null
        : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path
        : $"https://image.tmdb.org/t/p/{size}{path}";

    public async Task<TmdbResolvedImages?> ResolveAsync(int tmdbId, string type, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _log.LogWarning("TMDB_API_KEY не задано — резолв постера пропущено (tmdb_id={TmdbId})", tmdbId);
            return null;
        }

        // Кеш деталей: "movie" → movie, усе інше (series/show) → tv (як у TmdbClient/TitleService).
        var cacheType = type == "movie" ? "movie" : "series";
        var cached = await _cache.GetAsync(TmdbCacheKeys.Source, TmdbCacheKeys.Details(cacheType, tmdbId, "en-US"), ct);
        if (cached is not null)
        {
            try
            {
                // Повний кеш деталей (навіть з append_to_response) містить ті самі поля шляхів.
                return ParsePaths(cached);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Пошкоджений кеш деталей TMDB ({Type}:{Id}) — тягну свіжі дані", cacheType, tmdbId);
            }
        }

        var json = await FetchDetailsJsonAsync($"{(cacheType == "movie" ? "movie" : "tv")}/{tmdbId}", ct);
        if (json is null) return null;

        try
        {
            return ParsePaths(json);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Не вдалося розібрати відповідь TMDB для {Type}:{Id}", cacheType, tmdbId);
            return null;
        }
    }

    /// <summary>Парсер шляхів картинок з JSON деталей TMDB (poster_path/backdrop_path; нема поля → null).</summary>
    public static TmdbResolvedImages ParsePaths(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var poster = root.TryGetProperty("poster_path", out var pp) && pp.ValueKind == JsonValueKind.String
            ? pp.GetString()
            : null;
        var backdrop = root.TryGetProperty("backdrop_path", out var bp) && bp.ValueKind == JsonValueKind.String
            ? bp.GetString()
            : null;
        return new TmdbResolvedImages(poster, backdrop);
    }

    /// <summary>
    /// Один легкий GET /3/{movie|tv}/{id}?api_key=...&amp;language=en-US. Ретраї: 429 — з повагою
    /// Retry-After (кап 3с), 5xx — backoff; 404/інші 4xx — null без ретраїв. Тротлінг ≥300мс
    /// між запитами (IntervalThrottle на кожну спробу).
    /// </summary>
    private async Task<string?> FetchDetailsJsonAsync(string path, CancellationToken ct)
    {
        var url = $"{path}?api_key={_opt.ApiKey}&language=en-US";
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await _throttle.RunAsync(async () =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
                    return await _http.GetAsync(url, timeout.Token);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) // HttpRequestException / таймаут — джерело недоступне
            {
                if (attempt >= _maxAttempts - 1)
                {
                    _log.LogWarning(ex, "TMDB недоступний ({Path}) — здаюся після {N} спроб", path, _maxAttempts);
                    return null;
                }
                await Task.Delay(BackoffDelay(attempt, null), ct);
                continue;
            }

            using (resp)
            {
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _log.LogInformation("TMDB 404: {Path}", path);
                    return null;
                }

                if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500
                    && resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    _log.LogWarning("TMDB {Code} на {Path} — без ретраїв", (int)resp.StatusCode, path);
                    return null;
                }

                if (attempt >= _maxAttempts - 1)
                {
                    _log.LogWarning("TMDB {Code} на {Path} — ліміт спроб вичерпано", (int)resp.StatusCode, path);
                    return null;
                }

                TimeSpan? retryAfter = resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? ParseRetryAfter(resp)
                    : null;
                _log.LogInformation("TMDB {Code} на {Path} — ретрай через {Delay:F1}с",
                    (int)resp.StatusCode, path, (retryAfter ?? BackoffDelay(attempt, null)).TotalSeconds);
                await Task.Delay(retryAfter ?? BackoffDelay(attempt, null), ct);
            }
        }
    }

    /// <summary>Retry-After з відповіді 429, обрізаний капом (≤3с) — довгі Retry-After не блокують прогін.</summary>
    private TimeSpan ParseRetryAfter(HttpResponseMessage resp)
    {
        var delta = resp.Headers.RetryAfter?.Delta;
        if (delta is null)
        {
            var date = resp.Headers.RetryAfter?.Date;
            delta = date is { } d ? d - DateTimeOffset.UtcNow : null;
        }
        if (delta is not { } wait || wait <= TimeSpan.Zero)
            return TimeSpan.FromSeconds(1); // 429 без читабельного Retry-After — обережна секунда
        return wait > _retryAfterCap ? _retryAfterCap : wait;
    }

    private static TimeSpan BackoffDelay(int attempt, TimeSpan? retryAfter) =>
        retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1с, 2с, 4с
}
