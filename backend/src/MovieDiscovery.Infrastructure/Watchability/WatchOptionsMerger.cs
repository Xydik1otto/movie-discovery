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
    string? Dub = null,
    string? AudioLangs = null,
    string? SubLangs = null,
    string? Price = null,
    string? Currency = null);

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
    string? Dub = null,
    string? AudioLangs = null,
    string? SubLangs = null,
    string? Price = null,
    string? Currency = null);

/// <summary>
/// Чисте злиття watch_links + providers (рішення власника 09.11: панель «Де подивитись»
/// показує ВСІ провайдери — безкоштовні й платні; фронт ховає платні за перемикачем).
/// Правила: kind нормалізується (subscription/flatrate → stream, free-with-ads → ads);
/// UX-04: дедуп за (provider+url+мова-сигнатура) — різні мовні варіанти не губляться,
/// при колізії однієї сигнатури лишаємо «більш безкоштовний» kind, за рівного
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
        var merged = new List<WatchOption>();
        var byKey = new Dictionary<string, WatchOption>(StringComparer.Ordinal);

        // 1) watch_links першими: свіжий стан перевірки + метадані озвучки/якості (FR-19).
        foreach (var w in watchLinks)
        {
            var provider = (w.Provider ?? "").Trim();
            if (provider.Length == 0) continue;
            var option = new WatchOption(
                provider, NormalizeKind(w.Kind), w.Url,
                DeriveSource(provider) ?? NonEmpty(w.Source) ?? provider.ToLowerInvariant(),
                w.Region, w.IsAvailable, w.AudioLang, w.Quality, w.Dub,
                CleanCsv(w.AudioLangs), CleanCsv(w.SubLangs), NonEmpty(w.Price), NonEmpty(w.Currency));
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
                p.Region, p.IsAvailable, p.AudioLang, p.Quality, p.Dub,
                CleanCsv(p.AudioLangs), CleanCsv(p.SubLangs), NonEmpty(p.Price), NonEmpty(p.Currency));
            Upsert(byKey, option);
        }

        ReconcileUnknownSigs(byKey);
        merged.AddRange(byKey.Values);

        return [.. merged
            .OrderBy(o => KindRank(o.Kind))
            .ThenBy(o => SourceRank(o.Source))
            .ThenBy(o => o.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Url, StringComparer.Ordinal)
            .ThenBy(o => o.AudioLangs, StringComparer.Ordinal)];
    }

    /// <summary>Дедуп за (provider+url+мова-сигнатура): колізія ОДНІЄЇ сигнатури →
    /// «більш безкоштовний» kind перемагає (free/ads не ховаємо за перемикачем платних);
    /// метадані озвучки/якості/ціни переносимо, мовні набори об'єднуємо (union) —
    /// жоден варіант мови не губиться. Сигнатура — WatchLanguages.CanonicalKey
    /// (єдина нормалізація з upsert-ключем і міграційним UNIQUE-індексом).</summary>
    private static void Upsert(Dictionary<string, WatchOption> byKey, WatchOption option)
    {
        var key = DedupeKey(option.Provider, option.Url, option.AudioLangs, option.AudioLang);
        if (!byKey.TryGetValue(key, out var existing))
        {
            byKey[key] = option;
            return;
        }
        if (KindRank(option.Kind) < KindRank(existing.Kind))
        {
            // свіжіший/безкоштовніший рядок перемагає, метадані попереднього не губляться
            byKey[key] = option with
            {
                AudioLang = option.AudioLang ?? existing.AudioLang,
                Quality = option.Quality ?? existing.Quality,
                Dub = option.Dub ?? existing.Dub,
                AudioLangs = Union(option.AudioLangs, existing.AudioLangs),
                SubLangs = Union(option.SubLangs, existing.SubLangs),
                Price = option.Price ?? existing.Price,
                Currency = option.Currency ?? existing.Currency,
            };
        }
        else
        {
            // Рівний/гірший kind: перший (watch_links — свіжіший стан) лишається,
            // мови об'єднуємо, відсутні метадані добираємо (з легасі-полів теж).
            var legacyAudio = existing.AudioLang is { Length: > 0 } la ? LegacyToIso(la) : null;
            var legacyAudio2 = option.AudioLang is { Length: > 0 } la2 ? LegacyToIso(la2) : null;
            byKey[key] = existing with
            {
                AudioLang = existing.AudioLang ?? option.AudioLang,
                Quality = existing.Quality ?? option.Quality,
                Dub = existing.Dub ?? option.Dub,
                AudioLangs = Union(existing.AudioLangs, Union(legacyAudio, Union(option.AudioLangs, legacyAudio2))),
                SubLangs = Union(existing.SubLangs, option.SubLangs),
                Price = existing.Price ?? option.Price,
                Currency = existing.Currency ?? option.Currency,
            };
        }
    }

    /// <summary>Легасі-коди watch_links (ukr/russ/eng/…) → ISO для union з новими наборами.</summary>
    private static string? LegacyToIso(string legacy) => WatchLanguages.Normalize(legacy);

    /// <summary>
    /// Рядок з НЕВІДОМОЮ мовою (порожня сигнатура) того самого provider+url, для якого
    /// вже є рядок з відомими мовами НЕ ГІРШИМ kind — дублює посилання без жодної
    /// нової інформації → дропається. Якщо unknown-рядок БІЛЬШ безкоштовний — лишається:
    /// конфлікт джерел (каталог каже free, перевірена сторінка — платне) видно обома рядками.
    /// </summary>
    private static void ReconcileUnknownSigs(Dictionary<string, WatchOption> byKey)
    {
        var groups = byKey.Values
            .GroupBy(o => BaseKey(o.Provider, o.Url), StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var rows = group.ToList();
            if (rows.Count < 2) continue;
            var knownMinRank = rows
                .Where(r => EffectiveSig(r.AudioLangs, r.AudioLang).Length > 0)
                .Select(r => KindRank(r.Kind))
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            if (knownMinRank == int.MaxValue) continue;
            foreach (var u in rows.Where(r => EffectiveSig(r.AudioLangs, r.AudioLang).Length == 0).ToList())
                if (KindRank(u.Kind) >= knownMinRank)
                    byKey.Remove(DedupeKey(u.Provider, u.Url, u.AudioLangs, u.AudioLang));
        }
    }

    /// <summary>
    /// Ефективна мова-сигнатура РЯДКА для дедупу: новий csv (CanonicalKey), а коли він
    /// порожній — легасі одиночне audio_lang ('eng' → 'en'). Легасі-рядок із відомою мовою
    /// НЕ «невідомий»: він колайде з JW-рядком тієї ж мови й переносить свої метадані
    /// (review Codex: легасі-метадані не губляться при появі нових мовних полів).
    /// </summary>
    private static string EffectiveSig(string? audioLangs, string? audioLang)
    {
        var sig = WatchLanguages.CanonicalKey(audioLangs);
        if (sig.Length > 0) return sig;
        return audioLang is { Length: > 0 } la ? WatchLanguages.Normalize(la) ?? string.Empty : string.Empty;
    }

    /// <summary>Ключ дедупу: provider + url + ефективна мова-сигнатура (NULL-мова → «»)
    /// — та сама нормалізація, що в upsert-ключа WatchLinkUpsertCore і міграційному індексі.</summary>
    private static string DedupeKey(string provider, string? url, string? audioLangs, string? audioLang = null) =>
        $"{BaseKey(provider, url)}\u0002{EffectiveSig(audioLangs, audioLang)}";

    private static string BaseKey(string provider, string? url) =>
        $"{provider.ToLowerInvariant()}\u0001{url?.Trim() ?? string.Empty}";

    /// <summary>csv мов — union з ігноруванням порожніх (null = невідома, не «порожньо»).</summary>
    private static string? Union(string? a, string? b) => WatchLanguages.Union(a, b);

    /// <summary>csv з джерела: порожнє/біле → null (ніколи не порожній рядок — порожнє = невідома).</summary>
    private static string? CleanCsv(string? csv) => string.IsNullOrWhiteSpace(csv) ? null : csv.Trim();

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
