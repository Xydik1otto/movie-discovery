using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>Опції Jikan (дзеркало MAL): BaseUrl можна перевизначити env JIKAN_URL (дзеркала/проксі). Джоба dormant.</summary>
public sealed class JikanOptions
{
    public string BaseUrl { get; set; } = "https://api.jikan.moe/v4";
}

/// <summary>
/// Клієнт Jikan v4 (дзеркало MyAnimeList). Тротлінг ≥400 мс між запитами (спільний singleton),
/// 404 → null, 429/5xx → ретраї з backoff. Каталог іде через source_cache.
/// </summary>
public sealed class JikanClient(
    HttpClient http,
    ICacheStore cache,
    JikanThrottle throttle,
    ILogger<JikanClient> log)
{
    public const string Source = "jikan";

    private const int CatalogMaxPages = 25;
    private const int UserListMaxPages = 10;  // пагінація списків користувача: 100/стор., макс 10 стор.
    private const int UserListPageSize = 100;

    /// <summary>TTL каталогу (top/season) — як TMDB-списки: 6 годин.</summary>
    private const int CatalogTtlSeconds = 21600;

    /// <summary>TTL списків користувача: короткий, щоб повторний імпорт брав свіжі дані.</summary>
    private const int UserListTtlSeconds = 600;

    public Task<List<JikanMedia>?> TopAnimeAsync(int pages, CancellationToken ct = default) =>
        FetchCatalogAsync("top/anime?filter=bypopularity", "top:anime:bypopularity", pages, ct);

    public Task<List<JikanMedia>?> CurrentSeasonAsync(int pages, CancellationToken ct = default) =>
        FetchCatalogAsync("seasons/now", "seasons:now", pages, ct);

    public Task<List<JikanMedia>?> TopMangaAsync(int pages, CancellationToken ct = default) =>
        FetchCatalogAsync("top/manga?filter=bypopularity", "top:manga:bypopularity", pages, ct);

    public Task<List<JikanUserEntry>?> UserAnimeListAsync(string username, CancellationToken ct = default) =>
        FetchUserListAsync($"users/{Uri.EscapeDataString(username.Trim())}/animelist", ct);

    public Task<List<JikanUserEntry>?> UserMangaListAsync(string username, CancellationToken ct = default) =>
        FetchUserListAsync($"users/{Uri.EscapeDataString(username.Trim())}/mangalist", ct);

    // ─── Сторінкові каталоги ───

    private async Task<List<JikanMedia>?> FetchCatalogAsync(string pathTemplate, string cachePrefix, int pages, CancellationToken ct)
    {
        var all = new List<JikanMedia>();
        for (var page = 1; page <= Math.Clamp(pages, 1, CatalogMaxPages); page++)
        {
            var path = $"{pathTemplate}&page={page}";
            var data = await GetPagedAsync<JikanMedia>(path, $"{cachePrefix}:p{page}", CatalogTtlSeconds, ct);
            if (data is null) return page == 1 ? null : all; // перша сторінка впала → невідомо, далі — що зібрали
            all.AddRange(data);
            if (data.Count == 0) break;
        }
        return all;
    }

    private async Task<List<JikanUserEntry>?> FetchUserListAsync(string path, CancellationToken ct)
    {
        var all = new List<JikanUserEntry>();
        for (var page = 1; page <= UserListMaxPages; page++)
        {
            var data = await GetPagedAsync<JikanUserEntry>(
                $"{path}?page={page}", $"user:{path}:p{page}", UserListTtlSeconds, ct);
            if (data is null) return page == 1 ? null : all;
            all.AddRange(data);
            if (data.Count < UserListPageSize) break;
        }
        return all;
    }

    // ─── Спільний механізм: кеш → HTTP з тротлінгом і ретраями ───

    private async Task<List<T>?> GetPagedAsync<T>(string pathAndQuery, string cacheKey, int ttlSeconds, CancellationToken ct)
    {
        var cached = await cache.GetAsync(Source, cacheKey, ct);
        if (cached is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<JikanPage<T>>(cached, TmdbJson.Options)?.Data;
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Пошкоджений кеш Jikan ({Key}) — тягну свіжі дані", cacheKey);
            }
        }

        var json = await SendWithRetryAsync(pathAndQuery, ct);
        if (json is null) return null;

        try
        {
            await cache.SetAsync(Source, cacheKey, json, ttlSeconds, ct);
            return JsonSerializer.Deserialize<JikanPage<T>>(json, TmdbJson.Options)?.Data;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Не вдалося розібрати відповідь Jikan ({Path})", pathAndQuery);
            return null;
        }
    }

    private Task<string?> SendWithRetryAsync(string pathAndQuery, CancellationToken ct) =>
        throttle.RunAsync(async () =>
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
                        log.LogInformation("Jikan 404: {Path}", pathAndQuery);
                        return null; // профіль/сторінка не знайдені — без ретраїв
                    }

                    if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                    {
                        log.LogWarning("Jikan {Code} на {Path} — без ретраїв", (int)resp.StatusCode, pathAndQuery);
                        return null;
                    }

                    if (attempt >= 3)
                    {
                        log.LogWarning("Jikan {Code} на {Path} — ліміт ретраїв вичерпано", (int)resp.StatusCode, pathAndQuery);
                        return null;
                    }

                    var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    log.LogInformation("Jikan {Code} на {Path} — ретрай через {Delay}с", (int)resp.StatusCode, pathAndQuery, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt >= 3)
                    {
                        log.LogWarning(ex, "Jikan недоступний ({Path}) — здаюся після ретраїв", pathAndQuery);
                        return null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }
        }, ct);
}
