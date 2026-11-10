using System.Text.Json;
using System.Text.RegularExpressions;
using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Shikimori rate → Title-патч. title_en — ромадзі (поле name); поле «russian» (кирилиця) →
/// title_ru fill-only (політика власника 10.11: підтягуємо російські назви, але існуючу
/// ru-назву не перезаписуємо; у title_uk russian НЕ пише — uk Shikimori не має). Shikimori
/// має власні id (не MAL) → зберігаємо в shikimori_id.
/// </summary>
public static class ShikimoriMapper
{
    public static Title BuildAnime(ShikimoriRate r) => Build("anime", r.Anime, isManga: false);

    public static Title BuildManga(ShikimoriRate r) => Build("manga", r.Manga, isManga: true);

    public static void ApplyAnime(Title t, ShikimoriRate r) => Apply(t, r.Anime, isManga: false);

    public static void ApplyManga(Title t, ShikimoriRate r) => Apply(t, r.Manga, isManga: true);

    private static Title Build(string type, ShikimoriMedia? m, bool isManga)
    {
        var t = new Title { Type = type };
        Apply(t, m, isManga);
        return t;
    }

    private static void Apply(Title t, ShikimoriMedia? m, bool isManga)
    {
        if (m is null) return;
        t.ShikimoriId = m.Id > 0 ? (int)m.Id : null;
        // title_en — ЛИШЕ ромадзі (name). russian (кирилиця) → title_ru fill-only (політика
        // власника 10.11): пишемо лише коли існуюча ru-назва порожня, непорожню не змінюємо;
        // russian не потрапляє ні в title_en, ні в title_uk.
        t.TitleEn = JikanMediaMapper.FirstNonEmpty(m.Name) ?? t.TitleEn ?? "";
        if (!string.IsNullOrWhiteSpace(m.Russian) && string.IsNullOrWhiteSpace(t.TitleRu))
            t.TitleRu = m.Russian.Trim();
        t.Year = YearOf(m);
        t.Genres = (m.Genres ?? [])
            .Select(g => AnimeGenres.Canonical(g.Name))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        t.Format = AnimeFormats.Normalize(m.Kind);
        t.Episodes = !isManga && m.Episodes is > 0 ? m.Episodes : null;
        t.Chapters = isManga && m.Chapters is > 0 ? m.Chapters : null;
        t.Volumes = isManga && m.Volumes is > 0 ? m.Volumes : null;
        t.Studios = !isManga ? Names(m.Studios) : t.Studios;
        t.ScoreShikimori = m.Score is > 0 ? m.Score : null;
        t.PosterPath = PosterUrl(m.Image);
        t.Status = StatusOf(m.Status) ?? t.Status;
    }

    private static int? YearOf(ShikimoriMedia m)
    {
        if (m.AiredOn is { Length: >= 4 } s && int.TryParse(s.AsSpan(0, 4), out var y) && y is > 1900 and < 3000)
            return y;
        // fallback: season "spring_2009" → 2009
        var match = Regex.Match(m.Season ?? string.Empty, @"(\d{4})$");
        return match.Success && int.TryParse(match.Groups[1].Value, out var sy) ? sy : null;
    }

    /// <summary>Image у rates — шлях-строка («/system/...») або об'єкт {original|preview}. → повний URL.</summary>
    private static string? PosterUrl(JsonElement? image)
    {
        if (image is not { } el) return null;
        string? path = el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Object => el.TryGetProperty("original", out var orig) ? orig.GetString()
                : el.TryGetProperty("preview", out var prev) ? prev.GetString()
                : el.TryGetProperty("url", out var url) ? url.GetString()
                : null,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"https://shikimori.one{path}";
    }

    private static string? StatusOf(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "released" or "finished" => "finished",
        "ongoing" => "airing",
        _ => null, // anons тощо
    };

    private static string[]? Names(List<ShikimoriNamed>? list) =>
        list is not { Count: > 0 }
            ? null
            : list.Select(n => n.Name?.Trim()).OfType<string>().Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal).ToArray();
}
