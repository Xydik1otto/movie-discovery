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
    public const string Liked = "liked";     // FR-17
    public const string Disliked = "disliked"; // FR-17
    public const string Watching = "watching"; // дивиться зараз (паритет статусів Shikimori)
    public const string Reading = "reading";   // читає зараз — лише для манги
    public const string OnHold = "on_hold";    // відкладено
    public const string Dropped = "dropped";   // кинуто

    /// <summary>Усі 9 kind списків (базові 5 + паритет статусів трекерів).</summary>
    public static readonly string[] All = [Watchlist, Watched, Read, Liked, Disliked, Watching, Reading, OnHold, Dropped];

    /// <summary>
    /// Kind, які може створити імпорт трекерів. Ре-імпорт переносить наявні записи МІЖ ними
    /// (напр. watchlist → watching після розширення мапінгу), а не дублює — см. ImportService.ApplyBatchAsync.
    /// </summary>
    public static readonly string[] ImportProduced = [Watchlist, Watched, Read, Watching, Reading, OnHold, Dropped];

    /// <summary>
    /// Види списків, які рахуються як «вже в списках» для discover-кандидатів (FR-18):
    /// ВСІ 9 — нічого з будь-якого списку не рекомендувати повторно.
    /// </summary>
    public static readonly string[] DiscoverExcluded = All;

    public static bool IsValid(string? kind) =>
        kind is Watchlist or Watched or Read or Liked or Disliked or Watching or Reading or OnHold or Dropped;

    /// <summary>Оцінка 1–10 або відсутня (FR-17).</summary>
    public static bool IsValidRating(int? rating) => rating is null or (>= 1 and <= 10);

    /// <summary>kind списку → дія interaction_log (для перерахунку профілю смаку).
    /// watching/reading → "watchlist" (сигнал інтересу для смаку); on_hold/dropped → null (на смак не впливають).</summary>
    public static string? ActionForKind(string kind) => kind switch
    {
        Liked => "like",
        Disliked => "dislike",
        Watchlist => "watchlist",
        Watching or Reading => "watchlist",
        Watched or Read => "watched",
        _ => null, // on_hold/dropped — не впливають на смак (RecomputeAsync: "open" → вага 0)
    };

    /// <summary>kind=read|reading валідні лише для type='manga' (інакше контролер віддає 400); решта — для всіх типів.</summary>
    public static bool AllowedForType(string kind, string type)
    {
        if (string.Equals(kind, Read, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, Reading, StringComparison.OrdinalIgnoreCase))
            return string.Equals(type, "manga", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>
    /// Статус трекера → kind списку (повний паритет Shikimori/MAL/AniList):
    /// planned/plan_to_watch/plan_to_read/planning → watchlist («Хочу подивитись» — семантика збігається);
    /// watching/current/repeating/rewatching → watching; reading → reading (лише манга; для аніме
    /// reading не буває — фолбек watching); completed → watched (аніме) / read (манга);
    /// on_hold/paused → on_hold; dropped → dropped; невідоме → null (скіп).
    /// </summary>
    public static string? KindForStatus(string? status, bool isManga) => status?.Trim().ToLowerInvariant() switch
    {
        "completed" => isManga ? Read : Watched,
        "watching" or "current" or "repeating" or "rewatching" => Watching,
        "reading" => isManga ? Reading : Watching,
        "planned" or "plan_to_watch" or "plan_to_read" or "planning" => Watchlist,
        "on_hold" => OnHold,
        "paused" => OnHold,
        "dropped" => Dropped,
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
    public int WatchingAdded { get; set; }
    public int ReadingAdded { get; set; }
    public int OnHoldAdded { get; set; }
    public int DroppedAdded { get; set; }
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
        ["watchingAdded"] = WatchingAdded,
        ["readingAdded"] = ReadingAdded,
        ["onHoldAdded"] = OnHoldAdded,
        ["droppedAdded"] = DroppedAdded,
        ["skipped"] = Skipped,
        ["errors"] = Errors.Take(20).ToList(),
    });
}
