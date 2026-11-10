namespace MovieDiscovery.Core.Discover;

/// <summary>Опції discover (FR-18): скоринг, diversity, мікс exploit/safe/explore, anti-fatigue.</summary>
public sealed class DiscoverOptions
{
    public double GenreWeight { get; set; } = 1.0;
    public double EraWeight { get; set; } = 1.0;
    public double ActorWeight { get; set; } = 1.0;
    public double RatingNormWeight { get; set; } = 1.0;
    public double PopularityNormWeight { get; set; } = 0.3;
    public double NegativePenaltyWeight { get; set; } = 1.0;
    /// <summary>Максимум тайтлів одного жанру в одній видачі (diversity).</summary>
    public int MaxPerGenre { get; set; } = 2;
    public double ExploitShare { get; set; } = 0.7;
    public double SafeShare { get; set; } = 0.2;
    /// <summary>Поріг IMDb-рейтингу для safe-пулу (АБО top-квартиль популярності).</summary>
    public double SafeImdbThreshold { get; set; } = 7.0;
}
