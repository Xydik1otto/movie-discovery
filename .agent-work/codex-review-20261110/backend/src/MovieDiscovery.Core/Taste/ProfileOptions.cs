namespace MovieDiscovery.Core.Taste;

/// <summary>
/// Ваги сигналів профілю смаку (FR-17 / PLAN §FR-16/17/18): like +1.0;
/// оцінка (r−5.5)/4.5×1.2; watchlist +0.4; dislike −0.8; часовий спад — напівжиття 60 днів.
/// Внесок кожної дії: вага × спад за created_at.
/// </summary>
public sealed class ProfileOptions
{
    public double LikeWeight { get; set; } = 1.0;
    public double RatingWeight { get; set; } = 1.2;
    public double WatchlistWeight { get; set; } = 0.4;
    public double DislikeWeight { get; set; } = -0.8;
    /// <summary>Напівжиття внесків, днів.</summary>
    public double HalfLifeDays { get; set; } = 60.0;
    /// <summary>Холодний старт онбордингу: вага одного вибору (жанр/ера) — picks×0.6.</summary>
    public double QuizPickWeight { get; set; } = 0.6;
    /// <summary>Cooldown-вага негативного жанру за дизлайк (нагромаджується в negative_genres).</summary>
    public double NegativeCooldownWeight { get; set; } = 0.8;

    /// <summary>Експоненційний спад з напівжиттям: 0.5^(age/halfLife).</summary>
    public static double Decay(double ageDays, double halfLifeDays) =>
        Math.Pow(0.5, ageDays / halfLifeDays);
}
