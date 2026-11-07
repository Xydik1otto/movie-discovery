using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// AniList MediaListCollection entry → Title-патч. title_uk пишемо ЛИШЕ зі справжньої
/// uk-назви (media.title.ukrainian); score_anilist — meanScore (в імпорті averageScore) (0–100) / 10.
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

    private static string? FirstNonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
