using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>
/// Онбординг (FR-18): статичні 10 питань — 3 постери-мультивибір жанрів, 2 настрій, 2 ера,
/// 3 «обери тайтл» (підказки; вибір через пошук, без карток). Варіанти — жанрові ключі uk/en назви.
/// </summary>
public static class OnboardingQuestions
{
    public static object[] Build() =>
    [
        Poster("posters-1",
            "Які постери відгукуються? (обери скільки завгодно)",
            "Which posters catch your eye? (pick any)",
            "action", "comedy", "drama", "horror", "science fiction", "animation"),
        Poster("posters-2",
            "Що з цього тобі ближче?",
            "Which of these is closer to you?",
            "thriller", "romance", "fantasy", "documentary", "family", "crime"),
        Poster("posters-3",
            "Які жанри хочеш бачити частіше?",
            "Which genres do you want to see more often?",
            "adventure", "mystery", "history", "war", "music", "western"),
        Mood("mood-1",
            "Який настрій тобі зараз більше до душі?",
            "What kind of mood are you after?",
            "comedy", "family", "romance", "animation"),
        Mood("mood-2",
            "А якщо щось гостріше?",
            "Or something more intense?",
            "thriller", "mystery", "horror", "drama"),
        Era("era-1",
            "Які десятиліття тобі цікаві?",
            "Which decades interest you?",
            "1970s", "1980s", "1990s", "2000s"),
        Era("era-2",
            "І сучасніші?",
            "And more recent ones?",
            "2010s", "2020s"),
        Titles("titles-1",
            "Назви фільм, який ти любиш", "Name a movie you love"),
        Titles("titles-2",
            "Назви серіал, який ти любиш", "Name a series you love"),
        Titles("titles-3",
            "Назви аніме/мангу, яку ти любиш (якщо є)", "Name an anime/manga you love (if any)"),
    ];

    private static object Poster(string id, string promptUk, string promptEn, params string[] genreKeys) => new
    {
        id,
        type = "posters",
        promptUk,
        promptEn,
        options = genreKeys.Select(key => new
        {
            key,
            nameUk = TmdbGenres.ToUk(key),
            nameEn = key,
        }).ToArray(),
    };

    private static object Mood(string id, string promptUk, string promptEn, params string[] genreKeys) => new
    {
        id,
        type = "mood",
        promptUk,
        promptEn,
        options = genreKeys.Select(key => new
        {
            key,
            nameUk = TmdbGenres.ToUk(key),
            nameEn = key,
        }).ToArray(),
    };

    private static object Era(string id, string promptUk, string promptEn, params string[] decades) => new
    {
        id,
        type = "era",
        promptUk,
        promptEn,
        options = decades.Select(d => new { key = d, nameUk = EraUk(d), nameEn = d }).ToArray(),
    };

    private static object Titles(string id, string promptUk, string promptEn) => new
    {
        id,
        type = "titles",
        promptUk,
        promptEn,
        options = Array.Empty<object>(), // вибір тайтла — через пошук-автокомпліт
    };

    private static string EraUk(string decade) => decade switch
    {
        "1970s" => "1970-ті",
        "1980s" => "1980-ті",
        "1990s" => "1990-ті",
        "2000s" => "2000-ні",
        "2010s" => "2010-ті",
        "2020s" => "2020-ті",
        _ => decade,
    };
}
