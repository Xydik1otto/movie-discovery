namespace MovieDiscovery.Core.Similarity;

/// <summary>
/// Опційні фільтри «студії підборів» для by-seeds (FR-21):
/// type/рік — hard-фільтри ПУЛУ КАНДИДАТІВ (застосовуються в SQL ДО сортування;
/// план за індексами: type → ix_titles_type_popularity, рік → ix_titles_year);
/// genres — soft-boost: тайтли з перетином жанрів отримують бонус до score, НЕ відсікаються.
/// </summary>
public sealed record SimilarFilters
{
    /// <summary>Бонус до score за кожен жанр-акцент, що перетинається з тайтлом.</summary>
    public const double GenreBoostPerGenre = 0.08;
    /// <summary>Максимум жанрів, що беруть участь у бусті одного тайтла.</summary>
    public const int MaxBoostGenres = 3;

    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }
    /// <summary>Типи у канонічній lowercase-формі: movie | series | show | anime | manga.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];
    /// <summary>Жанри-акценти у канонічній en lowercase-формі (нормалізація — на контролері).</summary>
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Чи є hard-фільтри пулу (type/рік); жанри на пул не впливають.</summary>
    public bool HasPoolFilters => YearFrom is not null || YearTo is not null || Types.Count > 0;

    /// <summary>CSV (GET by-seeds): порожньо/whitespace → порожньо; токени trim-ляться, порожні відкидаються.</summary>
    public static List<string> ParseCsv(string? csv) => string.IsNullOrWhiteSpace(csv)
        ? []
        : csv.Split(',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>Нормалізація довільних рядків → lowercase/trim, порожні відкидаються, дублікати зливаються.</summary>
    public static List<string> NormalizeTokens(IEnumerable<string?> raw) => raw
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Select(t => t!.Trim().ToLowerInvariant())
        .Where(t => t.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Жанри-акценти: нормалізація uk/en/аліасів через делегат (Infrastructure-мапа), далі канонічні en lowercase.</summary>
    public static List<string> NormalizeGenres(IEnumerable<string?> raw, Func<string, string?>? genreNormalizer) =>
        NormalizeTokens(raw)
            .Select(g => genreNormalizer?.Invoke(g) ?? g)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
