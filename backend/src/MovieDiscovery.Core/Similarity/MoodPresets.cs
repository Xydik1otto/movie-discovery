namespace MovieDiscovery.Core.Similarity;

/// <summary>
/// Пресети настрою для переписаної системи еталонів («Схоже на...», 10.11):
/// настрій — чесний ПРЕСЕТ жанрів, а не вигадані поля: кожен ключ мапиться на
/// канонічні en-жанри каталогу (mutbl-колонка titles.genres зберігає саме їх).
/// Ключі локалізуються на фронті (uk/en i18n); бекенд оперує лише ключами.
/// У скорингу жанри настрою: (1) додаються у жанровий сигнал агрегата з вагою
/// MoodGenreWeight (м'якше за явні жанри користувача — ті мають вагу 1.0),
/// (2) беруть участь у soft-boost разом з явними жанрами.
/// Настрій працює БЕЗ еталонів: mood + genres — самодостатній запит.
/// </summary>
public static class MoodPresets
{
    /// <summary>Вага жанру настрою у жанровому сигналі агрегата (явні жанри користувача — 1.0).</summary>
    public const double MoodGenreWeight = 0.75;

    public static readonly IReadOnlyDictionary<string, string[]> Map =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["cozy"] = ["comedy", "family", "romance", "animation"],
            ["tense"] = ["thriller", "crime", "mystery"],
            ["funny"] = ["comedy", "romance"],
            ["sad"] = ["drama", "romance"],
            ["epic"] = ["action", "adventure", "fantasy", "science fiction"],
            ["romantic"] = ["romance", "drama", "comedy"],
            ["family"] = ["family", "animation", "adventure"],
            ["mindbender"] = ["mystery", "science fiction", "thriller"],
        };

    /// <summary>Ключі пресетів (для повідомлення про помилку і перевірок).</summary>
    public static IEnumerable<string> Keys => Map.Keys;

    public static bool IsValid(string? mood) =>
        !string.IsNullOrWhiteSpace(mood) && Map.ContainsKey(mood!.Trim());

    public static string Normalize(string mood) => mood.Trim().ToLowerInvariant();

    /// <summary>Жанри пресету (канонічні en lowercase); невідомий ключ → порожньо.</summary>
    public static IReadOnlyList<string> GenresOf(string mood) =>
        Map.TryGetValue(mood, out var genres) ? genres : [];
}
