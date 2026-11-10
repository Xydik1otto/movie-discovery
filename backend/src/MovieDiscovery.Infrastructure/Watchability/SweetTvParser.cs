using System.Text.Json;
using System.Text.RegularExpressions;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>Елемент каталогу sweet.tv (сторінка /ua-uk/movie).</summary>
public sealed record SweetCatalogItem(
    int Id,
    string Slug,
    string? NameUk,
    string? Description,
    string[] GenresUk,
    string Url);

/// <summary>Картка фільма sweet.tv: назва/рік/kind доступу (free | subscription).</summary>
public sealed record SweetMovieCard(
    int Id,
    string Slug,
    string NameUk,
    string? TitleEn,
    int? Year,
    string Kind,
    string Url);

/// <summary>
/// Парсери sweet.tv за РЕАЛЬНОЮ структурою зразків (FR-14):
/// каталог і картки — Nuxt SSR з JSON-LD (schema.org):
///   каталог: ItemList.itemListElement[@type=Movie] (url/name/description/genre);
///   картка:  @graph[WebPage (description з маркером доступу) + Movie (name, dateCreated=рік)];
///   фолбек — href="/ua-uk/movie/{id}-{slug}".
/// kind визначається ЛИШЕ за текстом опису сторінки (WebPage.description / meta description):
/// «у підписці»/«передплат» → subscription; «безкоштовн» → free; інакше — subscription (за замовчуванням).
/// Маркери кнопок/хедера («Спробувати безкоштовно») ігноруються — вони є на всіх сторінках.
/// </summary>
public static partial class SweetTvParser
{
    public const string KindFree = "free";
    public const string KindSubscription = "subscription";

    [GeneratedRegex("/ua-uk/movie/(?<id>\\d+)-(?<slug>[a-z0-9-]+)")]
    private static partial Regex MovieHref();

    [GeneratedRegex("\\((?<year>\\d{4})\\)")]
    private static partial Regex YearInTitle();

    // "Ти - космос ( 2025 ) – U Are the Universe дивитися фільм в Україні на Sweet TV."
    [GeneratedRegex("\\(\\s*\\d{4}\\s*\\)\\s*[–—-]\\s*(?<orig>.+?)\\s+дивитися")]
    private static partial Regex OriginalTitle();

    private static readonly Regex LdJson = new(
        "<script[^>]*type=\"application/ld\\+json\"[^>]*>(?<json>.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static List<SweetCatalogItem> ParseCatalog(string html)
    {
        var items = new List<SweetCatalogItem>();
        foreach (var json in LdJsonBlobs(html))
        {
            using var doc = TryParse(json);
            if (doc is null) continue;
            var root = doc.RootElement;
            if (!IsType(root, "ItemList")) continue;
            if (!root.TryGetProperty("itemListElement", out var els) || els.ValueKind != JsonValueKind.Array) continue;

            foreach (var el in els.EnumerateArray())
            {
                if (!IsType(el, "Movie")) continue;
                var url = el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                if (url is null) continue;
                var m = MovieHref().Match(url);
                if (!m.Success) continue;

                items.Add(new SweetCatalogItem(
                    int.Parse(m.Groups["id"].Value),
                    m.Groups["slug"].Value,
                    Str(el, "name"),
                    Str(el, "description"),
                    StrArray(el, "genre"),
                    url));
            }
        }

        if (items.Count == 0)
        {
            // Фолбек: посилання каталогу без JSON-LD (назва дістанеться з картки фільма)
            var seen = new HashSet<int>();
            foreach (Match m in MovieHref().Matches(html))
            {
                var id = int.Parse(m.Groups["id"].Value);
                if (!seen.Add(id)) continue;
                items.Add(new SweetCatalogItem(id, m.Groups["slug"].Value, null, null, [], $"https://sweet.tv/ua-uk/movie/{id}-{m.Groups["slug"].Value}"));
            }
        }
        return items;
    }

    public static SweetMovieCard? ParseMovieCard(string html)
    {
        string? nameUk = null, description = null, urlPath = null;
        int? year = null;

        foreach (var json in LdJsonBlobs(html))
        {
            using var doc = TryParse(json);
            if (doc is null) continue;
            var root = doc.RootElement;

            if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in graph.EnumerateArray())
                {
                    if (IsType(node, "WebPage"))
                    {
                        description ??= Str(node, "description");
                        nameUk ??= Str(node, "name");
                        urlPath ??= Str(node, "url");
                    }
                    else if (IsType(node, "Movie"))
                    {
                        nameUk = Str(node, "name") ?? nameUk;
                        urlPath = Str(node, "url") ?? urlPath;
                        var dc = Str(node, "dateCreated");
                        if (dc is { Length: 4 } && int.TryParse(dc, out var y)) year = y;
                    }
                }
            }
            else if (IsType(root, "Movie") && nameUk is null)
            {
                nameUk = Str(root, "name");
                urlPath ??= Str(root, "url");
            }
        }

