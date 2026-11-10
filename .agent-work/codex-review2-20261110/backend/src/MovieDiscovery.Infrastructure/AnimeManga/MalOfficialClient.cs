using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>Опції офіційного MAL API v2. ClientId — безкоштовний ключ з myanimelist.net/apis.php (env MAL_CLIENT_ID).</summary>
public sealed class MalOfficialOptions
{
    public string ClientId { get; set; } = "";
}

/// <summary>Пейджинг MAL v2: absolute URL наступної сторінки (null — сторінок більше нема).</summary>
public sealed class MalPaging
{
    [JsonPropertyName("next")] public string? Next { get; set; }
    [JsonPropertyName("previous")] public string? Previous { get; set; }
}

/// <summary>Сторінка відповіді MAL v2: data + paging.</summary>
public sealed class MalPage<T>
{
    [JsonPropertyName("data")] public List<T> Data { get; set; } = [];
    [JsonPropertyName("paging")] public MalPaging? Paging { get; set; }
}

/// <summary>
/// Нода MAL v2 (поля, які використовуємо). DTO nullable-толерантний: MAL опускає відсутні поля.
/// average_episode_duration — у СЕКУНДАХ; start_date — "2013-04-07" | "2013-04" | "2013".
/// </summary>
public sealed class MalNode
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("main_picture")] public MalPicture? MainPicture { get; set; }
    [JsonPropertyName("alternative_titles")] public MalAlternativeTitles? AlternativeTitles { get; set; }
    [JsonPropertyName("start_date")] public string? StartDate { get; set; }
    [JsonPropertyName("end_date")] public string? EndDate { get; set; }
    [JsonPropertyName("synopsis")] public string? Synopsis { get; set; }
    [JsonPropertyName("mean")] public double? Mean { get; set; }                       // 0–10
    [JsonPropertyName("num_list_users")] public int? NumListUsers { get; set; }
    [JsonPropertyName("media_type")] public string? MediaType { get; set; }            // tv|movie|ova|ona|special|manga|light_novel|one_shot|manhwa|manhua|music|doujin|oel
    [JsonPropertyName("status")] public string? Status { get; set; }                   // currently_airing|finished_airing|not_yet_aired|currently_publishing|finished_publishing|...
    [JsonPropertyName("genres")] public List<MalNamed>? Genres { get; set; }
    [JsonPropertyName("studios")] public List<MalNamed>? Studios { get; set; }
    [JsonPropertyName("rating")] public string? Rating { get; set; }                   // g | pg_13 | rx — Hazel: зберігаємо тільки назву
    [JsonPropertyName("num_episodes")] public int? NumEpisodes { get; set; }
    [JsonPropertyName("average_episode_duration")] public int? AverageEpisodeDuration { get; set; }
}

public sealed class MalPicture
{
    [JsonPropertyName("large")] public string? Large { get; set; }
    [JsonPropertyName("medium")] public string? Medium { get; set; }
}

public sealed class MalAlternativeTitles
{
    [JsonPropertyName("en")] public string? En { get; set; }
    [JsonPropertyName("ja")] public string? Ja { get; set; }
}

