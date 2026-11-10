using System.Net;
using System.Text.RegularExpressions;
using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// AniList MediaListCollection entry → Title-патч. title_uk пишемо ЛИШЕ зі справжньої
/// uk-назви (media.title.ukrainian); synonyms розподіляємо за мовами (fill-only):
/// кирилиця з ї/і/є/ґ → title_uk, кирилиця без них → title_ru, латиниця → title_en.
/// score_anilist — meanScore (в імпорті averageScore) (0–100) / 10.
/// Опис (description) — лише en: HTML санітизуємо в OverviewEn (uk-описів в AniList немає).
/// </summary>
public static class AniListMapper
{
    public static Title BuildAnime(AniListEntry e) => Build("anime", e.Media, isManga: false);

    public static Title BuildManga(AniListEntry e) => Build("manga", e.Media, isManga: true);

    /// <summary>Каталог-sync (Page/POPULARITY_DESC): медіа без обгортки entry.</summary>
    public static Title BuildCatalogAnime(AniListMedia m) => Build("anime", m, isManga: false);

    public static Title BuildCatalogManga(AniListMedia m) => Build("manga", m, isManga: true);

    public static void ApplyAnime(Title t, AniListEntry e) => Apply(t, e.Media, isManga: false);

    public static void ApplyManga(Title t, AniListEntry e) => Apply(t, e.Media, isManga: true);

    private static Title Build(string type, AniListMedia? m, bool isManga)
    {
        var t = new Title { Type = type };
        Apply(t, m, isManga);
        return t;
    }

    private static void Apply(Title t, AniListMedia? m, bool isManga)
    {
        if (m is null) return;
        t.AnilistId = m.Id > 0 ? m.Id : null;
        t.TitleEn = JikanMediaMapper.FirstNonEmpty(m.Title?.Romaji, m.Title?.English) ?? t.TitleEn ?? "";
        t.TitleUk = JikanMediaMapper.FirstNonEmpty(m.Title?.Ukrainian) ?? t.TitleUk; // справжній uk, інакше null
        // synonyms → назви за мовами (fill-only): справжні назви (romaji/ukrainian) мають пріоритет,
        // synonyms лише дозаповнюють порожні (політика власника 10.11 — російські ТА українські назви)
        ApplySynonyms(t, m.Synonyms);
        // Опис — тільки en (uk-описів в AniList не буває); порожній/відсутній не затирає наявний
        t.OverviewEn = CleanDescription(m.Description) ?? t.OverviewEn;
        t.Year = m.StartDate?.Year is > 1900 and < 3000 ? m.StartDate!.Year : null;
        t.Genres = (m.Genres ?? [])
            .Select(AnimeGenres.Canonical)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        t.Format = AnimeFormats.Normalize(m.Format);
        t.Episodes = !isManga && m.Episodes is > 0 ? m.Episodes : null;
        t.Chapters = isManga && m.Chapters is > 0 ? m.Chapters : null;
        t.Volumes = isManga && m.Volumes is > 0 ? m.Volumes : null;
        t.Studios = !isManga
            ? (m.Studios is { Nodes.Count: > 0 }
                ? m.Studios.Nodes.Select(n => n.Name?.Trim()).OfType<string>().Where(n => n.Length > 0)
                    .Distinct(StringComparer.Ordinal).ToArray()
                : null)
            : t.Studios;
        // score_anilist: meanScore (середня оцінка користувачів, сумірна з MAL mean) має пріоритет;
        // в імпорті meanScore не запитується → працює averageScore (0–100 → шкала 0–10)
        var scoreRaw = m.MeanScore ?? m.AverageScore;
        t.ScoreAnilist = scoreRaw is > 0 ? scoreRaw.Value / 10d : null;
        // популярність: popularity (скільки юзерів має в списках) або favourites (каталог-запит) → /10000
        var popRaw = m.Popularity is > 0 ? m.Popularity : m.Favourites;
        t.Popularity = popRaw is > 0 ? popRaw.Value / 10000d : t.Popularity;
        t.PosterPath = FirstNonEmpty(m.CoverImage?.Large) ?? t.PosterPath;
    }

    private static readonly Regex BrTagRegex =
        new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex HtmlTagRegex =
        new(@"<[^>]*>", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly Regex BlankLinesRegex =
        new(@"\n{3,}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Опис AniList приходить з HTML: «br» → перенос рядка, решту тегів (i, b, a href)
    /// прибираємо, HTML-сутності декодуємо (апостроф, амперсанд, тире), 3+ порожні рядки
    /// стискаємо до одного абзацного відступу. Порожнє → null (Merge не затирає наявний опис).
    /// </summary>
    public static string? CleanDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Replace("\r\n", "\n");
        s = BrTagRegex.Replace(s, "\n");
        s = HtmlTagRegex.Replace(s, string.Empty);
        s = WebUtility.HtmlDecode(s); // сутності декодуємо ПІСЛЯ зняття тегів — декодований текст уже не стає тегом
        s = BlankLinesRegex.Replace(s, "\n\n");
        s = string.Join("\n", s.Split('\n').Select(l => l.Trim())).Trim();
        return s.Length == 0 ? null : s;
    }

    private static string? FirstNonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static readonly char[] UkrainianOnlyLetters = ['\u0457', '\u0456', '\u0454', '\u0491']; // ї і є ґ

    /// <summary>
    /// Розподіл synonyms за мовами (fill-only, політика власника 10.11): кирилиця з
    /// українськими літерами (ї/і/є/ґ) → title_uk, кирилиця без них → title_ru, латиниця →
    /// title_en лише якщо той ще порожній. Перший відповідний синонім кожної мови виграє;
    /// не-латиниця/не-кирилиця (кана, ієрогліфи) — ігноруємо.
    /// </summary>
    private static void ApplySynonyms(Title t, List<string>? synonyms)
    {
        if (synonyms is not { Count: > 0 }) return;
        foreach (var raw in synonyms)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            if (HasCyrillic(name))
            {
                if (HasUkrainianMarks(name))
                {
                    if (string.IsNullOrWhiteSpace(t.TitleUk)) t.TitleUk = name;
                }
                else if (string.IsNullOrWhiteSpace(t.TitleRu))
                {
                    t.TitleRu = name;
                }
            }
            else if (HasLatin(name) && string.IsNullOrWhiteSpace(t.TitleEn))
            {
                t.TitleEn = name;
            }
        }
    }

    private static bool HasCyrillic(string s) => s.Any(c => c is >= '\u0400' and <= '\u04FF');

    private static bool HasUkrainianMarks(string s) => s.ToLowerInvariant().IndexOfAny(UkrainianOnlyLetters) >= 0;

    private static bool HasLatin(string s) => s.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
}