        // фолбеки: meta description + <title>
        description ??= MetaDescription(html);
        var titleTag = Regex.Match(html, "<title>(?<t>.*?)</title>", RegexOptions.Singleline).Groups["t"].Value;
        year ??= int.TryParse(YearInTitle().Match(titleTag).Groups["year"].Value, out var ty) ? ty : null;
        nameUk ??= TitleFromTag(titleTag);

        if (nameUk is null || nameUk.Trim().Length == 0) return null;

        // id/slug: з JSON-LD url, далі з canonical
        var path = urlPath ?? Canonical(html);
        var m = MovieHref().Match(path ?? "");
        var id = m.Success ? int.Parse(m.Groups["id"].Value) : 0;
        var slug = m.Success ? m.Groups["slug"].Value : "";
        if (id == 0) return null;

        return new SweetMovieCard(
            id,
            slug,
            nameUk.Trim(),
            OriginalEn(description, nameUk),
            year,
            DetectKind(description),
            $"https://sweet.tv/ua-uk/movie/{id}-{slug}");
    }

    /// <summary>Мови озвучки/субтитрів сторінки тайтла sweet.tv (UX-04): csv ISO-639-1 або null.</summary>
    public sealed record SweetMovieLanguages(string? AudioLangs, string? SubLangs);

    /// <summary>
    /// Блок «Аудіодоріжки:»/«Субтитри:» на view-сторінці (class movie-available-info):
    /// &lt;a href="/ua-uk/movies/filters/audiotrack/{slug}"&gt;Назва&lt;/a&gt; — слаг = мова сторінки
    /// тайтла (ukrajinska/english/russkiy/...). СЕМАНТИКА per-movie: це мови САМЕ ЦЬОГО
    /// тайтла (контрольна перевірка хвилі-2: тайтл без рос. озвучки не має russkiy-анкора).
    /// Нормалізація WatchLanguages; невідомі слаги відкидаємо (не вигадуємо), дублі злиті.
    /// </summary>
    public static SweetMovieLanguages ParseLanguages(string html)
    {
        var audio = new List<string>();
        var subs = new List<string>();
        foreach (Match m in LanguageFilterHref().Matches(html ?? ""))
        {
            var target = m.Groups["kind"].Value;
            var norm = WatchLanguages.Normalize(m.Groups["slug"].Value);
            if (norm is null) continue; // невідомий слаг — чесний пропуск
            if (target == "audiotrack") audio.Add(norm);
            else subs.Add(norm);
        }
        return new SweetMovieLanguages(
            WatchLanguages.NormalizeSet(audio),
            WatchLanguages.NormalizeSet(subs));
    }

    // /ua-uk/movies/filters/audiotrack/ukrajinska | /ua-uk/movies/filters/subtitle/english
    [GeneratedRegex("filters/(?<kind>audiotrack|subtitle)/(?<slug>[a-z-]+)")]
    private static partial Regex LanguageFilterHref();

    /// <summary>kind за маркерами опису сторінки (не всього HTML!).</summary>
    public static string DetectKind(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return KindSubscription;
        var d = description.ToLowerInvariant();
        if (d.Contains("безкоштовн")) return KindFree;
        if (d.Contains("у підписці") || d.Contains("передплат") || d.Contains("підпис")) return KindSubscription;
        return KindSubscription;
    }

    /// <summary>uk-жанри sweet.tv → канонічні en lowercase (відомі — за мапою/аліасами, невідомі пропускаємо).</summary>
    public static string[] NormalizeGenres(IEnumerable<string> genresUk)
    {
        var result = new List<string>();
        foreach (var g in genresUk)
        {
            var genre = g.Trim();
            if (genre.Length == 0) continue;
            var en = TmdbGenres.NormalizeToEn(genre)
                     ?? UkGenreAliases.GetValueOrDefault(genre)
                     ?? TmdbGenres.NormalizeToEn(TrimPlural(genre))
                     ?? (genre.Length > 3 && genre.EndsWith("ії", StringComparison.Ordinal)
                         ? TmdbGenres.NormalizeToEn(genre[..^2] + "ія") // «Комедії» → «Комедія»
                         : null);
            if (en is null || result.Contains(en, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(en);
        }
        return [.. result];
    }

    /// <summary>uk-множини жанрів sweet.tv, які не виводяться простим відкиданням літери.</summary>
    private static readonly Dictionary<string, string> UkGenreAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["комедії"] = "comedy",
        ["драми"] = "drama",
        ["бойовики"] = "action",
        ["документальні"] = "documentary",
        ["мюзикли"] = "music",
    };

    private static string TrimPlural(string g) =>
        g.Length > 3 && (g.EndsWith("и", StringComparison.Ordinal)
                         || g.EndsWith("і", StringComparison.Ordinal)
                         || g.EndsWith("ї", StringComparison.Ordinal))
            ? g[..^1]
            : g;

    private static string? OriginalEn(string? description, string nameUk)
    {
        if (description is null) return null;
        var m = OriginalTitle().Match(description);
        if (!m.Success) return null;
        var orig = m.Groups["orig"].Value.Trim();
        if (orig.Length == 0 || orig.Length > 200) return null;
        if (string.Equals(orig, nameUk, StringComparison.OrdinalIgnoreCase)) return null; // українська назва й так оригінал
        return orig;
    }

    private static string? TitleFromTag(string titleTag)
    {
        // "Ти — космос (2025) дивитися онлайн українською | SWEET.TV" → "Ти — космос"
        var cut = titleTag.IndexOf(" (", StringComparison.Ordinal);
        if (cut <= 0) return null;
        return titleTag[..cut].Trim();
    }

    private static string? Canonical(string html)
    {
        var m = Regex.Match(html, "<link[^>]*rel=\"canonical\"[^>]*href=\"(?<h>[^\"]+)\"", RegexOptions.Singleline);
        return m.Success ? m.Groups["h"].Value : null;
    }

    private static string? MetaDescription(string html)
    {
        var m = Regex.Match(html, "<meta[^>]*name=\"description\"[^>]*content=\"(?<c>[^\"]+)\"", RegexOptions.Singleline);
        if (!m.Success)
            m = Regex.Match(html, "<meta[^>]*content=\"(?<c>[^\"]+)\"[^>]*name=\"description\"", RegexOptions.Singleline);
        return m.Success ? m.Groups["c"].Value : null;
    }

    private static IEnumerable<string> LdJsonBlobs(string html)
    {
        foreach (Match m in LdJson.Matches(html))
            yield return m.Groups["json"].Value;
    }

    private static JsonDocument? TryParse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static bool IsType(JsonElement el, string type)
    {
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("@type", out var t)) return false;
        return t.ValueKind == JsonValueKind.String
            ? string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase)
            : t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && string.Equals(x.GetString(), type, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[] StrArray(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray(),
            JsonValueKind.String when v.GetString() is { Length: > 0 } s => [s],
            _ => [],
        };
    }
}
