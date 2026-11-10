using System.Globalization;
using System.Text;

namespace MovieDiscovery.Bot;

/// <summary>Форматування повідомлень: HTML-екранування, обрізання опису, емодзі, посилання TMDB.</summary>
public static class TextFormatter
{
    /// <summary>Екранує рядок для Telegram HTML (&lt; &gt; &amp;). Застосовується до ВСІХ динамічних рядків.</summary>
    public static string Escape(string? s)
        => s is null ? string.Empty : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Схлопує пробіли, переноси рядків і табуляції в один пробіл, обрізає краї.</summary>
    public static string NormalizeWhitespace(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Обрізає опис до ~max символів, preferably на межі слова, додає «…».</summary>
    public static string Truncate(string? s, int max = 300)
    {
        if (max <= 0)
            return string.Empty;

        var text = NormalizeWhitespace(s);
        if (text.Length <= max)
            return text;

        var cut = text[..max];
        var space = cut.LastIndexOf(' ');
        if (space > max / 2)
            cut = cut[..space];
        return cut.TrimEnd() + "…";
    }

    /// <summary>Емодзі за типом тайтла: movie → 🎬, series → 📺, show → 🎙️.</summary>
    public static string TypeEmoji(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "movie" => "🎬",
        "series" => "📺",
        "show" => "🎙️",
        _ => "🎞️"
    };

    /// <summary>Емодзі за kind списку: watchlist → 📌, watched → ✅.</summary>
    public static string KindEmoji(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "watchlist" => "📌",
        "watched" => "✅",
        _ => "📋"
    };

    /// <summary>movie → https://www.themoviedb.org/movie/{id}; series|show → /tv/{id}. null, якщо tmdbId відсутній.</summary>
    public static string? TmdbLink(string? type, int? tmdbId)
        => tmdbId is null or <= 0
            ? null
            : $"https://www.themoviedb.org/{(IsSeries(type) ? "tv" : "movie")}/{tmdbId}";

    public static bool IsSeries(string? type) => type is "series" or "show";

    /// <summary>«⭐ 7.8 · IMDb 8.1» (крапка — invariant, щоб не залежати від культури процесу).</summary>
    public static string FormatRatings(double? tmdb, double? imdb)
    {
        var parts = new List<string>(2);
        if (tmdb is { } t)
            parts.Add($"⭐ {t.ToString("0.0", CultureInfo.InvariantCulture)}");
        if (imdb is { } i)
            parts.Add($"IMDb {i.ToString("0.0", CultureInfo.InvariantCulture)}");
        return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
    }
}
