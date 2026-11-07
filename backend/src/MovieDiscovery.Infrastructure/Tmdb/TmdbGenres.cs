namespace MovieDiscovery.Infrastructure.Tmdb;

/// <summary>
/// Локалізація жанрів TMDB: імпорт зберігає канонічні англійські назви, uk-назви — мапа в коді.
/// Використовується і для відображення (en→uk), і для нормалізації фільтрів (uk→en).
/// </summary>
public static class TmdbGenres
{
    private static readonly Dictionary<int, (string Uk, string En)> byId = new()
    {
        [28] = ("Бойовик", "Action"),
        [12] = ("Пригоди", "Adventure"),
        [16] = ("Мультфільм", "Animation"),
        [35] = ("Комедія", "Comedy"),
        [80] = ("Кримінал", "Crime"),
        [99] = ("Документальний", "Documentary"),
        [18] = ("Драма", "Drama"),
        [10751] = ("Сімейний", "Family"),
        [14] = ("Фентезі", "Fantasy"),
        [36] = ("Історія", "History"),
        [27] = ("Жахи", "Horror"),
        [10402] = ("Музика", "Music"),
        [9648] = ("Детектив", "Mystery"),
        [10749] = ("Мелодрама", "Romance"),
        [878] = ("Фантастика", "Science Fiction"),
        [10770] = ("Телефільм", "TV Movie"),
        [53] = ("Триллер", "Thriller"),
        [10752] = ("Воєнний", "War"),
        [37] = ("Вестерн", "Western"),
        // TV-жанри (співпадіння id з movie навмисно різні у TMDB)
        [10759] = ("Екшн та Пригоди", "Action & Adventure"),
        [10762] = ("Дитячий", "Kids"),
        [10763] = ("Новини", "News"),
        [10764] = ("Реаліті", "Reality"),
        [10765] = ("Наукова фантастика та Фентезі", "Sci-Fi & Fantasy"),
        [10766] = ("Мильна опера", "Soap"),
        [10767] = ("Ток-шоу", "Talk"),
        [10768] = ("Війна та Політика", "War & Politics"),
    };

    /// <summary>Англійська канонічна назва жанру за id (null — невідомий id). Зберігаємо в БД у lowercase.</summary>
    public static string? EnName(int id) => byId.TryGetValue(id, out var v) ? v.En.ToLowerInvariant() : null;

    /// <summary>Uk-назва за id.</summary>
    public static string? UkName(int id) => byId.TryGetValue(id, out var v) ? v.Uk : null;

    /// <summary>en → uk для відображення (невідоме значення повертається як є).</summary>
    public static string ToUk(string en)
    {
        foreach (var (_, (uk, enName)) in byId)
            if (string.Equals(enName, en, StringComparison.OrdinalIgnoreCase)) return uk;
        return en;
    }

    // Побутові написання → канонічне en (lowercase). Регістр не важливий.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sci-fi"] = "science fiction",
        ["sci_fi"] = "science fiction",
        ["scifi"] = "science fiction",
        ["science-fiction"] = "science fiction",
        ["tv-movie"] = "tv movie",
        ["тв-фільм"] = "tv movie",
        ["романтика"] = "romance",
    };

    /// <summary>Нормалізація фільтра: uk/en назва або аліас → канонічне en у lowercase (null — невідомо).</summary>
    public static string? NormalizeToEn(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        if (Aliases.TryGetValue(trimmed, out var alias)) return alias;
        foreach (var (_, (uk, en)) in byId)
        {
            if (string.Equals(en, trimmed, StringComparison.OrdinalIgnoreCase)) return en.ToLowerInvariant();
            if (string.Equals(uk, trimmed, StringComparison.OrdinalIgnoreCase)) return en.ToLowerInvariant();
        }
        return Aliases.TryGetValue(trimmed.ToLowerInvariant().Replace("ё", "е"), out var a2) ? a2 : null;
    }
}
