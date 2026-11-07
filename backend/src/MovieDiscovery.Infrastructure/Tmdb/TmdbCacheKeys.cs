namespace MovieDiscovery.Infrastructure.Tmdb;

/// <summary>Формати ключів кешу (source_cache). Тестуються в MovieDiscovery.Tests.</summary>
public static class TmdbCacheKeys
{
    public const string Source = "tmdb";
    public const string OmdbSource = "omdb";

    /// <summary>Спискові виклики (popular/discover): "list:movie:popular:uk-UA:p3".</summary>
    public static string List(string kind, string type, string lang, int page) =>
        $"list:{type}:{kind}:{lang}:p{page}";

    /// <summary>Метадані тайтла (безстроковий кеш): "details:movie:123:uk-UA".</summary>
    public static string Details(string type, int tmdbId, string lang) =>
        $"details:{type}:{tmdbId}:{lang}";

    /// <summary>Провайдери регіону: "providers:movie:123:UA".</summary>
    public static string Providers(string type, int tmdbId, string region) =>
        $"providers:{type}:{tmdbId}:{region}";

    /// <summary>OMDb за назвою+роком: "omdb:the office:2005".</summary>
    public static string Omdb(string title, int? year) =>
        $"omdb:{title.Trim().ToLowerInvariant()}:{year?.ToString() ?? "-"}";
}
