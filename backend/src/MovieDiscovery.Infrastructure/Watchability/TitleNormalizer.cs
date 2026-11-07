using System.Text;
using System.Text.RegularExpressions;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>
/// Нормалізація назв для матчингу тайтлів (FR-14): lowercase, без пунктуації,
/// без en-артиклів (the/a/an) на початку, без «(1968)»-року в хвості, стиснуті пробіли.
/// </summary>
public static partial class TitleNormalizer
{
    [GeneratedRegex("\\(\\d{4}\\)\\s*$")]
    private static partial Regex TrailingYear();

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        var s = TrailingYear().Replace(name.Trim(), "").ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');

        var normalized = string.Join(' ',
            sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // en-артиклі на початку («The Office» → «office»)
        foreach (var article in (string[])["the ", "a ", "an "])
        {
            if (!normalized.StartsWith(article, StringComparison.Ordinal)) continue;
            normalized = normalized[article.Length..].Trim();
            break;
        }
        return normalized;
    }
}
