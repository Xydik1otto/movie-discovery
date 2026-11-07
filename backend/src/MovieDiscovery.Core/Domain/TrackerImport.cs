using System.Text.Json;

namespace MovieDiscovery.Core.Domain;

/// <summary>Джерело імпорту списків (FR-13): публічний профіль трекера, без OAuth.</summary>
public sealed record ImportSourceInfo(string Id, string Name, string Hint, bool Authless);

/// <summary>Каталог джерел імпорту + валідація kind списків + мапінг статусів трекерів.</summary>
public static class ImportSourceCatalog
{
    public const string Mal = "mal";
    public const string Anilist = "anilist";
    public const string Shikimori = "shikimori";

    public static readonly ImportSourceInfo[] All =
    [
        new(Mal, "MyAnimeList", "нікнейм MAL", true),
        new(Anilist, "AniList", "нікнейм AniList", true),
        new(Shikimori, "Shikimori", "нікнейм або ID", true),
    ];

    public static bool IsKnown(string? source) => All.Any(s => string.Equals(s.Id, source, StringComparison.Ordinal));
}

/// <summary>Валідні kind списків і переходи статусів трекерів → kind (idempotency-семантика FR-13).</summary>
public static class ListKinds
{
    public const string Watchlist = "watchlist";
    public const string Watched = "watched";
    public const string Read = "read";

    public static bool IsValid(string? kind) => kind is Watchlist or Watched or Read;

    /// <summary>kind=read валідний лише для type='manga' (інакше контролер віддає 400).</summary>
    public static bool AllowedForType(string kind, string type) =>
        !string.Equals(kind, Read, StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "manga", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Статус трекера → kind списку: completed → watched (аніме) / read (манга);
    /// watching/reading/current/repeating → watchlist; on_hold/dropped/planned/paused → null (скіп).
    /// </summary>
    public static string? KindForStatus(string? status, bool isManga) => status?.Trim().ToLowerInvariant() switch
    {
        "completed" => isManga ? Read : Watched,
        "watching" or "reading" or "current" or "repeating" or "rewatching" => Watchlist,
        _ => null,
    };

    /// <summary>Оцінка трекера → rating 1–10: ≤0 → null; &gt;10 → /10 (POINT_100); кламп 1..10.</summary>
    public static int? RatingFromScore(double? score)
    {
        if (score is not { } s || s <= 0) return null;
        if (s > 10) s = Math.Round(s / 10d, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(s, 1, 10);
    }
}

/// <summary>Лічильники прогресу джоби імпорту (stats jsonb в import_jobs; PLAN.md).</summary>
public sealed class ImportStats
{
    public int AnimeTotal { get; set; }
    public int MangaTotal { get; set; }
    public int Matched { get; set; }
    public int Created { get; set; }
    public int AnimeWatched { get; set; }
    public int MangaRead { get; set; }
    public int WatchlistAdded { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = [];

    /// <summary>Зперіалізувати в jsonb-документ (ключі — camelCase за контрактом PLAN).</summary>
    public JsonDocument ToJsonDocument() => JsonSerializer.SerializeToDocument(new Dictionary<string, object?>
    {
        ["animeTotal"] = AnimeTotal,
        ["mangaTotal"] = MangaTotal,
        ["matched"] = Matched,
        ["created"] = Created,
        ["animeWatched"] = AnimeWatched,
        ["mangaRead"] = MangaRead,
        ["watchlistAdded"] = WatchlistAdded,
        ["skipped"] = Skipped,
        ["errors"] = Errors.Take(20).ToList(),
    });
}
