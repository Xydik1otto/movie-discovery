using MovieDiscovery.Bot.Api;

namespace MovieDiscovery.Bot;

/// <summary>Результат розбивки на частини: чанки ≤ SafeMessageLength і номер першого елемента останнього чанка.</summary>
public sealed record ChunkedResults(IReadOnlyList<string> Chunks, int LastChunkFirstNumber);

/// <summary>
/// Чисте форматування відповідей бота (без Telegram SDK) — легко юніт-тестується.
/// Усе, що йде в повідомлення з ParseMode.Html, тут уже екрановане.
/// </summary>
public static class Renderer
{
    /// <summary>Ліміт Telegram — 4096 символів на повідомлення.</summary>
    public const int MaxMessageLength = 4096;

    /// <summary>Запас під HTML-теги/емодзі: чанка довжина тримається нижче цього.</summary>
    public const int SafeMessageLength = 3900;

    public static string FormatSearchHeader(string query, int page, int totalPages, int total, string lang)
        => Strings.F(lang, "search.header", TextFormatter.Escape(query), page, totalPages, total);

    /// <summary>Блок одного тайтла: номер + емодзі типу + назва/рік/рейтинг (+ опис, + причина) + посилання TMDB.</summary>
    public static string FormatTitleBlock(
        int number,
        TitleCard t,
        string lang,
        int overviewMax = 300,
        bool includeOverview = true,
        int? userRating = null)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append(number).Append(". ")
          .Append(TextFormatter.TypeEmoji(t.Type)).Append(" <b>")
          .Append(TextFormatter.Escape(t.Title)).Append("</b>");

        if (t.Year is { } year)
            sb.Append(" (").Append(year).Append(')');

        var ratings = TextFormatter.FormatRatings(t.RatingTmdb, t.RatingImdb);
        if (ratings.Length > 0)
            sb.Append(" · ").Append(ratings);

        if (t.Score is { } score)
            sb.Append(" · ").Append(Strings.F(lang, "match.score", (int)Math.Round(score * 100)));

        if (t.Seasons is { } seasons && seasons > 0)
            sb.Append(" · ").Append(Strings.F(lang, "title.seasons", seasons));

        if (userRating is { } rating)
            sb.Append(" · ").Append(Strings.F(lang, "lists.myRating", rating));

        if (t.Genres is { Count: > 0 } genres)
            sb.Append('\n').Append(TextFormatter.Escape(string.Join(", ", genres.Take(3))));

        if (includeOverview)
        {
            var overview = TextFormatter.Truncate(t.Overview, overviewMax);
            if (overview.Length > 0)
                sb.Append("\n<i>").Append(TextFormatter.Escape(overview)).Append("</i>");
        }

        sb.Append("\n🔗 <a href=\"").Append(TextFormatter.TmdbLink(t.Type, t.TmdbId)).Append("\">TMDB</a>");

        if (t.Reasons is { Count: > 0 } reasons)
        {
            foreach (var reason in reasons.Take(3))
                sb.Append("\n🧩 ").Append(TextFormatter.Escape(reason));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Збирає блоки у повідомлення ≤ SafeMessageLength: перший чанк починається із заголовка.
    /// Блок i має номер firstNumber + i (наскрізна нумерація між сторінками).
    /// </summary>
    public static ChunkedResults ChunkBlocks(string header, IReadOnlyList<string> blocks, int firstNumber = 1)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        var lastFirst = firstNumber;
        var firstInCurrent = firstNumber;

        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            if (current.Length == 0)
            {
                if (chunks.Count == 0 && header.Length > 0)
                    current.Append(header).Append("\n\n");
                firstInCurrent = firstNumber + i;
                current.Append(block);
                continue;
            }

            if (current.Length + 2 + block.Length > SafeMessageLength)
            {
                chunks.Add(current.ToString());
                lastFirst = firstInCurrent;
                current.Clear();
                firstInCurrent = firstNumber + i;
                current.Append(block);
            }
            else
            {
                current.Append("\n\n").Append(block);
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
            lastFirst = firstInCurrent;
        }

        if (chunks.Count == 0)
            chunks.Add(header);

        return new ChunkedResults(chunks, lastFirst);
    }
}
