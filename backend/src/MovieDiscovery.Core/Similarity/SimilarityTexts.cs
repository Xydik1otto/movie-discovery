namespace MovieDiscovery.Core.Similarity;

/// <summary>
/// Локалізовані причини схожості (топ-3 внески). Два набори рядків у коді: uk та en.
/// Російської мови нема — свідоме рішення власника.
/// </summary>
public static class SimilarityTexts
{
    public static List<string> BuildReasons(
        IReadOnlyList<SignalContribution> contributions, int seedCount, string lang)
    {
        var uk = IsUk(lang);
        var reasons = new List<string>();

        foreach (var c in contributions
                     .Where(c => c.Value > 0.01)
                     .OrderByDescending(c => c.Weight * c.Value)
                     .ThenBy(c => c.Signal))
        {
            if (reasons.Count >= 3) break;
            var suffix = seedCount > 1 && c.Signal is "genres" or "keywords" or "credits"
                ? uk
                    ? $" (у {SupportSeeds(c, seedCount)} з {seedCount} еталонів)"
                    : $" (in {SupportSeeds(c, seedCount)} of {seedCount} seeds)"
                : "";

            switch (c.Signal)
            {
                case "semantic":
                    reasons.Add(uk ? "Схожий тон опису" : "Similar plot tone");
                    break;
                case "genres":
                    if (c.Items.Count > 0)
                        reasons.Add((uk ? "Спільні жанри: " : "Shared genres: ") + Join(c.Items) + suffix);
                    break;
                case "keywords":
                    if (c.Items.Count > 0)
                        reasons.Add((uk ? "Спільні ключові слова: " : "Shared keywords: ") + Join(c.Items) + suffix);
                    break;
                case "credits":
                    foreach (var dir in ItemsWithRole(c, "director"))
                    {
                        if (reasons.Count >= 3) break;
                        reasons.Add((uk ? "Спільний режисер: " : "Shared director: ") + dir + suffix);
                    }
                    if (reasons.Count < 3)
                    {
                        var actors = ItemsWithRole(c, "actor").ToList();
                        if (actors.Count > 0)
                            reasons.Add((uk ? "Спільні актори: " : "Shared actors: ") + Join(actors) + suffix);
                    }
                    break;
                case "era":
                    reasons.Add(uk ? "Близький рік випуску" : "Close release year");
                    break;
                case "numeric":
                    reasons.Add(uk ? "Схожий рейтинг і хронометраж" : "Similar rating and runtime");
                    break;
                case "tmdb":
                    reasons.Add(uk ? "TMDB відносить їх до схожих" : "TMDB lists them as similar");
                    break;
            }
        }

        return reasons;
    }

    /// <summary>Скільки еталонів підтримують сигнал: k = round(Support × n), обмежене 1..n.</summary>
    private static int SupportSeeds(SignalContribution c, int seedCount)
    {
        var k = (int)Math.Round(Math.Clamp(c.Support, 0.0, 1.0) * seedCount);
        return Math.Clamp(k, 1, seedCount);
    }

    private static IEnumerable<string> ItemsWithRole(SignalContribution c, string role) =>
        c.Items
            .Where(i => i.StartsWith(role + ":", StringComparison.Ordinal))
            .Select(i => i[(role.Length + 1)..])
            .Take(3);

    private static string Join(IReadOnlyList<string> items) => string.Join(", ", items);

    public static bool IsUk(string? lang) => !string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);
}
