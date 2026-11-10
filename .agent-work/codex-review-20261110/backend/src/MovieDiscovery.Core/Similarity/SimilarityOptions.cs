namespace MovieDiscovery.Core.Similarity;

/// <summary>
/// Ваги сигналів схожості (за PLAN.md): semantic 0.35, keywords 0.20, genres 0.15,
/// credits 0.15, era 0.05, numeric 0.05, tmdbSimilarBoost 0.05; бонус same-type ×1.1.
/// Якщо семантики нема — ваги нормалізуються на решту доступних сигналів.
/// </summary>
public sealed class SimilarityOptions
{
    public double Semantic { get; set; } = 0.35;
    public double Keywords { get; set; } = 0.20;
    public double Genres { get; set; } = 0.15;
    public double Credits { get; set; } = 0.15;
    public double Era { get; set; } = 0.05;
    public double Numeric { get; set; } = 0.05;
    public double TmdbSimilarBoost { get; set; } = 0.05;
    public double SameTypeBonus { get; set; } = 1.1;

    /// <summary>Напівжиття експоненційного спаду епохи, років.</summary>
    public double EraHalfLifeYears { get; set; } = 7.0;
    /// <summary>Сигма гауссової близькості рейтингу.</summary>
    public double RatingSigma { get; set; } = 1.0;
    /// <summary>Сигма гауссової близькості тривалості, хв.</summary>
    public double RuntimeSigmaMinutes { get; set; } = 25.0;
}
