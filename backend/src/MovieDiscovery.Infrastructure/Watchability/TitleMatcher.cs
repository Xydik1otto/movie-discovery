using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>
/// Матчинг тайтлів за нормалізованою назвою + рік ±1 (FR-14: Internet Archive, sweet.tv).
/// Чисті функції; кандидати з БД фільтруються запитом за роком (індекс titles(year)).
/// </summary>
public static class TitleMatcher
{
    /// <summary>
    /// Знайти кандидата: нормалізована назва (uk або en, обидві сторони) + |Δyear| ≤ 1.
    /// Пріоритет — збіг en-назви. Рік null у кандидата рахується як «рік невідомий» → збіг назви достатній.
    /// </summary>
    public static Title? Find(IReadOnlyList<Title> candidates, string? nameUk, string? nameEn, int? year)
    {
        var nUk = TitleNormalizer.Normalize(nameUk);
        var nEn = TitleNormalizer.Normalize(nameEn);
        if (nUk.Length == 0 && nEn.Length == 0) return null;

        Title? ukMatch = null;
        foreach (var c in candidates)
        {
            if (!YearMatches(c.Year, year)) continue;
            var cEn = TitleNormalizer.Normalize(c.TitleEn);
            var cUk = TitleNormalizer.Normalize(c.TitleUk);

            if (nEn.Length > 0 && (cEn == nEn || (cUk.Length > 0 && cUk == nEn))) return c; // en-збіг — найкращий
            if (ukMatch is null && nUk.Length > 0 && (cUk == nUk || (cEn.Length > 0 && cEn == nUk))) ukMatch = c;
        }
        return ukMatch;
    }

    private static bool YearMatches(int? candidateYear, int? year)
    {
        if (candidateYear is null || year is null) return true; // рік невідомий — не блокуємо
        return Math.Abs(candidateYear.Value - year.Value) <= 1;
    }
}
