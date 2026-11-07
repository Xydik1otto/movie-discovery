using System.Text.RegularExpressions;

namespace MovieDiscovery.Core.Text;

public sealed record ExtractedSignals(IReadOnlyList<string> Genres, IReadOnlyList<string> Keywords)
{
    public bool IsEmpty => Genres.Count == 0 && Keywords.Count == 0;
}

/// <summary>
/// Легкий keyword-extraction з вільного тексту (fallback Е0/Е1 для /api/similar/by-text):
/// розпізнає жанри (uk/en) та настрій-ключові слова. Чистий C#, без I/O.
/// </summary>
public static partial class KeywordExtractor
{
    // (regex, жанр TMDB en, keyword-токени)
    private static readonly (Regex Pattern, string Genre, string[] ExtraKeywords)[] Rules =
    [
        (Genre(@"ситком|sitcom"), "Comedy", ["sitcom"]),
        (Genre(@"комед|comed"), "Comedy", []),
        (Genre(@"драм"), "Drama", []),
        (Genre(@"триллер|thriller"), "Thriller", []),
        (Genre(@"детектив|mystery|detective"), "Mystery", []),
        (Genre(@"кримінал|кримінальн|злочин|crime|gangster|мафія|mafia"), "Crime", []),
        (Genre(@"бойовик|екшн|action"), "Action", []),
        (Genre(@"жах|horror|скример|slasher"), "Horror", []),
        (Genre(@"фантастика|sci-?fi|science[ -]fiction|космос|space"), "Science Fiction", []),
        (Genre(@"фентезі|fantasy|магі|magic|чарівн"), "Fantasy", []),
        (Genre(@"романти|мелодрам|romance|romantic|love story"), "Romance", []),
        (Genre(@"анімац|мультфільм|мультсеріал|animation|animated|cartoon|anime"), "Animation", []),
        (Genre(@"документальн|documentary|docuseries"), "Documentary", []),
        (Genre(@"історичн|histor"), "History", []),
        (Genre(@"сімейн|family"), "Family", []),
        (Genre(@"вестерн|western"), "Western", []),
        (Genre(@"пригод|adventure"), "Adventure", []),
        (Genre(@"музик|musical"), "Music", []),
        (Genre(@"військов|воєнн|war"), "War", []),
    ];

    // mood/стиль → keyword-токени (англійські фрази-стилі як у TMDB keywords)
    private static readonly (Regex Pattern, string[] Keywords)[] MoodRules =
    [
        (Mood(@"легк|light|easy[ -]?watch|cozy|затишн|feel[ -]?good"), ["feel-good"]),
        (Mood(@"похмурн|темн|dark|gritty|мрачн"), ["dark"]),
        (Mood(@"розумн|інтелектуальн|clever|smart|mind-?bending|twist|поворот"), ["mind-bending", "twist"]),
        (Mood(@"слоу|slow-?burn|повільн"), ["slow-burn"]),
        (Mood(@"напружен|suspense|tense|нервов"), ["suspense"]),
        (Mood(@"серіал|series|tv show|телешоу"), []),
        (Mood(@"детективн|detective|розслідуван|investigation|whodunit"), ["detective", "investigation"]),
        (Mood(@"спорт|sport"), ["sports"]),
        (Mood(@"містичн|mystic|supernatural|надприродн"), ["supernatural"]),
        (Mood(@"реальн.*поді|based on a true story|true story|справжн"), ["based on a true story"]),
    ];

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "with", "that", "this", "have", "from", "want", "like", "something", "about",
        "would", "could", "should", "there", "their", "what", "when", "where", "which", "will",
        "your", "into", "more", "very", "just", "also", "some", "than", "then", "them", "over",
        "фільм", "фільми", "серіал", "серіали", "фільма", "тому", "який", "яка", "яке", "які",
        "щось", "щоб", "коли", "тільки", "дуже", "трохи", "можна", "хочу", "хотів", "хотіла",
        "буде", "бути", "мати", "після", "перед", "поки", "тому", "такий", "така", "таке",
        "about", "after", "before", "under", "над", "під", "як", "але", "або", "та", "і", "й", "із", "з",
    };

    public static ExtractedSignals Extract(string text)
    {
        var genres = new List<string>();
        var keywords = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new ExtractedSignals(genres, keywords);

        foreach (var (pattern, genre, extra) in Rules)
        {
            if (!pattern.IsMatch(text)) continue;
            if (!genres.Contains(genre, StringComparer.OrdinalIgnoreCase)) genres.Add(genre);
            foreach (var k in extra)
                if (!keywords.Contains(k, StringComparer.OrdinalIgnoreCase)) keywords.Add(k);
        }

        foreach (var (pattern, kws) in MoodRules)
        {
            if (!pattern.IsMatch(text)) continue;
            foreach (var k in kws)
                if (!keywords.Contains(k, StringComparer.OrdinalIgnoreCase)) keywords.Add(k);
        }

        // Салient-слова: довгі токени, що не є стоп-словами — для ILIKE-пошуку по keywords/назвах у БД
        var salient = Regex.Split(text, @"[^\p{L}\p{N}\-]+")
            .Where(w => w.Length >= 4)
            .Select(w => w.ToLowerInvariant())
            .Where(w => !StopWords.Contains(w))
            .Distinct()
            .Take(8)
            .ToList();

        foreach (var w in salient)
            if (!keywords.Contains(w, StringComparer.OrdinalIgnoreCase)) keywords.Add(w);

        return new ExtractedSignals(genres, keywords);
    }

    private static Regex Genre(string alternatives) =>
        new(@"\b(" + alternatives + @")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Regex Mood(string alternatives) =>
        new(@"\b(" + alternatives + @")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
