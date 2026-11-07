using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Нода офіційного MAL API v2 → Title-патч. title_uk НІКОЛИ не пишемо (uk-назв у MAL нема —
/// не затираємо null'ом); media_type визначає type (anime|manga) і format; rating_tmdb = mean
/// (шкала 0–10 сумірна з TMDB); popularity = num_list_users/10000 (як members у Jikan).
/// </summary>
public static class MalOfficialMapper
{
    private static readonly HashSet<string> AnimeMediaTypes =
        new(["tv", "movie", "ova", "ona", "special"], StringComparer.Ordinal);

    private static readonly HashSet<string> MangaMediaTypes =
        new(["manga", "light_novel", "one_shot", "manhwa", "manhua"], StringComparer.Ordinal);

    /// <summary>fallbackType — тип ендпоінта (top/anime, anime/season → anime; top/manga → manga) для невідомих media_type.</summary>
    public static Title Build(MalNode n, string fallbackType = "anime")
    {
        var t = new Title { Type = TypeOf(n.MediaType, fallbackType) };
        Apply(t, n, fallbackType);
        return t;
    }

    public static void Apply(Title t, MalNode n, string fallbackType = "anime")
    {
        t.Type = TypeOf(n.MediaType, fallbackType);
        var isManga = string.Equals(t.Type, "manga", StringComparison.Ordinal);

        t.MalId = n.Id > 0 ? n.Id : t.MalId;
        t.TitleEn = JikanMediaMapper.FirstNonEmpty(n.Title, n.AlternativeTitles?.En) ?? t.TitleEn ?? "";
        t.OverviewEn = JikanMediaMapper.FirstNonEmpty(n.Synopsis) ?? t.OverviewEn;
        t.Year = YearOf(n.StartDate) ?? t.Year;
        t.Genres = (n.Genres ?? [])
            .Select(g => AnimeGenres.Canonical(g.Name))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        t.Format = AnimeFormats.Normalize(n.MediaType);
        t.Episodes = !isManga && n.NumEpisodes is > 0 ? n.NumEpisodes : t.Episodes;

        // runtime: average_episode_duration — секунди → хвилини; фільм → RuntimeMin, серіал → EpisodeRuntimeMin
        if (n.AverageEpisodeDuration is > 0 && n.AverageEpisodeDuration.Value / 60 > 0)
        {
            var minutes = n.AverageEpisodeDuration.Value / 60;
            if (string.Equals(n.MediaType, "movie", StringComparison.Ordinal))
                t.RuntimeMin = minutes;
            else if (!isManga)
                t.EpisodeRuntimeMin = minutes;
        }

        t.Studios = !isManga ? Names(n.Studios) : t.Studios;
        t.ScoreMal = n.Mean is > 0 ? n.Mean : t.ScoreMal; // mean у MAL — це і є MAL-оцінка
        t.RatingTmdb = n.Mean is > 0 ? n.Mean : t.RatingTmdb; // 0–10, сумірно з TMDB → пошукові фільтри
        t.Popularity = n.NumListUsers is > 0 ? n.NumListUsers.Value / 10000d : t.Popularity;
        t.PosterPath = JikanMediaMapper.FirstNonEmpty(n.MainPicture?.Large, n.MainPicture?.Medium) ?? t.PosterPath;
        t.Status = StatusOf(n.Status) ?? t.Status;
    }

    private static string TypeOf(string? mediaType, string fallbackType) =>
        AnimeMediaTypes.Contains(mediaType ?? "") ? "anime"
        : MangaMediaTypes.Contains(mediaType ?? "") ? "manga"
        : fallbackType;

    private static int? YearOf(string? startDate)
    {
        if (string.IsNullOrWhiteSpace(startDate)) return null;
        var head = startDate.Split('-')[0].Trim();
        return int.TryParse(head, out var year) && year is > 1900 and < 3000 ? year : null;
    }

    private static string? StatusOf(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "currently_airing" or "currently_publishing" => "airing",
        "finished_airing" or "finished_publishing" => "finished",
        "not_yet_aired" or "not_yet_published" => "upcoming",
        "on_hiatus" or "discontinued" => null,
        _ => null,
    };

    private static string[]? Names(List<MalNamed>? list) =>
        list is not { Count: > 0 }
            ? null
            : list.Select(x => x.Name?.Trim()).OfType<string>().Where(x => x.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray();
}
