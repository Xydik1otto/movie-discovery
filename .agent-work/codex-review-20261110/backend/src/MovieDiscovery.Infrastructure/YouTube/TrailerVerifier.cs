using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Infrastructure.YouTube;

/// <summary>
/// Результат верифікації одного YouTube-відео: дані videos.list (part=snippet,contentDetails,status)
/// + безкоштовний oEmbed-чек (без квоти). oEmbed: 200 = живий і embeddable,
/// 401 = embed заборонено власником, 404 = видалено/не існує.
/// </summary>
public sealed record TrailerVerification(
    string YtId,
    bool Exists,
    string? Duration,
    bool? Embeddable,
    string? PrivacyStatus,
    string? UploadStatus,
    string? DefaultLanguage,
    string? DefaultAudioLanguage,
    string Title,
    string Description,
    string ChannelTitle,
    string? OembedStatus)
{
    /// <summary>
    /// Зведений verified_status: ok | gone | nonembeddable | private | NULL (невизначено —
    /// мережева помилка oEmbed/API: чесно лишаємо NULL, не вигадуємо статус).
    /// </summary>
    public string? VerifiedStatus
    {
        get
        {
            if (!Exists) return "gone";
            if (PrivacyStatus == "private") return "private";
            if (Embeddable == false) return "nonembeddable";
            return OembedStatus switch
            {
                "ok" => "ok",
                "nonembeddable" => "nonembeddable",
                "gone" => "gone",
                _ => null,
            };
        }
    }
}

/// <summary>
/// Канальний allowlist офіційності (студії / дистриб'ютори / перевірені трейлер-архіви).
/// Підказки з живого аудиту 09.11: Rotten Tomatoes Classic Trailers, WBRussia,
/// UA-Cinema, new kino ua, Interstellar Movie (офіційний фільм-канал Paramount).
/// </summary>
public static class TrailerChannelAllowlist
{
    private static readonly string[] Keys =
    [
        // Голлівудські студії та їхні офіційні канали
        "warner bros", "wb russia", "wbrussia", "universal pictures", "universal russia",
        "paramount pictures", "paramount movies", "sony pictures", "columbia pictures",
        "20th century studios", "walt disney studio", "disney", "pixar", "dreamworks",
        "marvel entertainment", "marvel studios", "lionsgate", "metro goldwyn mayer",
        "studiocanal", "focus features", "fox searchlight", "searchlight pictures",
        "a24", "amblin", "working title", "miramax", "new line cinema", "regency",
        // Офіційні трейлер-архіви (перевірені кураторські канали)
        "rotten tomatoes classic trailers", "movieclips", "fandango",
        // Українські дистриб'ютори/кінотеатри (живі зразки 09.11)
        "ua-cinema", "new kino ua", "arthouse traffic", "kinomania",
        "ukrainian film distribution", "film.ua",
        // Російські дистриб'ютори (офіційні ru-канали)
        "централ партнершип", "central partnership", "каропрокат", "наші фільми",
    ];

    public static bool IsOfficialChannel(string? channelTitle)
    {
        if (string.IsNullOrWhiteSpace(channelTitle)) return false;
        var ch = channelTitle.ToLowerInvariant();
        return Keys.Any(k => ch.Contains(k, StringComparison.Ordinal));
    }
}

/// <summary>
/// Верифікатор трейлерів: videos.list (status-частина: embeddable/privacy) + oEmbed-чек.
/// videos.list = 1 unit на виклик ≤50 id; oEmbed безкоштовний і квоту не витрачає.
/// </summary>
public sealed class TrailerVerifier(HttpClient http, YoutubeClient client, ILogger<TrailerVerifier> log)
{
    private const string OembedPath = "oembed";

    /// <summary>
    /// Верифікувати батч id (≤50): 1× videos.list(status) + oEmbed на кожен id.
    /// Відсутнє у videos.list = видалене (gone). Мережеві помилки oEmbed → OembedStatus=null.
    /// </summary>
    public async Task<IReadOnlyList<TrailerVerification>> VerifyAsync(
        IReadOnlyList<string> videoIds, CancellationToken ct = default)
    {
        var ids = videoIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(50).ToList();
        if (ids.Count == 0) return [];

        var details = await client.DetailsFullAsync(ids, ct);
        var byId = details.ToDictionary(v => v.VideoId, StringComparer.Ordinal);

        // oEmbed — паралельно з обмеженим ступенем (без квоти, але без шторму)
        var oembed = new ConcurrentDictionary<string, string?>();
        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (id, token) => oembed[id] = await OembedStatusAsync(id, token));

