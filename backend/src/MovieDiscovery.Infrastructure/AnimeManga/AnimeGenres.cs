namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Мапа жанрів аніме/манги: назви джерел (Jikan/MAL, AniList, Shikimori) → канонічні en у lowercase.
/// Імпорт: невпізнане зберігаємо lowercase як є; hentai/erotica відкидаємо.
/// Фільтри пошуку: лише впізнані значення (NormalizeToEn → null для невідомих).
/// </summary>
public static class AnimeGenres
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["action"] = "action",
        ["adventure"] = "adventure",
        ["avant garde"] = "avant garde",
        ["award winning"] = "award winning",
        ["award-winning"] = "award winning",
        ["boys love"] = "boys love",
        ["comedy"] = "comedy",
        ["drama"] = "drama",
        ["ecchi"] = "ecchi",
        ["fantasy"] = "fantasy",
        ["gourmet"] = "gourmet",
        ["horror"] = "horror",
        ["mecha"] = "mecha",
        ["music"] = "music",
        ["mystery"] = "mystery",
        ["psychological"] = "psychological",
        ["romance"] = "romance",
        ["sci-fi"] = "science fiction",
        ["sci_fi"] = "science fiction",
        ["science fiction"] = "science fiction",
        ["school"] = "school",
        ["slice of life"] = "slice of life",
        ["sports"] = "sports",
        ["supernatural"] = "supernatural",
        ["suspense"] = "thriller",
        ["thriller"] = "thriller",
    };

    /// <summary>Для імпорту: впізнане → канонічне en lowercase; невпізнане → lowercase як є; hentai/erotica → null.</summary>
    public static string? Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var lower = name.Trim().ToLowerInvariant();
        if (lower is "hentai" or "erotica") return null;
        return Map.TryGetValue(lower, out var canonical) ? canonical : lower;
    }

    /// <summary>Для фільтрів: лише впізнані жанри (null — невідоме значення → 400 у контролері).</summary>
    public static string? NormalizeToEn(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var lower = name.Trim().ToLowerInvariant().Replace("ё", "е");
        return Map.TryGetValue(lower, out var canonical) ? canonical : null;
    }
}

/// <summary>Канонічні формати аніме/манги (PLAN: tv|movie|ova|ona|special|manga|manhwa|one_shot|novel).</summary>
public static class AnimeFormats
{
    public static readonly string[] Valid =
        ["tv", "movie", "ova", "ona", "special", "manga", "manhwa", "one_shot", "novel"];

    /// <summary>Назва формату джерела (Jikan/AniList/Shikimori) → канонічна lowercase; невідома → null.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = raw.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return key switch
        {
            "tv" or "tv_short" => "tv",
            "movie" => "movie",
            "ova" => "ova",
            "ona" => "ona",
            "special" or "tv_special" => "special",
            "manga" => "manga",
            "manhwa" => "manhwa",
            "one_shot" => "one_shot",
            "novel" or "light_novel" => "novel",
            _ => null, // music, doujin, manhua тощо — поза канонічним набором
        };
    }
}