public sealed class MalNamed
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>
/// Клієнт офіційного MAL API v2 (https://api.myanimelist.net/v2). Заголовок X-MAL-CLIENT-ID
/// з env MAL_CLIENT_ID; порожній → SyncSkip (шар пропускається з warn, це не помилка синку).
/// Тротлінг 350 мс; каталог іде через source_cache (TTL 6 годин), пагінація по paging.next.
/// </summary>
public sealed class MalOfficialClient(
    HttpClient http,
    ICacheStore cache,
    MalOfficialThrottle throttle,
    IOptions<MalOfficialOptions> options,
    ILogger<MalOfficialClient> log)
{
    public const string Source = "mal-official";

    public static readonly string[] Seasons = ["winter", "spring", "summer", "fall"];

    private const string Fields =
        "id,title,main_picture,alternative_titles{en,ja},start_date,end_date,synopsis,mean," +
        "num_list_users,media_type,genres,studios,rating,num_episodes,average_episode_duration,status";

    private const int PageSize = 100;
    private const int MaxPages = 10;
    private const int CatalogTtlSeconds = 21600; // 6 годин — як каталоги Jikan/TMDB

    private readonly MalOfficialOptions _opt = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ClientId);

    /// <summary>SyncSkip-гейт: MAL_CLIENT_ID не задано → шар пропускається з warn (не фейлить синк).</summary>
    public void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new SyncSkipException(
                "MAL official пропущено: MAL_CLIENT_ID не задано. Зареєструй безкоштовний client ID на myanimelist.net/apis.php і пропиши MAL_CLIENT_ID у .env");
    }

    /// <summary>Поточний сезон за місяцем (дефолт season у sync/mal-season).</summary>
    public static string SeasonForMonth(int month) => month switch
    {
        12 or 1 or 2 => "winter",
        >= 3 and <= 5 => "spring",
        >= 6 and <= 8 => "summer",
        _ => "fall",
    };

    /// <summary>Сезонний шар: anime/season/{year}/{season} (тільки аніме; 100/стор., пагінація по next).</summary>
    public async Task<List<MalNode>?> SeasonAsync(int year, string season, int pages, CancellationToken ct = default)
    {
        EnsureConfigured();
        var path = SeasonUrl(year, season); // валідація season/year всередині
        return await FetchPagesAsync(path, $"season:{year}:{NormalizeSeason(season)}", pages, ct);
    }

    public async Task<List<MalNode>?> TopAnimeAsync(int pages, string ranking = "all", CancellationToken ct = default)
    {
        EnsureConfigured();
        return await FetchPagesAsync(RankingUrl("anime", ranking), "top:anime", pages, ct);
    }

    public async Task<List<MalNode>?> TopMangaAsync(int pages, string ranking = "all", CancellationToken ct = default)
    {
        EnsureConfigured();
        return await FetchPagesAsync(RankingUrl("manga", ranking), "top:manga", pages, ct);
    }

    // ─── URL-будування і валідація (public static — покриваються тестами без HTTP) ───

    public static string SeasonUrl(int year, string season)
    {
        var s = NormalizeSeason(season)
            ?? throw new ArgumentException(
                $"season має бути winter | spring | summer | fall, а не «{season}»", nameof(season));
        if (year is < 1900 or > 2999)
            throw new ArgumentException($"year має бути 1900–2999, а не {year}", nameof(year));
        return $"anime/season/{year}/{s}?limit={PageSize}&fields={Fields}";
    }

    public static string RankingUrl(string kind, string ranking = "all") =>
        $"{kind}/ranking?ranking_type={ranking}&limit={PageSize}&fields={Fields}";

    public static string? NormalizeSeason(string? season)
    {
        if (string.IsNullOrWhiteSpace(season)) return null;
        var s = season.Trim().ToLowerInvariant();
        return Seasons.Contains(s) ? s : null;
    }

    // ─── Пагінація по paging.next: перша впала сторінка → null, далі — що зібрали ───

    private async Task<List<MalNode>?> FetchPagesAsync(string firstPath, string cachePrefix, int pages, CancellationToken ct)
    {
        var all = new List<MalNode>();
        var path = firstPath;
        for (var page = 1; page <= Math.Clamp(pages, 1, MaxPages); page++)
        {
            var data = await GetPageAsync(path, $"{cachePrefix}:p{page}", ct);
            if (data is null) return page == 1 ? null : all;
            all.AddRange(data.Data);
            var next = data.Paging?.Next;
            if (next is null || data.Data.Count == 0) break;
            path = next; // paging.next — абсолютний URL з тими самими fields/limit
        }
        return all;
    }

    private async Task<MalPage<MalNode>?> GetPageAsync(string pathOrUrl, string cacheKey, CancellationToken ct)
    {
        var cached = await cache.GetAsync(Source, cacheKey, ct);
        if (cached is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<MalPage<MalNode>>(cached, TmdbJson.Options);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Пошкоджений кеш MAL official ({Key}) — тягну свіжі дані", cacheKey);
            }
        }

        var json = await SendWithRetryAsync(pathOrUrl, ct);
        if (json is null) return null;

        try
        {
            await cache.SetAsync(Source, cacheKey, json, CatalogTtlSeconds, ct);
            return JsonSerializer.Deserialize<MalPage<MalNode>>(json, TmdbJson.Options);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Не вдалося розібрати відповідь MAL official ({Path})", pathOrUrl);
            return null;
        }
    }

    private Task<string?> SendWithRetryAsync(string pathOrUrl, CancellationToken ct) =>
        throttle.RunAsync(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var resp = await http.GetAsync(pathOrUrl, ct);
                    if (resp.IsSuccessStatusCode)
                        return await resp.Content.ReadAsStringAsync(ct);

                    if (resp.StatusCode is HttpStatusCode.NotFound
                        or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        // 401/403 — client id відсутній/некоректний; далі смислу нема
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        log.LogWarning("MAL official {Code} на {Path} — без ретраїв: {Body}",
                            (int)resp.StatusCode, pathOrUrl, body.Length > 300 ? body[..300] : body);
                        return null;
                    }

                    if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                    {
                        log.LogWarning("MAL official {Code} на {Path} — без ретраїв", (int)resp.StatusCode, pathOrUrl);
                        return null;
                    }

                    if (attempt >= 3)
                    {
                        log.LogWarning("MAL official {Code} на {Path} — ліміт ретраїв вичерпано", (int)resp.StatusCode, pathOrUrl);
                        return null;
                    }

                    var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    log.LogInformation("MAL official {Code} на {Path} — ретрай через {Delay}с",
                        (int)resp.StatusCode, pathOrUrl, delay.TotalSeconds);
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
                        log.LogWarning(ex, "MAL official недоступний ({Path}) — здаюся після ретраїв", pathOrUrl);
                        return null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }
        }, ct);
}
