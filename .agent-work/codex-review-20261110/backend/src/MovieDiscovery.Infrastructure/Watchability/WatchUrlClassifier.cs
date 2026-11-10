using System.Text.RegularExpressions;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>
/// Класифікатор watch-URL (UX-04): «direct» — deep-link на конкретний тайтл
/// (ID/UUID/слаг тайтла в URL), «find» — все інше (головна/каталог/пошук/порожньо):
/// таке посилання НЕ подається як пряме переглядання, а чесно підписується
/// «Знайти на сервісу» на фронті. url:null → find (не direct за жодних умов).
/// Категорії повторюють структурну класифікацію хвилі-1 (jw_classify.py): netflix_title,
/// apple_umc, crunchy, uuid, numeric, slug — але без «deep_slugish»-фолбеку для
/// колекційних/категорійних шляхів. Чисті функції — покриті тестами.
/// </summary>
public static partial class WatchUrlClassifier
{
    public const string Direct = "direct";
    public const string Find = "find";

    [GeneratedRegex(@"netflix\.com/title/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex NetflixTitle();

    [GeneratedRegex(@"/um[cd]\.cmc\.[a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex AppleUmc();

    [GeneratedRegex(@"/watch/[a-z0-9]{8,}/", RegexOptions.IgnoreCase)]
    private static partial Regex CrunchyrollWatch();

    [GeneratedRegex(@"/[a-z-]+/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidPath();

    [GeneratedRegex(@"/(?:movie|movies|title|watch|video|details?|detail|film|serial|series|show|view|episode|episodes|tv|anime|manga)/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex NumericId();

    [GeneratedRegex(@"/(?:collection|collections|category|categories|genre|genres|channel|list|lists|discover|browse|home|account|search|more-services)/", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionPath();

    [GeneratedRegex(@"(?:[/?&#])(?:search|find|query|results?)(?:[=/&#?]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SearchHint();

    [GeneratedRegex(@"^/(ua|ru|en|us|uk|pl|ua-uk|ru-ru|en-us)?/?$|^/(browse|home|movies|series|tv|films|list|lists|collections?|search|discover|account)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex Rootish();

    /// <summary>Сегменти-контейнери: не є ідентифікатором тайтла; ігноруємо при пошуку слага.</summary>
    private static readonly HashSet<string> GenericSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "ua", "ru", "en", "us", "uk", "pl", "ua-uk", "ru-ru", "en-us", "pt-br", "de", "fr", "es", "it",
        "movie", "movies", "title", "watch", "video", "videos", "details", "detail", "film", "films",
        "serial", "series", "show", "shows", "view", "episode", "episodes", "tv", "anime", "manga",
        "web", "content", "catalog", "store", "rent", "buy", "stream", "streaming", "play", "player",
        "mediacenter", "media", "p", "m", "w", "e", "ua-uk-movie",
    };

    /// <summary>direct | find. null/порожнє → find (не рендеримо як пряме переглядання).</summary>
    public static string Classify(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Find;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.Host.Length == 0)
            return Find;

        var full = url.Trim();
        var path = SafeUnescape(uri.AbsolutePath);

        if (CollectionPath().Search(path)) return Find; // колекції/категорії/пошук — не тайтл
        if (SearchHint().Search(full)) return Find;
        if (Rootish().Search(path.TrimEnd('/'))) return Find;

        // явні deep-ID патерни (перевірені на реальних даних хвилі-1)
        if (NetflixTitle().Search(full)) return Direct;
        if (AppleUmc().Search(full)) return Direct;
        if (CrunchyrollWatch().Search(full)) return Direct;
        if (UuidPath().Search(full)) return Direct;
        if (NumericId().Search(path)) return Direct;

        // slug-подібний сегмент: megogo /ua/view/1003951-Dictator.html, plex /movie/mission-to-mars-2000,
        // rakuten /movies/the-matrix, HBO епізодні /show/{uuid}/seasons/...
        var segments = path.Split('/')
            .Select(s => s.Trim().Replace(".html", "").Replace(".htm", ""))
            .Where(s => s.Length > 0)
            .ToList();
        foreach (var seg in segments)
        {
            if (GenericSegments.Contains(seg)) continue;
            var hasLetters = seg.Any(char.IsLetter);
            var digits = seg.Count(char.IsDigit);
            if ((hasLetters && seg.Length >= 3) || digits >= 2) return Direct;
        }
        return Find;
    }

    private static string SafeUnescape(string path)
    {
        try { return Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return path; }
    }

    /// <summary>Регекс-пошук як у jw-класифікаторі (Match.Success замість IsMatch для GeneratedRegex).</summary>
    private static bool Search(this Regex re, string input) => re.Match(input).Success;
}
