namespace MovieDiscovery.Core.Similarity;

/// <summary>
/// Утиліти локалізації для схожості. Російської мови нема — свідоме рішення власника.
/// FR-21: причини схожості повертаються машинними кодами (SimilarityReasons) — фронт локалізує.
/// </summary>
public static class SimilarityTexts
{
    public static bool IsUk(string? lang) => !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
}
