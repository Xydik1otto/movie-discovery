using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Discover;
using MovieDiscovery.Core.Taste;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>
/// Профіль смаку юзера (FR-17): інкрементальний перерахунок user_profiles з interaction_log
/// після кожної дії (like +1.0, rate (r−5.5)/4.5×1.2, watchlist +0.4, dislike −0.8;
/// часовий спад — напівжиття 60 днів за created_at). Ваги — ProfileOptions.
/// </summary>
public sealed class TasteProfileService(MovieDiscoveryDbContext db, IOptions<ProfileOptions> options)
{
    public ProfileOptions Options { get; } = options.Value;

    /// <summary>
    /// Профіль юзера; за потреби створює user_profiles + постійний профіль списків (зв'язка FR-16).
    /// Гонка реєстрації безпечна: unique index на user_id → при конфлікті перечитуємо.
    /// </summary>
    public async Task<UserProfile> ResolveAsync(string userId, CancellationToken ct = default)
    {
        var up = await db.UserProfiles.Include(x => x.Profile)
            .FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (up is not null) return up;

        // КОЖЕН залогінений юзер отримує ВЛАСНИЙ профіль (ізоляція даних між користувачами).
        // Дефолтний профіль (id=1) — лише для анонімного гостьового режиму, юзерам НЕ приріплюється.
        var ownProfile = db.Profiles.Add(new Profile
        {
            Name = $"Профіль {(userId.Length <= 8 ? userId : userId[..8])}",
            IsDefault = false,
        }).Entity;
        up = new UserProfile { UserId = userId, Profile = ownProfile };
        db.UserProfiles.Add(up);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Паралельний запит уже створив — перечитуємо (unique user_id).
            db.Entry(up).State = EntityState.Detached;
            up = await db.UserProfiles.Include(x => x.Profile)
                .FirstAsync(x => x.UserId == userId, ct);
        }
        return up;
    }

    /// <summary>Розібраний профіль (без I/O) для скорингу discover.</summary>
    public async Task<TasteProfileData> GetDataAsync(UserProfile up, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return new TasteProfileData
        {
            Genres = TasteJson.Parse(up.TasteGenres),
            Keywords = TasteJson.Parse(up.TasteKeywords),
            Era = TasteJson.Parse(up.TasteEra),
            Actors = TasteJson.Parse(up.TasteActors),
            NegativeGenres = TasteJson.Parse(up.NegativeGenres),
            QuizGenres = ParseQuizGenres(up.Quiz),
        };
    }

    /// <summary>quiz jsonb → жанрові ключі цікавостей (picks + mood) для explore-пулу.</summary>
    public static List<string> ParseQuizGenres(string? quiz) =>
        ParseQuiz(quiz).Picks.Concat(ParseQuiz(quiz).Mood).Distinct().ToList();

    /// <summary>Розбір quiz jsonb (picks / mood / era) — для prior-внеску в Recompute.</summary>
    public static (List<string> Picks, List<string> Mood, List<string> Era) ParseQuiz(string? quiz)
    {
        if (string.IsNullOrWhiteSpace(quiz)) return ([], [], []);
        try
        {
            using var doc = JsonDocument.Parse(quiz);
            var root = doc.RootElement;
            List<string> Get(string key) =>
                root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!.Trim().ToLowerInvariant())
                        .Where(s => s.Length > 0)
                        .ToList()
                    : [];
            return (Get("picks"), Get("mood"), Get("era"));
        }
        catch (JsonException)
        {
            return ([], [], []);
        }
    }

    /// <summary>Повний перерахунок профілю з interaction_log (інкрементальний виклик після кожної дії).</summary>
    public async Task<UserProfile> RecomputeAsync(string userId, CancellationToken ct = default)
    {
        var up = await ResolveAsync(userId, ct);
        var rows = await db.InteractionLogs.AsNoTracking()
            .Include(l => l.Title)
            .Where(l => l.UserId == userId)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;

        var genres = new Dictionary<string, double>();
        var keywords = new Dictionary<string, double>();
        var era = new Dictionary<string, double>();
        var actors = new Dictionary<string, double>();
        var negative = new Dictionary<string, double>();

        foreach (var row in rows)
        {
            var title = row.Title;
            if (title is null) continue; // тайтл видалено — внесок не рахуємо
            var decay = ProfileOptions.Decay((now - row.CreatedAt).TotalDays, Options.HalfLifeDays);
            var weight = row.Action switch
            {
                "like" => Options.LikeWeight * (row.Value ?? 1) * decay, // Value=2 → фаворит онбордингу (2 лайки)
                "rate" => RateWeight(row.Value) * decay,
                "watched" => row.Value is not null ? RateWeight(row.Value) * decay : Options.WatchlistWeight * decay,
                "watchlist" => Options.WatchlistWeight * decay,
                "dislike" => Options.DislikeWeight * decay,
                _ => 0.0, // skip/open — на профіль не впливають (FR-18)
            };
            if (Math.Abs(weight) < 1e-9) continue;

            AddAll(genres, title.Genres, weight);
            AddAll(keywords, title.Keywords, weight);
            AddAll(actors, title.Actors, weight);
            if (DiscoverScorer.DecadeOf(title.Year) is { } decade)
                AddAll(era, [decade], weight);
            if (row.Action == "dislike")
                AddAll(negative, title.Genres, Math.Abs(Options.DislikeWeight) * decay);
        }

        // Cold-start prior з онбордингу (quiz jsonb): picks+mood ×0.6 у жанри, era ×0.6 —
        // застосовується при КОЖНОМУ перерахунку, щоб внесок квізу не губився при наступних Recompute.
        var (quizPicks, quizMood, quizEra) = ParseQuiz(up.Quiz);
        foreach (var g in quizPicks.Concat(quizMood).Distinct())
            AddAll(genres, [g], Options.QuizPickWeight);
        foreach (var e in quizEra)
            AddAll(era, [e], Options.QuizPickWeight);

        up.TasteGenres = TasteJson.Serialize(genres);
        up.TasteKeywords = TasteJson.Serialize(keywords);
        up.TasteEra = TasteJson.Serialize(era);
        up.TasteActors = TasteJson.Serialize(actors);
        up.NegativeGenres = TasteJson.Serialize(negative);
        up.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return up;
    }

    /// <summary>
    /// Онбординг (FR-18): cold-start профіль — picks×0.6, улюблені тайтли як 2 лайки (interaction_log like Value=2);
    /// quiz зберігається в jsonb; skipped → onboarding_done=false (квіз можна пропустити, TZ FR-18).
    /// </summary>
    public async Task<UserProfile> ApplyOnboardingAsync(
        string userId,
        IReadOnlyList<string> picks,
        IReadOnlyList<string> mood,
        IReadOnlyList<string> era,
        IReadOnlyList<int> favoriteTitles,
        bool skipped,
        CancellationToken ct = default)
    {
        var up = await ResolveAsync(userId, ct);
        var genres = TasteJson.Parse(up.TasteGenres);
        var eraMap = TasteJson.Parse(up.TasteEra);
        var keywords = TasteJson.Parse(up.TasteKeywords);
        var actors = TasteJson.Parse(up.TasteActors);

        foreach (var g in picks) AddAll(genres, [NormalizeKey(g)], Options.QuizPickWeight);
        foreach (var g in mood) AddAll(genres, [NormalizeKey(g)], Options.QuizPickWeight);
        foreach (var e in era) AddAll(eraMap, [e.Trim().ToLowerInvariant()], Options.QuizPickWeight);

        foreach (var titleId in favoriteTitles.Distinct().Take(3))
        {
            var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == titleId, ct);
            if (title is null) continue;
            AddAll(genres, title.Genres, Options.LikeWeight * 2);
            AddAll(keywords, title.Keywords, Options.LikeWeight * 2);
            AddAll(actors, title.Actors, Options.LikeWeight * 2);
            if (DiscoverScorer.DecadeOf(title.Year) is { } decade)
                AddAll(eraMap, [decade], Options.LikeWeight * 2);
            db.InteractionLogs.Add(new InteractionLog
            {
                UserId = userId, TitleId = titleId, Action = "like", Value = 2, CreatedAt = DateTime.UtcNow,
            });
        }

        up.TasteGenres = TasteJson.Serialize(genres);
        up.TasteEra = TasteJson.Serialize(eraMap);
        up.TasteKeywords = TasteJson.Serialize(keywords);
        up.TasteActors = TasteJson.Serialize(actors);
        up.OnboardingDone = !skipped;
        up.Quiz = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["picks"] = picks, ["mood"] = mood, ["era"] = era,
            ["favoriteTitles"] = favoriteTitles, ["skipped"] = skipped,
        });
        up.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return up;
    }

    private double RateWeight(int? rating) =>
        rating is { } r ? (r - 5.5) / 4.5 * Options.RatingWeight : 0;

    /// <summary>Жанрові ключі — канонічні en lowercase (збіг з titles.genres).</summary>
    private static string NormalizeKey(string key) => key.Trim().ToLowerInvariant();

    private static void AddAll(Dictionary<string, double> map, IEnumerable<string> keys, double weight)
    {
        foreach (var k in keys)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            map[k] = map.GetValueOrDefault(k) + weight;
        }
    }
}
