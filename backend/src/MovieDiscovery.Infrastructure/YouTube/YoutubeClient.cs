using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MovieDiscovery.Infrastructure.YouTube;

/// <summary>Опції YouTube Data API v3 (розділ Youtube конфігу; ключ — env YOUTUBE_API_KEY).</summary>
public sealed class YoutubeOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://www.googleapis.com/youtube/v3/";

    /// <summary>Скільки кандидатів повертати search.list (кожен search = 100 units квоти).</summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>Кеш search-відповідей у source_cache (24 год — акуратно з квотою).</summary>
    public int CacheTtlSeconds { get; set; } = 86_400;
}

/// <summary>Кандидат з search.list (part=snippet, type=video, videoDuration=long).</summary>
public sealed record YoutubeSearchItem(
    string VideoId, string Title, string Description, string ChannelTitle, string? PublishedAt);

/// <summary>Деталі з videos.list (part=snippet,contentDetails): duration ISO8601, definition, прев'ю.</summary>
public sealed record YoutubeVideo(
    string VideoId, string Title, string Description, string ChannelTitle,
    string Duration, string Definition, string? ThumbnailUrl);

/// <summary>Помилка YouTube Data API (квота/ключ/недоступність) — деградація без аварії.</summary>
public sealed class YoutubeApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Парсинг відповідей YouTube Data API + нормалізація метаданих. Чисті функції — покриті тестами.
/// </summary>
public static partial class YoutubeParser
{
    [GeneratedRegex("^P(?:(\\d+)D)?T?(?:(\\d+)H)?(?:(\\d+)M)?(?:(\\d+)S)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRx();

    /// <summary>ISO-8601 duration YouTube («PT1H32M10S») → TimeSpan; некоректне/нульове → null.</summary>
    public static TimeSpan? ParseDuration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        var m = DurationRx().Match(iso.Trim());
        if (!m.Success) return null;
        var days = Group(m, 1);
        var hours = Group(m, 2);
        var minutes = Group(m, 3);
        var seconds = Group(m, 4);
        if (days is null && hours is null && minutes is null && seconds is null) return null;
        var ts = new TimeSpan(
            days ?? 0, hours ?? 0, minutes ?? 0, seconds ?? 0);
        return ts > TimeSpan.Zero ? ts : null;
    }

    private static int? Group(Match m, int i) =>
        m.Groups[i].Success ? int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>definition contentDetails: sd=480, hd=1080 (4K спрощено до hd, як у контракті).</summary>
    public static int? QualityFromDefinition(string? definition) => definition switch
    {
        "hd" => 1080,
        "sd" => 480,
        _ => null,
    };

    /// <summary>Розібрати відповідь search.list; рядки без videoId пропускаються.</summary>
    public static List<YoutubeSearchItem> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<YoutubeSearchItem>();
        if (!doc.RootElement.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var it in arr.EnumerateArray())
        {
            var videoId = it.TryGetProperty("id", out var idEl)
                          && idEl.ValueKind == JsonValueKind.Object
                          && idEl.TryGetProperty("videoId", out var vid)
                          ? vid.GetString() : null;
            if (string.IsNullOrWhiteSpace(videoId)) continue;
            it.TryGetProperty("snippet", out var sn);
            items.Add(new YoutubeSearchItem(
                videoId,
                Str(sn, "title"), Str(sn, "description"), Str(sn, "channelTitle"),
                StrOrNull(sn, "publishedAt")));
        }
        return items;
    }

    /// <summary>Розібрати відповідь videos.list (part=snippet,contentDetails).</summary>
    public static List<YoutubeVideo> ParseVideos(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<YoutubeVideo>();
        if (!doc.RootElement.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return items;

        foreach (var it in arr.EnumerateArray())
        {
            var videoId = it.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(videoId)) continue;
            it.TryGetProperty("snippet", out var sn);
            it.TryGetProperty("contentDetails", out var cd);

            var thumb = (string?)null;
            if (sn.ValueKind == JsonValueKind.Object && sn.TryGetProperty("thumbnails", out var th))
                thumb = ThumbUrl(th, "high") ?? ThumbUrl(th, "medium") ?? ThumbUrl(th, "default");

            items.Add(new YoutubeVideo(
                videoId,
                Str(sn, "title"), Str(sn, "description"), Str(sn, "channelTitle"),
                Str(cd, "duration"), Str(cd, "definition"), thumb));
        }
        return items;
    }

    private static string Str(JsonElement e, string prop) => StrOrNull(e, prop) ?? "";

    private static string? StrOrNull(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(prop, out var p)
        && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? ThumbUrl(JsonElement thumbnails, string key) =>
        thumbnails.TryGetProperty(key, out var t)
        && t.ValueKind == JsonValueKind.Object
        && t.TryGetProperty("url", out var u)
        && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
}

/// <summary>
/// Клієнт YouTube Data API v3 (FR-20): search.list (type=video, videoDuration=long) +
/// videos.list (duration/thumbnails/definition). System.Text.Json; ключ — YoutubeOptions.ApiKey.
/// Реальні виклики лише в проді; тести — ін'єкція HttpMessageHandler-стаба.
/// </summary>
public sealed class YoutubeClient(HttpClient http, IOptions<YoutubeOptions> options, ILogger<YoutubeClient> log)
{
    private readonly YoutubeOptions _opt = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ApiKey);

    /// <summary>search.list: «повні викладки» — відео, type=video, videoDuration=long (~100 units).</summary>
    public async Task<IReadOnlyList<YoutubeSearchItem>> SearchFullAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        var url = $"search?part=snippet&type=video&videoDuration=long" +
                  $"&maxResults={Math.Clamp(maxResults, 1, 50)}" +
                  $"&q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(_opt.ApiKey)}";
        var json = await SendAsync(url, ct);
        log.LogDebug("YouTube search «{Query}»: {Count} кандидатів", query, YoutubeParser.ParseSearch(json).Count);
        return YoutubeParser.ParseSearch(json);
    }

    /// <summary>videos.list: duration/thumbnails/definition кандидатів (1 unit на виклик, ≤50 id).</summary>
    public async Task<IReadOnlyList<YoutubeVideo>> DetailsAsync(
        IReadOnlyList<string> videoIds, CancellationToken ct = default)
    {
        var ids = videoIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(50).ToList();
        if (ids.Count == 0) return [];
        var url = $"videos?part=snippet,contentDetails&id={Uri.EscapeDataString(string.Join(",", ids))}" +
                  $"&key={Uri.EscapeDataString(_opt.ApiKey)}";
        return YoutubeParser.ParseVideos(await SendAsync(url, ct));
    }

    private async Task<string> SendAsync(string relativeUrl, CancellationToken ct)
    {
        using var resp = await http.GetAsync(relativeUrl, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var reason = YoutubeErrorReason(body) ?? resp.StatusCode.ToString();
            log.LogWarning("YouTube API {Status}: {Reason}", (int)resp.StatusCode, reason);
            throw new YoutubeApiException($"YouTube API {(int)resp.StatusCode}: {reason}", (int)resp.StatusCode);
        }
        return body;
    }

    /// <summary>Коротке пояснення помилки з JSON-тіла (error.message / error.errors[].reason).</summary>
    public static string? YoutubeErrorReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString();
                if (err.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array
                    && errs.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.Object
                    && errs.EnumerateArray().First().TryGetProperty("reason", out var reason))
                    return reason.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }
}
