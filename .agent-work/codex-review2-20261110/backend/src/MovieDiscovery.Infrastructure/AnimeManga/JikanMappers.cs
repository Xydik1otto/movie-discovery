using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Jikan/MAL аніме → Title-патч (type=anime). title_en — ромадзі з fallback на title_english;
/// uk-назви в MAL нема → title_uk не чіпаємо (ніколи не затираємо null'ом).
/// </summary>
public static class JikanAnimeMapper
{
    public static Title Build(JikanMedia m) => JikanMediaMapper.BuildPatch("anime", m, isManga: false);

    public static void Apply(Title t, JikanMedia m) => JikanMediaMapper.Apply(t, m, isManga: false);
}

/// <summary>Jikan/MAL манга → Title-патч (type=manga): розділи, томи, автори.</summary>
public static class JikanMangaMapper
{
    public static Title Build(JikanMedia m) => JikanMediaMapper.BuildPatch("manga", m, isManga: true);

    public static void Apply(Title t, JikanMedia m) => JikanMediaMapper.Apply(t, m, isManga: true);
}

/// <summary>Спільна логіка мапінгу Jikan-медіа (аніме/манга).</summary>
internal static class JikanMediaMapper
{
    public static Title BuildPatch(string type, JikanMedia m, bool isManga)
    {
        var t = new Title { Type = type };
        Apply(t, m, isManga);
        return t;
    }

    public static void Apply(Title t, JikanMedia m, bool isManga)
    {
        t.MalId = m.MalId > 0 ? m.MalId : null;
        t.TitleEn = FirstNonEmpty(m.Title, m.TitleEnglish) ?? t.TitleEn ?? "";
        t.OverviewEn = FirstNonEmpty(m.Synopsis) ?? t.OverviewEn;
        t.Year = YearOf(m);
        t.Genres = (m.Genres ?? [])
            .Select(g => AnimeGenres.Canonical(g.Name))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        t.Format = AnimeFormats.Normalize(m.Type);
        t.Episodes = !isManga && m.Episodes is > 0 ? m.Episodes : null;
        t.Chapters = isManga && m.Chapters is > 0 ? m.Chapters : null;
        t.Volumes = isManga && m.Volumes is > 0 ? m.Volumes : null;
        t.Studios = !isManga ? Names(m.Studios) : t.Studios;
        t.Authors = isManga ? Names(m.Authors) : t.Authors;
        t.ScoreMal = m.Score is > 0 ? m.Score : null;
        // Нормування популярності: members (сотні тисяч–мільйони) → шкала, сумірна з TMDB (десятки–сотні).
        t.Popularity = m.Members is > 0 ? m.Members.Value / 10000d : t.Popularity;
        t.PosterPath = FirstNonEmpty(m.Images?.Jpg?.LargeImageUrl, m.Images?.Jpg?.ImageUrl) ?? t.PosterPath;
        t.Status = StatusOf(m.Status) ?? t.Status;
    }

    private static int? YearOf(JikanMedia m)
    {
        if (m.Year is > 0) return m.Year;
        var from = m.Aired?.From ?? m.Published?.From;
        return from is { Year: > 1900 and < 3000 } d ? d.Year : null;
    }

    private static string? StatusOf(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "finished airing" or "finished" or "discontinued" => "finished",
        "currently airing" or "publishing" => "airing",
        "not yet aired" or "not yet published" or "on hiatus" => null,
        "canceled" => "canceled",
        _ => null,
    };

    internal static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    internal static string[]? Names(List<JikanNamed>? list) =>
        list is not { Count: > 0 }
            ? null
            : list.Select(n => n.Name?.Trim()).OfType<string>().Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray();
}
