using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;

namespace MovieDiscovery.Infrastructure.Tmdb;

/// <summary>
/// Клієнт TMDB v3 (typed HttpClient, api_key у query). КОЖЕН виклик іде через source_cache.
/// Обробка 404 (→ null, без ретраїв) і 429/5xx (ретраї з backoff, повага Retry-After).
/// </summary>
public sealed class TmdbClient(
    HttpClient http, ICacheStore cache, IOptions<TmdbOptions> options, ILogger<TmdbClient> log)
{
    private readonly TmdbOptions _opt = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ApiKey);

    // ─── Спискові виклики (popular/discover), ttl = ListTtlSeconds ───

    public Task<TmdbPage<TmdbMovieSummary>?> MoviePopularAsync(int page, string lang, CancellationToken ct = default) =>
        FetchAsync<TmdbPage<TmdbMovieSummary>>(
            TmdbCacheKeys.List("popular", "movie", lang, page),
            $"movie/popular?api_key={_opt.ApiKey}&language={lang}&page={page}",
            _opt.ListTtlSeconds, ct);

    public Task<TmdbPage<TmdbMovieSummary>?> DiscoverMoviesAsync(int page, string lang, CancellationToken ct = default) =>
        FetchAsync<TmdbPage<TmdbMovieSummary>>(
            TmdbCacheKeys.List("discover", "movie", lang, page),
            $"discover/movie?api_key={_opt.ApiKey}&language={lang}&page={page}&sort_by=popularity.desc&include_adult=false",
            _opt.ListTtlSeconds, ct);

    public Task<TmdbPage<TmdbTvSummary>?> TvPopularAsync(int page, string lang, CancellationToken ct = default) =>
        FetchAsync<TmdbPage<TmdbTvSummary>>(
            TmdbCacheKeys.List("popular", "series", lang, page),
            $"tv/popular?api_key={_opt.ApiKey}&language={lang}&page={page}",
            _opt.ListTtlSeconds, ct);

    public Task<TmdbPage<TmdbTvSummary>?> DiscoverTvAsync(int page, string lang, CancellationToken ct = default) =>
        FetchAsync<TmdbPage<TmdbTvSummary>>(
            TmdbCacheKeys.List("discover", "series", lang, page),
            $"discover/tv?api_key={_opt.ApiKey}&language={lang}&page={page}&sort_by=popularity.desc&include_adult=false",
            _opt.ListTtlSeconds, ct);

    // ─── Деталі тайтла: append_to_response = credits,keywords,videos,watch/providers,similar,recommendations ───
    // Метадані — фактично безстроковий кеш (ttl=0).

    public Task<TmdbMovieDetails?> MovieDetailsAsync(int id, string lang, CancellationToken ct = default) =>
        FetchAsync<TmdbMovieDetails>(
            TmdbCacheKeys.Details("movie", id, lang),
            $"movie/{id}?api_key={_opt.ApiKey}&language={lang}" +
            "&append_to_response=credits,keywords,videos,watch/providers,similar,recommendations",
            0, ct);

    public Task<TmdbTvDetails?> TvDetailsAsync(int id, string lang, CancellationToken ct = default) =>
        FetchAsync<TmdbTvDetails>(
            TmdbCacheKeys.Details("series", id, lang),
            $"tv/{id}?api_key={_opt.ApiKey}&language={lang}" +
            "&append_to_response=credits,keywords,videos,watch/providers,similar,recommendations",
            0, ct);

    // ─── FR-14: watch/providers через imdb_id (dormant до TMDB_API_KEY) ───

    /// <summary>find/{imdb_id}?external_source=imdb_id → id фільма/серіалу TMDB (безстроковий кеш).</summary>
    public Task<TmdbFind?> FindByImdbAsync(string imdbId, CancellationToken ct = default) =>
        FetchAsync<TmdbFind>(
            TmdbCacheKeys.Find("imdb", imdbId),
            $"find/{imdbId}?api_key={_opt.ApiKey}&external_source=imdb_id",
            0, ct);

    public Task<TmdbWatchProviders?> MovieWatchProvidersAsync(int id, CancellationToken ct = default) =>
        FetchAsync<TmdbWatchProviders>(
            TmdbCacheKeys.Providers("movie", id, "UA"),
            $"movie/{id}/watch/providers?api_key={_opt.ApiKey}",
            0, ct);

    public Task<TmdbWatchProviders?> TvWatchProvidersAsync(int id, CancellationToken ct = default) =>
        FetchAsync<TmdbWatchProviders>(
            TmdbCacheKeys.Providers("series", id, "UA"),
            $"tv/{id}/watch/providers?api_key={_opt.ApiKey}",
            0, ct);

    // ─── Загальний механізм: кеш → HTTP з ретраями ───

    private async Task<T?> FetchAsync<T>(string cacheKey, string pathAndQuery, int ttlSeconds, CancellationToken ct) where T : class
    {
        if (!IsConfigured)
        {
            log.LogWarning("TMDB_API_KEY не задано — зовнішній виклик пропущено ({Key})", cacheKey);
            return null;
        }

        var cached = await cache.GetAsync(TmdbCacheKeys.Source, cacheKey, ct);
        if (cached is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(cached, TmdbJson.Options);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Пошкоджений кеш TMDB ({Key}) — тягну свіжи дані", cacheKey);
            }
        }

        var json = await SendWithRetryAsync(pathAndQuery, ct);
        if (json is null) return null;

        try
        {
            await cache.SetAsync(TmdbCacheKeys.Source, cacheKey, json, ttlSeconds, ct);
            return JsonSerializer.Deserialize<T>(json, TmdbJson.Options);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Не вдалося розібрати відповідь TMDB ({Key})", cacheKey);
            return null;
        }
    }

    private async Task<string?> SendWithRetryAsync(string pathAndQuery, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await http.GetAsync(pathAndQuery, ct);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync(ct);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    log.LogInformation("TMDB 404: {Path}", pathAndQuery);
                    return null;
                }

                if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                {
                    log.LogWarning("TMDB {Code} на {Path} — без ретраїв", (int)resp.StatusCode, pathAndQuery);
                    return null;
                }

                // 429 / 5xx → ретраї з backoff
                if (attempt >= _opt.MaxRetries)
                {
                    log.LogWarning("TMDB {Code} на {Path} — ліміт ретраїв вичерпано", (int)resp.StatusCode, pathAndQuery);
                    return null;
                }

                var delay = BackoffDelay(attempt, resp.StatusCode == HttpStatusCode.TooManyRequests
                    ? ParseRetryAfter(resp.Headers)
                    : null);
                log.LogInformation("TMDB {Code} на {Path} — ретрай через {Delay}с", (int)resp.StatusCode, pathAndQuery, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= _opt.MaxRetries)
                {
                    log.LogWarning(ex, "TMDB недоступний ({Path}) — здаюся після {N} ретраїв", pathAndQuery, _opt.MaxRetries);
                    return null;
                }
                await Task.Delay(BackoffDelay(attempt, null), ct);
            }
        }
    }

    private static TimeSpan? ParseRetryAfter(System.Net.Http.Headers.HttpResponseHeaders headers) =>
        headers.RetryAfter?.Delta is { } d ? d : null;

    private static TimeSpan BackoffDelay(int attempt, TimeSpan? retryAfter) =>
        retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 1с, 2с, 4с
}
