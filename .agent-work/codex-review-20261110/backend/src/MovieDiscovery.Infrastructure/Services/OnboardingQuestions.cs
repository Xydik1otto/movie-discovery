using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>Варіант онбординг-питання (onboarding FR-18). PosterUrl заповнює OnboardingPostersService.</summary>
public sealed class OnboardingOptionDto
{
    public string Key { get; set; } = "";
    public string NameUk { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string? PosterUrl { get; set; }
}

/// <summary>Питання онбордингу (FR-18): posters/mood/era — мультивибір, titles — пошук-автокомпліт.</summary>
public sealed class OnboardingQuestionDto
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = ""; // posters | mood | era | titles
    public string PromptUk { get; set; } = "";
    public string PromptEn { get; set; } = "";
    public List<OnboardingOptionDto> Options { get; set; } = [];
}

/// <summary>
/// Онбординг (FR-18): статичні 10 питань — 3 постери-мультивибір жанрів, 2 настрій (жанри),
/// 2 ера (десятиліття), 3 «назви тайтл» (type=titles, вибір через пошук-автокомпліт).
/// Постери варіантів резолвить OnboardingPostersService (топ-популярний тайтл жанру/десятиліття).
/// </summary>
public static class OnboardingQuestions
{
    public static List<OnboardingQuestionDto> Build() =>
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

    private static OnboardingQuestionDto Poster(string id, string promptUk, string promptEn, params string[] genreKeys) =>
        new()
        {
            Id = id,
            Type = "posters",
            PromptUk = promptUk,
            PromptEn = promptEn,
            Options = genreKeys.Select(key => new OnboardingOptionDto
            {
                Key = key,
                NameUk = TmdbGenres.ToUk(key),
                NameEn = key,
            }).ToList(),
        };

    private static OnboardingQuestionDto Mood(string id, string promptUk, string promptEn, params string[] genreKeys) =>
        new()
        {
            Id = id,
            Type = "mood",
            PromptUk = promptUk,
            PromptEn = promptEn,
            Options = genreKeys.Select(key => new OnboardingOptionDto
            {
                Key = key,
                NameUk = TmdbGenres.ToUk(key),
                NameEn = key,
            }).ToList(),
        };

    private static OnboardingQuestionDto Era(string id, string promptUk, string promptEn, params string[] decades) =>
        new()
        {
            Id = id,
            Type = "era",
            PromptUk = promptUk,
            PromptEn = promptEn,
            Options = decades.Select(d => new OnboardingOptionDto
            {
                Key = d,
                NameUk = EraUk(d),
                NameEn = d,
            }).ToList(),
        };

    private static OnboardingQuestionDto Titles(string id, string promptUk, string promptEn) => new()
    {
        Id = id,
        Type = "titles",
        PromptUk = promptUk,
        PromptEn = promptEn,
        Options = [], // вибір тайтла — через пошук-автокомпліт
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
