namespace MovieDiscovery.Core.Search;

/// <summary>
/// Віртуальні категорії поверх unified-схеми titles (FR-19): category → SQL-умови
/// по genres/country/type. Константи — єдине джерело правди для SearchService (трансляція)
/// і контролера (валідація); не розмножують type.
/// </summary>
public static class VirtualCategories
{
    public static readonly string[] All = ["movie", "series", "anime", "manga", "tvshow", "cartoon", "dorama"];

    /// <summary>Категорії, які мапляться напряму на titles.type (решта — жанрово-країнові умови).</summary>
    public static readonly string[] TypeMapped = ["movie", "series", "anime", "manga"];

    /// <summary>GENRE_SETS: жанрові умови віртуальних категорій (канонічні lowercase жанри БД).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> GenreSets =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // tvshow: reality/talk/game/variety — суцільний набір через overlap (&&)
            ["tvshow"] = ["reality-tv", "talk-show", "game-show", "variety"],
            // cartoon: мультфільми й мультсеріали; аніме (type=anime) НЕ дублюється
            ["cartoon"] = ["animation"],
        };

    /// <summary>dorama: країни виробника KR|CN|JP + жанр drama (фільми й серіали).</summary>
    public static readonly string[] DoramaCountries = ["KR", "CN", "JP"];
    public const string DoramaGenre = "drama";

    /// <summary>Валідні значення audioLang-фільтра (FR-19).</summary>
    public static readonly string[] AudioLangs = ["ukr", "russ", "eng", "jpn", "kor", "zho", "original", "multi"];

    /// <summary>Валідні значення minQuality-фільтра (max height px).</summary>
    public static readonly int[] Qualities = [480, 720, 1080, 2160];

    /// <summary>Категорія валідна? (null/порожньо → не застосовується, це ок).</summary>
    public static bool IsValid(string? category) =>
        string.IsNullOrWhiteSpace(category) || All.Contains(category.Trim().ToLowerInvariant());
}
