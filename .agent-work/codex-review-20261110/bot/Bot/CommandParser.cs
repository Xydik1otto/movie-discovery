namespace MovieDiscovery.Bot;

/// <summary>Розібрана команда: "/search@MyBot Matrix" → ("search", "Matrix").</summary>
public sealed record ParsedCommand(string Command, string Args);

/// <summary>
/// Парсер команд та їхніх аргументів — винесений в окремий клас для юніт-тестів.
/// Підтримує суфікс @botname, регістр не важливий.
/// </summary>
public static class CommandParser
{
    public static ParsedCommand? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '/')
            return null;

        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var raw = parts[0][1..]; // без '/'
        var at = raw.IndexOf('@');
        if (at >= 0)
            raw = raw[..at]; // /search@my_bot → search
        if (raw.Length == 0)
            return null;

        return new ParsedCommand(raw.ToLowerInvariant(), parts.Length > 1 ? parts[1] : string.Empty);
    }
}

/// <summary>
/// Парсер аргументів /similar: еталони розділені «;», зайві «;» і пробіли ігноруються,
/// дублікати (без урахування регістру) прибираються, порядок зберігається.
/// Обмеження 1–10 еталонів перевіряє обробник (API: seeds 1–10 — внутрішні id тайтлів).
/// </summary>
public static class SimilarSeedsParser
{
    public static IReadOnlyList<string> Parse(string? args)
        => (args ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>Парсер /lang: uk|ukr → "uk", en|eng → "en", інакше null (російської мови нема — свідомо).</summary>
public static class LangParser
{
    public static string? Parse(string? args)
    {
        var t = (args ?? string.Empty).Trim().ToLowerInvariant();
        return t switch
        {
            "uk" or "ukr" => "uk",
            "en" or "eng" => "en",
            _ => null
        };
    }
}

/// <summary>Парсер номера сторінки: ≥ 1, за замовчуванням 1.</summary>
public static class PageParser
{
    public static int Parse(string? args)
        => int.TryParse((args ?? string.Empty).Trim(), out var page) && page > 0 ? page : 1;
}
