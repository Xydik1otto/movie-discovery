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

    /// <summary>Англійська канонічна назва жанру за id (null — невідомий id).</summary>
    public static string? EnName(int id) => byId.TryGetValue(id, out var v) ? v.En : null;

    /// <summary>Uk-назва за id.</summary>
    public static string? UkName(int id) => byId.TryGetValue(id, out var v) ? v.Uk : null;

    /// <summary>en → uk для відображення (невідоме значення повертається як є).</summary>
    public static string ToUk(string en)
    {
        foreach (var (_, (uk, enName)) in byId)
            if (string.Equals(enName, en, StringComparison.OrdinalIgnoreCase)) return uk;
        return en;
    }

    /// <summary>Нормалізація фільтра: uk або en назва → канонічна en (null — невідома).</summary>
    public static string? NormalizeToEn(string name)
    {
        foreach (var (_, (uk, en)) in byId)
        {
            if (string.Equals(en, name, StringComparison.OrdinalIgnoreCase)) return en;
            if (string.Equals(uk, name, StringComparison.OrdinalIgnoreCase)) return en;
        }
        return null;
    }
}
