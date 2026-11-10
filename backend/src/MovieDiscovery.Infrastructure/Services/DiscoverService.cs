using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Discover;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>
/// Режим «Кінотека-свайпи» (FR-18): кандидати, яких нема в liked/disliked/watched/watchlist юзера;
/// скоринг DiscoverScorer за профілем; мікс 70/20/10 (exploit/safe/explore); diversity ≤2 жанру.
/// Anti-fatigue streak-логіка скасована власником (07.11.2026) — дизлайки без обмежень.
/// </summary>
public sealed class DiscoverService(
    MovieDiscoveryDbContext db,
    TasteProfileService taste,
    UserListService lists,
    IOptions<DiscoverOptions> options)
{
    public DiscoverOptions Options { get; } = options.Value;

    /// <param name="allowAdult">опція (дефолт false): is_adult=false, якщо не дозволено.</param>
    public async Task<(List<Title> Items, string Mode)> GetFeedAsync(
        string userId, int count, string lang, bool allowAdult, CancellationToken ct = default)
    {
        count = Math.Clamp(count, 1, 50);
        var up = await taste.ResolveAsync(userId, ct);
        var profile = await taste.GetDataAsync(up, ct);
        var excluded = await db.ListEntries.AsNoTracking()
            .Where(l => l.ProfileId == up.ProfileId)
            .Select(l => l.TitleId)
            .ToListAsync(ct);

        var poolSize = Math.Max(count * 30, 300);
        // БЕЗ фільтра по PosterPath: IMDb-рядки не мають постерів (це поле TMDB, dormant) —
        // з фільтром топ-пул сканує весь індекс 2.4 млн рядків (30 с таймаут). UI має плейсхолдер.
        var baseQuery = db.Titles.AsNoTracking()
            .Where(t => !excluded.Contains(t.Id) && !string.IsNullOrEmpty(t.TitleEn));
        if (!allowAdult)
            baseQuery = baseQuery.Where(t => !t.IsAdult);

        // Пул 2 (база): загальна популярність.
        var candidates = await baseQuery
            .OrderByDescending(t => t.Popularity).Take(poolSize).ToListAsync(ct);

        // Пул 1: жанрові збіги з профілем (exploit). Npgsql — overlap у SQL (перекладено NpgsqlTranslationTests);
        // InMemory (тести) не перекладає Contains всередині масиву → клієнтський фільтр по популярному пулу.
        if (profile.Genres.Count > 0)
        {
            List<Title> byGenre;
            if (db.Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                byGenre = await WithGenreOverlap(baseQuery, profile.Genres.Keys.ToList())
                    .OrderByDescending(t => t.Popularity).Take(poolSize).ToListAsync(ct);
            }
            else
            {
                byGenre = candidates
                    .Where(t => t.Genres.Any(g => profile.Genres.ContainsKey(g)))
                    .ToList();
            }
            candidates = byGenre.Concat(candidates).DistinctBy(t => t.Id).ToList();
        }

        // Пул 3 (explore): quiz-жанри з найменшою taste-вагою.
        if (profile.QuizGenres.Count > 0)
        {
            var minQuiz = profile.QuizGenres.Min(g => profile.Genres.TryGetValue(g, out var w) ? w : 0);
            var exploreKeys = profile.QuizGenres
                .Where(g => (profile.Genres.TryGetValue(g, out var w) ? w : 0) <= minQuiz + 1e-9)
                .ToList();
            if (exploreKeys.Count > 0)
            {
                List<Title> extra;
                if (db.Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))
                {
                    extra = await WithGenreOverlap(baseQuery, exploreKeys)
                        .OrderByDescending(t => t.Popularity).Take(poolSize).ToListAsync(ct);
                }
                else
                {
                    extra = candidates
                        .Where(t => t.Genres.Any(g => exploreKeys.Contains(g, StringComparer.OrdinalIgnoreCase)))
                        .ToList();
                }
                candidates = candidates.Concat(extra).DistinctBy(t => t.Id).ToList();
            }
        }

        var scorer = new DiscoverScorer(Options);
        var popQuartile = Quartile(candidates);
        var exploreGenres = profile.QuizGenres
            .Where(g => (profile.Genres.TryGetValue(g, out var w) ? w : 0) <=
                        (profile.QuizGenres.Count == 0 ? 0 : profile.QuizGenres.Min(q => profile.Genres.TryGetValue(q, out var w2) ? w2 : 0)) + 1e-9)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<(DiscoverCandidate, double)> exploit = [], safe = [], explore = [];
        foreach (var title in candidates)
        {
            var cand = ToCandidate(title);
            var score = scorer.Score(cand, profile);
            if (title.Genres.Any(g => profile.Genres.ContainsKey(g)))
                exploit.Add((cand, score));
            if ((title.RatingImdb ?? 0) >= Options.SafeImdbThreshold || title.Popularity >= popQuartile)
                safe.Add((cand, score));
            if (exploreGenres.Count > 0 && title.Genres.Any(g => exploreGenres.Contains(g)))
                explore.Add((cand, score));
        }

        List<(DiscoverCandidate, double)> Sort(List<(DiscoverCandidate, double)> pool) =>
            pool.OrderByDescending(x => x.Item2).ToList();
        var picks = DiscoverPlanner.Build(Sort(exploit), Sort(safe), Sort(explore), count, Options);

        // mode: порожній профіль → «popularity+safe» (TZ FR-18: до квізу лише popularity+safe).
        var mode = profile.IsEmpty ? DiscoverPlanner.PoolSafe : DiscoverPlanner.PoolExploit;
        var items = picks.Select(p => p.Item.Id)
            .Select(id => FindTitle(candidates, id))
            .ToList();
        return (items, mode);
    }

    /// <summary>Дія свайпу (FR-18): like|dislike|skip|watchlist|watched → list_items + interaction_log + Recompute.</summary>
    public async Task<ListActionResult> HandleActionAsync(
        string userId, int titleId, string action, int? rating, CancellationToken ct = default)
    {
        if (action is not ("like" or "dislike" or "skip" or "watchlist" or "watched"))
            return new ListActionResult(false, "invalid_request", "action має бути like | dislike | skip | watchlist | watched");
        if (!ListKinds.IsValidRating(rating))
            return new ListActionResult(false, "invalid_request", "rating має бути 1–10");

        if (action == "skip")
        {
            // «Not now»: не впливає на профіль зовсім — лише журнал взаємодій.
            var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == titleId, ct);
            if (title is null)
                return new ListActionResult(false, "title_not_found", "Тайтл не знайдено");
            db.InteractionLogs.Add(new InteractionLog
            {
                UserId = userId, TitleId = titleId, Action = "skip", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            return new ListActionResult(true, "", "", TitleId: titleId, Kind: "skip", ProfileUpdated: false);
        }

        var kind = action switch
        {
            "like" => ListKinds.Liked,
            "dislike" => ListKinds.Disliked,
            "watchlist" => ListKinds.Watchlist,
            _ => ListKinds.Watched,
        };

        var result = await lists.AddAsync(userId, kind, titleId, null, action == "watched" ? rating : null, ct);
        if (result.Error == "already_in_list")
            return result with { Ok = true, ProfileUpdated = false }; // повторний свайп — ідемпотентно
        return result;
    }

    // ─── Персональні рекомендації Головної (GET /api/recommendations/home) ───

    /// <summary>Вага сигналу смаку за видом списку (liked 1.0 … disliked −0.8 = мінус);
    /// dropped — не сигнал. Рейтинг 7+ підсилює лише watched до 0.8; read/reading — як watchlist (0.6).</summary>
    internal static double HomeWeightOfKind(string kind, int? rating) => kind switch
    {
        ListKinds.Liked => 1.0,
        ListKinds.Watched => rating is { } r && r >= 7 ? 0.8 : 0.5,
        ListKinds.Watchlist or ListKinds.Watching or ListKinds.Read or ListKinds.Reading => 0.6,
        ListKinds.OnHold => 0.3,
        ListKinds.Disliked => -0.8,
        _ => 0.0,
    };

    /// <summary>Результат домашніх рекомендацій: тайтли + сирий смак-мотив (жанр-ключ en + домінуючий
    /// kind списку) — reason локалізує контролер, IMemoryCache per-user (TTL 5 хв) теж у контролері.</summary>
    public sealed record HomeRecommendations(List<Title> Items, string? ReasonGenre, string? ReasonKind);

    /// <summary>
    /// Персональні рекомендації Головної: (a) смак юзера зі списків (liked 1.0, watched/read ≥7 — 0.8,
    /// watched/read 0.5, watchlist/watching/read/reading 0.6, on_hold 0.3, disliked −0.8) → топ-жанри
    /// тайтлів у списках; (b) кандидати — жанровий OR-пул по popularity DESC (SearchService-style),
    /// тип будь-який, роки ±10 від медіани улюблених — soft-фільтр (пріоритет, НЕ відсікання);
    /// (c) ЖОРТКЕ виключення тайтлів, які вже в списках юзера (усі 9 kind — DiscoverExcluded);
    /// (d) диверсифікація: не більше 5 тайтлів одного жанру в топ-N.
    /// </summary>
    public async Task<HomeRecommendations> GetHomeRecommendationsAsync(
        string userId, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var up = await taste.ResolveAsync(userId, ct);

        var entries = await db.ListEntries.AsNoTracking()
            .Where(l => l.ProfileId == up.ProfileId)
            .Select(l => new { l.Kind, l.Rating, l.TitleId })
            .ToListAsync(ct);
        if (entries.Count == 0)
            return new HomeRecommendations([], null, null); // порожній профіль → порожня секція (фронт ховає)

        var listTitleIds = entries.Select(e => e.TitleId).Distinct().ToList();
        var listTitles = await db.Titles.AsNoTracking()
            .Where(t => listTitleIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Genres, t.Year })
            .ToDictionaryAsync(t => t.Id, ct);

        // (a) жанрові ваги: позитивні види списків накопичують, дизлайк дає мінус (жанр не потрапляє в топ).
        var genres = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var kindWeights = new Dictionary<(string Genre, string Kind), double>();
        var years = new List<int>();
        foreach (var e in entries)
        {
            if (!listTitles.TryGetValue(e.TitleId, out var t)) continue;
            var w = HomeWeightOfKind(e.Kind, e.Rating);
            if (Math.Abs(w) < 1e-9) continue; // dropped та інші нуль-сигнали
            if (w > 0 && t.Year is { } y) years.Add(y);
            foreach (var g in t.Genres)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                genres[g] = genres.GetValueOrDefault(g) + w;
                if (w > 0)
                    kindWeights[(g, e.Kind)] = kindWeights.GetValueOrDefault((g, e.Kind)) + w;
            }
        }

        var topGenres = genres.Where(kv => kv.Value > 1e-9)
            .OrderByDescending(kv => kv.Value).Take(8).ToList();
        if (topGenres.Count == 0)
            return new HomeRecommendations([], null, null); // лише дизлайки — рекомендувати нічого

        var topGenre = topGenres[0].Key;
        var topKind = kindWeights
            .Where(kv => kv.Key.Genre == topGenre)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key.Kind)
            .FirstOrDefault() ?? ListKinds.Liked;

        // (c) ЖОРТКЕ виключення: ВСЕ, що в будь-якому списку юзера (DiscoverExcluded — усі 9 kind).
        var baseQuery = db.Titles.AsNoTracking()
            .Where(t => !listTitleIds.Contains(t.Id) && !string.IsNullOrEmpty(t.TitleEn))
            .Where(t => !t.IsAdult);

        // (b) кандидати: жанровий OR, popularity DESC. Npgsql — overlap у SQL (перекладено
        // NpgsqlTranslationTests); InMemory (тести) не перекладає Contains всередині масиву →
        // клієнтський фільтр по популярному пулу (усталена адаптація, як у GetFeedAsync).
        var poolSize = Math.Max(limit * 10, 200);
        var genreKeys = topGenres.Select(kv => kv.Key).ToList();
        List<Title> candidates;
        if (db.Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            candidates = await WithGenreOverlap(baseQuery, genreKeys)
                .OrderByDescending(t => t.Popularity).Take(poolSize).ToListAsync(ct);
        }
        else
        {
            var popular = await baseQuery
                .OrderByDescending(t => t.Popularity).Take(Math.Max(poolSize * 3, 600)).ToListAsync(ct);
            candidates = popular
                .Where(t => t.Genres.Any(g => genreKeys.Contains(g, StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }
        if (candidates.Count == 0)
            return new HomeRecommendations([], topGenre, topKind);

        // Роки ±10 від медіани улюблених — soft-фільтр: in-range ідуть першими, поза діапазоном НЕ відсікаються.
        var median = MedianYear(years);
        var ordered = candidates
            .OrderByDescending(t => InYearWindow(t.Year, median))
            .ThenByDescending(t => t.Popularity)
            .ToList();

        // (d) диверсифікація: не більше 5 тайтлів одного жанру в топ-N.
        const int PerGenreCap = 5;
        var perGenre = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var picked = new List<Title>(limit);
        foreach (var t in ordered)
        {
            if (picked.Count >= limit) break;
            if (t.Genres.Any(g => perGenre.GetValueOrDefault(g) >= PerGenreCap)) continue;
            picked.Add(t);
            foreach (var g in t.Genres)
                perGenre[g] = perGenre.GetValueOrDefault(g) + 1;
        }
        return new HomeRecommendations(picked, topGenre, topKind);
    }

    private static int? MedianYear(List<int> years)
    {
        if (years.Count == 0) return null;
        var sorted = years.OrderBy(y => y).ToList();
        return sorted[sorted.Count / 2];
    }

    private static bool InYearWindow(int? year, int? median) =>
        median is null || (year is { } y && Math.Abs(y - median.Value) <= 10);

    /// <summary>GENRES.Any(g => keys.Contains(g)) — підтверджено-перекладний патерн (NpgsqlTranslationTests).</summary>
    private static IQueryable<Title> WithGenreOverlap(IQueryable<Title> query, IReadOnlyList<string> keys) =>
        query.Where(t => t.Genres.Any(g => keys.Contains(g)));

    private static double Quartile(IReadOnlyList<Title> candidates)
    {
        if (candidates.Count == 0) return 0;
        var sorted = candidates.Select(t => t.Popularity).OrderByDescending(p => p).ToList();
        return sorted[Math.Min(sorted.Count - 1, sorted.Count / 4)];
    }

    private static DiscoverCandidate ToCandidate(Title t) => new(
        t.Id, t.TmdbId, t.Type, t.TitleUk, t.TitleEn, t.Year, t.Genres, t.Actors,
        t.RatingTmdb, t.RatingImdb, t.VotesImdb, t.Popularity, t.PosterPath, t.OverviewUk, t.OverviewEn);

    private static Title FindTitle(IReadOnlyList<Title> titles, int id) =>
        titles.First(t => t.Id == id);
}
