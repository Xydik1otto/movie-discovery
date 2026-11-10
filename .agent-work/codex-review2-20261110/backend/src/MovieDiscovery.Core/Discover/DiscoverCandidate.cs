namespace MovieDiscovery.Core.Discover;

/// <summary>
/// Кандидат видачі discover — зріз тайтла для скорингу й картки (без I/O; проекцію з БД робить сервіс).
/// </summary>
public sealed record DiscoverCandidate(
    int Id,
    int? TmdbId,
    string Type,
    string? TitleUk,
    string TitleEn,
    int? Year,
    string[] Genres,
    string[] Actors,
    double? RatingTmdb,
    double? RatingImdb,
    int? VotesImdb,
    double Popularity,
    string? PosterPath,
    string? OverviewUk,
    string? OverviewEn);