        var result = new List<TrailerVerification>(ids.Count);
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var v))
            {
                result.Add(new TrailerVerification(
                    id, Exists: true, v.Duration, v.Embeddable, v.PrivacyStatus, v.UploadStatus,
                    v.DefaultLanguage, v.DefaultAudioLanguage,
                    v.Title, v.Description, v.ChannelTitle, oembed.TryGetValue(id, out var s) ? s : null));
            }
            else
            {
                // немає у videos.list → видалено/недоступне
                result.Add(new TrailerVerification(
                    id, Exists: false, null, null, null, null, null, null, "", "", "",
                    oembed.TryGetValue(id, out var s) ? s : "gone"));
            }
        }
        return result;
    }

    /// <summary>Швидкий чек одного відео (швидкий шлях titles.trailer_yt_id): oEmbed 200/401/404.</summary>
    public async Task<string?> OembedStatusAsync(string videoId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{OembedPath}?url={Uri.EscapeDataString($"https://www.youtube.com/watch?v={videoId}")}&format=json";
            using var resp = await http.GetAsync(url, ct);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => "ok",
                HttpStatusCode.Unauthorized => "nonembeddable", // embed заборонено власником
                HttpStatusCode.NotFound => "gone",               // видалено
                _ => null,                                       // невизначено (429/5xx) — чесний NULL
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("oEmbed чек не вдався (video {Id}): {Msg}", videoId, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Мова трейлера: defaultAudioLanguage → defaultLanguage → title-регекс → NULL (unknown).
/// Коди зберігаються ISO-подібно: uk | ru | en | de | fr | es | ja | ko | zh | multi.
/// </summary>
public static class TrailerLang
{
    /// <summary>«uk-UA» → «uk»; «zxx»/порожнє → null.</summary>
    public static string? FromYouTubeLang(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var baseCode = code.Trim().Split('-')[0].ToLowerInvariant();
        return baseCode switch
        {
            "uk" => "uk",
            "ru" => "ru",
            "en" => "en",
            "de" => "de",
            "fr" => "fr",
            "es" => "es",
            "ja" => "ja",
            "ko" => "ko",
            "zh" => "zh",
            _ => null, // zxx (без мовного змісту) та невідомі — чесний unknown
        };
    }

    /// <summary>Коди YoutubeLangDetector (ukr|russ|eng|deu|fra|spa|jpn|kor|zho|multi) → ISO-подібні.</summary>
    public static string? FromDetector(string? detectorCode) => detectorCode switch
    {
        "ukr" => "uk",
        "russ" => "ru",
        "eng" => "en",
        "deu" => "de",
        "fra" => "fr",
        "spa" => "es",
        "jpn" => "ja",
        "kor" => "ko",
        "zho" => "zh",
        "multi" => "multi",
        _ => null,
    };

    /// <summary>Ланцюжок за замовчуванням для кандидата з videos.list.</summary>
    public static string? Detect(
        string? defaultAudioLanguage, string? defaultLanguage,
        string title, string description, string channelTitle) =>
        FromYouTubeLang(defaultAudioLanguage)
        ?? FromYouTubeLang(defaultLanguage)
        ?? FromDetector(YoutubeLangDetector.Detect(title, description, channelTitle));
}

/// <summary>
/// Фільтр трейлер-кандидатів для search-синку: мовні варіанти слова «трейлер» (en/uk/ru)
/// + guard від шуму (reaction/повний фільм/огляд), який вміє і укр/рос слово «трейлер»,
/// на відміну від YoutubeFilters.IsTrailer (лише en-regex — для «full movie»-запитів).
/// </summary>
public static partial class TrailerCandidateFilter
{
    [GeneratedRegex("trailer|трейлер|трілер|teaser|тизер|тізер", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailerWordRx();

    [GeneratedRegex("reaction|реакц|full movie|повний фільм|фільм повністю|ending explained|пояснення фільму|огляд фільму|розбір фільму",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseRx();

    /// <summary>Слово «трейлер/тизер» у назві/описі І не reaction/повне видання в назві.</summary>
    public static bool IsTrailerCandidate(string title, string description) =>
        (TrailerWordRx().IsMatch(title ?? "") || TrailerWordRx().IsMatch(description ?? ""))
        && !NoiseRx().IsMatch(title ?? "");

    /// <summary>Тривалість трейлера: відома, &gt; 0 і ≤ 5 хв (тизери/трейлери не бувають довші).</summary>
    public static bool DurationOk(TimeSpan? duration) =>
        duration is { } d && d > TimeSpan.Zero && d <= TimeSpan.FromMinutes(5);

    /// <summary>
    /// Матчинг тайтла: назва відео має містити суцільну послідовність токенів ОДНІЄЇ з назв
    /// (en | uk | ru — ru-трейлери називають фільм по-роськи) + рік відео в межах ±1 від року тайтла.
    /// </summary>
    public static bool MatchesAnyName(Core.Domain.Title t, string videoTitle, string videoDescription = "")
    {
        var videoTokens = TitleNormalizer.Normalize(videoTitle)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (videoTokens.Length == 0) return false;

        var matched =
            YoutubeMatcher.ContainsContiguous(videoTokens, Tokens(t.TitleEn))
            || YoutubeMatcher.ContainsContiguous(videoTokens, Tokens(t.TitleUk))
            || YoutubeMatcher.ContainsContiguous(videoTokens, Tokens(t.TitleRu));
        if (!matched) return false;

        var videoYear = YoutubeMatcher.ExtractYear(videoTitle) ?? YoutubeMatcher.ExtractYear(videoDescription);
        if (videoYear is { } vy && t.Year is { } ty && Math.Abs(vy - ty) > 1) return false;
        return true;
    }

    private static string[] Tokens(string? name) =>
        TitleNormalizer.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
