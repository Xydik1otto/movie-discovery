namespace MovieDiscovery.Infrastructure.Imdb;

/// <summary>Рядок title.basics.tsv (IMDb datasets).</summary>
public sealed record ImdbBasicsRow(
    string Tconst,
    string TitleType,
    string PrimaryTitle,
    string OriginalTitle,
    bool IsAdult,
    int? StartYear,
    int? RuntimeMinutes,
    string[] Genres);

/// <summary>Рядок title.ratings.tsv (IMDb datasets).</summary>
public sealed record ImdbRatingRow(string Tconst, double AverageRating, int NumVotes);

/// <summary>
/// Потоковий парсер TSV IMDb datasets (FR-15). Чисті функції — без I/O, покриваються тестами.
/// "\N" — відсутнє значення; рядки нестандартної форми пропускаються (не ламають потік).
/// </summary>
public static class ImdbTsv
{
    public const string NullField = "\\N";

    /// <summary>Розібрати рядок basics; некоректний рядок → null.</summary>
    public static ImdbBasicsRow? ParseBasics(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split('\t');
        if (parts.Length < 9) return null;
        if (string.IsNullOrWhiteSpace(parts[0]) || parts[0] == NullField) return null;

        var genres = parts[8] == NullField || parts[8].Length == 0
            ? Array.Empty<string>()
            : parts[8].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ImdbBasicsRow(
            parts[0].Trim(),
            parts[1].Trim(),
            Field(parts[2]),
            Field(parts[3]),
            parts[4].Trim() == "1",
            NullableInt(parts[5]),
            NullableInt(parts[7]),
            genres);
    }

    /// <summary>Розібрати рядок ratings; некоректний рядок → null.</summary>
    public static ImdbRatingRow? ParseRating(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var parts = line.Split('\t');
        if (parts.Length < 3) return null;
        if (string.IsNullOrWhiteSpace(parts[0]) || parts[0] == NullField) return null;
        if (!double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var rating)) return null;
        if (!int.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var votes)) return null;
        return new ImdbRatingRow(parts[0].Trim(), rating, votes);
    }

    private static string Field(string s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim() == NullField ? "" : s.Trim();

    private static int? NullableInt(string s)
    {
        var t = s.Trim();
        if (t.Length == 0 || t == NullField) return null;
        return int.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
