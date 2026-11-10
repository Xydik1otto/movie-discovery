using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Embeddings;

/// <summary>Текст для ембедингу тайтла: назви uk+en, описи, жанри, keywords.</summary>
public static class EmbeddingTextBuilder
{
    public static string Build(Title t)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(t.TitleUk)) sb.Append(t.TitleUk).Append(" / ");
        if (!string.IsNullOrWhiteSpace(t.TitleEn)) sb.Append(t.TitleEn);
        if (t.Year is > 0) sb.Append(" (").Append(t.Year).Append(')');
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(t.OverviewUk)) sb.AppendLine(t.OverviewUk);
        if (!string.IsNullOrWhiteSpace(t.OverviewEn)) sb.AppendLine(t.OverviewEn);
        if (t.Genres is { Length: > 0 }) sb.Append("Genres: ").Append(string.Join(", ", t.Genres)).AppendLine();
        if (t.Keywords is { Length: > 0 }) sb.Append("Keywords: ").Append(string.Join(", ", t.Keywords.Take(20))).AppendLine();
        var text = sb.ToString().Trim();
        return text.Length > 4000 ? text[..4000] : text;
    }
}
