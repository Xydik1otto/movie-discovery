namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>
/// Рядок-джерело для злиття варіантів «де подивитись»:
/// з watch_links (source не зберігається — виводиться з provider) або з таблиці providers
/// (там source є: justwatch|megogo|plex|sweettv|youtube|internet-archive).
/// </summary>
public sealed record WatchOptionRow(
    string Provider,
    string Kind,
    string? Url,
    string? Source,
    string? Region,
    bool? IsAvailable = null,
    string? AudioLang = null,
    int? Quality = null,
    string? Dub = null);

/// <summary>
/// Уніфікований варіант «де подивитись»: {provider, kind (free|stream|rent|buy|ads), url, source, region?}.
/// </summary>
public sealed record WatchOption(
    string Provider,
    string Kind,
    string? Url,
    string Source,
    string? Region,
    bool? IsAvailable = null,
    string? AudioLang = null,
    int? Quality = null,
    string? Dub = null);

/// <summary>
/// Чисте злиття watch_links + providers (рішення власника 09.11: панель «Де подивитись»
/// показує ВСІ провайдери — безкоштовні й платні; фронт ховає платні за перемикачем).
/// Правила: kind нормалізується (subscription/flatrate → stream, free-with-ads → ads);
/// дедуп за (provider+url) — при колізії лишаємо «більш безкоштовний» kind, за рівного
/// kind — рядок watch_links (свіжий стан перевірки + метадані FR-19); порядок:
/// спочатку free/ads, потім платні (free, stream, rent, buy), всередині — за
/// пріоритетом джерела sweettv → megogo → plex → youtube → internet-archive → justwatch.
/// </summary>
public static class WatchOptionsMerger
{
    /// <summary>kind → ранг сортування: безкоштовні/ads першими, далі платні за зростанням.</summary>
    public static int KindRank(string kind) => kind switch
    {
        "free" => 0,
        "ads" => 1,
        "stream" => 2,
        "rent" => 3,
        "buy" => 4,
        _ => 5, // захист: невідомі kind — в кінці
    };

    /// <summary>source → ранг пріоритету: українські/легальні джерела першими, JustWatch-каталог — останнім.</summary>
    public static int SourceRank(string? source) => (source ?? "").Trim().ToLowerInvariant() switch
    {
        "sweettv" => 0,
        "megogo" => 1,
        "plex" => 2,
        "youtube" => 3,
        "internet-archive" => 4,
        "justwatch" => 5,
        _ => 6, // невідоме джерело — в кінці
    };

    /// <summary>
    /// Нормалізація kind до уніфікованого набору free|stream|rent|buy|ads.
    /// watch_links зберігають subscription, providers (TMDB) — stream/flatrate; ads-варіанти — окремо.
    /// Невідоме/порожнє → stream (платний перегляд: безпечний дефолт — не показуємо у безкоштовних).
    /// </summary>
    public static string NormalizeKind(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "free" => "free",
        "ads" or "free_with_ads" or "free-with-ads" or "avod" => "ads",
        "rent" => "rent",
        "buy" => "buy",
        _ => "stream", // stream | subscription | flatrate | невідоме
    };

    /// <summary>
    /// source, виведений з назви провайдера watch_links (там source-колонки нема).
    /// Невідомі провайдери → null (джерело беремо з providers-рядка або сам provider).
    /// </summary>
    public static string? DeriveSource(string? provider) => (provider ?? "").Trim().ToLowerInvariant() switch
    {
        "sweettv" => "sweettv",
        "megogo" => "megogo",
        "plex" => "plex",
        "youtube" => "youtube",
        "archive" => "internet-archive",
        "tmdb-streaming" => "justwatch", // легасі-провайдери TMDB — це дані JustWatch
        _ => null,
    };

    /// <summary>Злити watch_links і providers-каталог в уніфікований, дедуплікований та впорядкований список.</summary>
    public static List<WatchOption> Merge(IEnumerable<WatchOptionRow> watchLinks, IEnumerable<WatchOptionRow> providers)
    {
        var byKey = new Dictionary<string, WatchOption>(StringComparer.Ordinal);

        // 1) watch_links першими: свіжий стан перевірки + метадані озвучки/якості (FR-19).
        foreach (var w in watchLinks)
        {
            var provider = (w.Provider ?? "").Trim();
            if (provider.Length == 0) continue;
            var option = new WatchOption(
                provider, NormalizeKind(w.Kind), w.Url,
                DeriveSource(provider) ?? NonEmpty(w.Source) ?? provider.ToLowerInvariant(),
                w.Region, w.IsAvailable, w.AudioLang, w.Quality, w.Dub);
            Upsert(byKey, option);
        }

        // 2) providers-каталог — лише те, чого ще нема після дедупу.
        foreach (var p in providers)
        {
            var provider = (p.Provider ?? "").Trim();
            if (provider.Length == 0) continue;
            var option = new WatchOption(
                provider, NormalizeKind(p.Kind), p.Url,
                NonEmpty(p.Source) ?? DeriveSource(provider) ?? provider.ToLowerInvariant(),
                p.Region, p.IsAvailable, p.AudioLang, p.Quality, p.Dub);
            Upsert(byKey, option);
        }

        return [.. byKey.Values
            .OrderBy(o => KindRank(o.Kind))
            .ThenBy(o => SourceRank(o.Source))
            .ThenBy(o => o.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Url, StringComparer.Ordinal)];
    }

    /// <summary>Дедуп за (provider+url): колізія → «більш безкоштовний» kind перемагає
    /// (free/ads не ховаємо за перемикачем платних); метадані озвучки/якості переносимо.</summary>
    private static void Upsert(Dictionary<string, WatchOption> byKey, WatchOption option)
    {
        var key = DedupeKey(option.Provider, option.Url);
        if (!byKey.TryGetValue(key, out var existing))
        {
            byKey[key] = option;
            return;
        }
        if (KindRank(option.Kind) < KindRank(existing.Kind))
        {
            byKey[key] = option with
            {
                AudioLang = option.AudioLang ?? existing.AudioLang,
                Quality = option.Quality ?? existing.Quality,
                Dub = option.Dub ?? existing.Dub,
            };
        }
        // Рівний kind: перший перемагає (watch_links ідуть першими — свіжіший стан).
    }

    private static string DedupeKey(string provider, string? url) =>
        $"{provider.ToLowerInvariant()}\u0001{url?.Trim() ?? string.Empty}";

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
