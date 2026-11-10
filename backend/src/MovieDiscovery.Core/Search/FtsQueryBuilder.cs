using System.Buffers;
using System.Text;

namespace MovieDiscovery.Core.Search;

/// <summary>
/// Перетворення сирого рядка пошуку у tsquery для Фази A (FTS) — конфіг 'simple'.
/// Правила: trim → split по whitespace → кожен токен lowercase + вирізати
/// tsquery-спецсимволи (&amp; | ! ( ) : * ' " \ / &lt; &gt;) → відкинути стопворди
/// (en/uk/ru) і токени &lt;2 символів → до кожного додати ':*' (префіксний збіг)
/// → з'єднати ' &amp; '. Жодного токена → null (клиент іде в ILIKE-фазу B як зараз).
/// Токени без жодної літери/цифри (емодзі, тире, ©) відкидаються: парсер tsquery
/// класифікує їх як blank/символи і '🚀:*' дає syntax error замість результату.
/// </summary>
public static class FtsQueryBuilder
{
    // tsquery-синтаксис: ці символи в токені ламають to_tsquery ('don't', 'C++', 'a*b'),
    // тому вирізаються до склейки; ':*' до токена додається ПІСЛЯ чистки.
    private static readonly SearchValues<char> TsQuerySpecials =
        SearchValues.Create("&|!():*'\"\\/<>");

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        // en
        "the", "a", "an", "of", "in", "on", "at", "to", "and", "or", "for", "with",
        "is", "was", "my", "me", "it", "its", "this", "that", "be", "as", "by", "from",
        // uk / ru
        "і", "в", "на", "з", "та", "у", "до", "за", "по", "не", "що", "це", "й", "або",
        "из", "и", "с", "к", "о", "а", "но", "или", "для",
    };

    public static string? Build(string? rawQ)
    {
        if (string.IsNullOrWhiteSpace(rawQ))
            return null;

        List<string> tokens = [];
        foreach (var raw in rawQ.Split(
                     (char[]?)null,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw)
            {
                if (TsQuerySpecials.Contains(ch))
                    continue;
                sb.Append(char.ToLowerInvariant(ch));
            }

            var tok = sb.ToString();
            if (tok.Length < 2 || StopWords.Contains(tok) || !HasLetterOrDigit(tok))
                continue;

            tokens.Add(tok + ":*");
        }

        return tokens.Count == 0 ? null : string.Join(" & ", tokens);
    }

    private static bool HasLetterOrDigit(string tok)
    {
        foreach (var ch in tok)
            if (char.IsLetterOrDigit(ch))
                return true;
        return false;
    }
}
