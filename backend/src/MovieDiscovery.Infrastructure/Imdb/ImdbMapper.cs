using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Imdb;

/// <summary>Патч тайтла з IMDb datasets — те, що зливається в titles за imdb_id.</summary>
public sealed record ImdbTitlePatch(
    string ImdbId,
    string Type, // movie | series
    string TitleEn,
    int? Year,
    int? RuntimeMin,
    string[] Genres,
    double? RatingImdb,
    int? VotesImdb,
    double Popularity,
    bool IsAdult);

/// <summary>
/// Мапінг IMDb titleType/жанрів у модель каталогу (FR-15). Чисті функції — покриті тестами.
/// titleType: movie|tvMovie|video|tvSpecial|short → movie; tvSeries|tvMiniSeries → series;
/// tvEpisode|videoGame (та інше невідоме) → SKIP.
/// popularity = log10(numVotes+1)*10 — лог-нормалізація, консистентна з іншими джерелами
/// (TMDB popular ≈ 10–100; 1 млн голосів IMDb → 60).
/// </summary>
public static class ImdbMapper
{
    public const double PopularityScale = 10.0;

    /// <summary>titleType → type каталогу; null — рядок пропускаємо.</summary>
    public static string? MapType(string titleType) => titleType switch
    {
        "movie" or "tvMovie" or "video" or "tvSpecial" or "short" => "movie",
        "tvSeries" or "tvMiniSeries" => "series",
        _ => null, // tvEpisode, videoGame, невідоме
    };

    /// <summary>Жанри IMDb → канонічні lowercase (відомі — за мапою, невідомі — як є).</summary>
    public static string[] NormalizeGenres(IEnumerable<string> raw)
    {
        var result = new List<string>();
        foreach (var g in raw)
        {
            var genre = g.Trim().ToLowerInvariant();
            if (genre.Length == 0) continue;
            genre = Tmdb.TmdbGenres.NormalizeToEn(genre) ?? Alias(genre) ?? genre;
            if (!result.Contains(genre, StringComparer.OrdinalIgnoreCase)) result.Add(genre);
        }
        return [.. result];
    }

    /// <summary>Побутові назви IMDb, яких нема в TMDB-мапі.</summary>
    private static string? Alias(string genre) => genre switch
    {
        "reality-tv" => "reality",
        "talk-show" => "talk",
        "musical" => "music",
        "film-noir" => "film noir",
        "game-show" => "game show",
        "biography" => "biography", // особливої канонічної форми нема — лишається як є
        _ => null,
    };

    public static double PopularityFromVotes(int votes) =>
        votes <= 0 ? 0 : Math.Log10(votes + 1) * PopularityScale;

    /// <summary>
    /// Рядок basics (+ опційно rating) → патч; null — SKIP (тип не мапиться або вимкнений фільтром).
    /// </summary>
    public static ImdbTitlePatch? ToPatch(
        ImdbBasicsRow row,
        ImdbRatingRow? rating,
        IReadOnlySet<string> mediaTypes)
    {
        var type = MapType(row.TitleType);
        if (type is null || !mediaTypes.Contains(type)) return null;

        var titleEn = NonEmpty(row.PrimaryTitle) ?? NonEmpty(row.OriginalTitle);
        if (titleEn is null) return null; // без назви рядок марний

        return new ImdbTitlePatch(
            row.Tconst,
            type,
            titleEn,
            row.StartYear,
            row.RuntimeMinutes,
            NormalizeGenres(row.Genres),
            rating?.AverageRating,
            rating?.NumVotes,
            PopularityFromVotes(rating?.NumVotes ?? 0),
            row.IsAdult);
    }

    private static string? NonEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim() == ImdbTsv.NullField ? null : s.Trim();
}
