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
        var baseQuery = db.Titles.AsNoTracking()
            .Where(t => !excluded.Contains(t.Id) && t.PosterPath != null && !string.IsNullOrEmpty(t.TitleEn));
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
