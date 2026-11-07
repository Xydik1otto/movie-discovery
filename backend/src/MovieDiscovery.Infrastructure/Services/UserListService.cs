using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

public sealed record GenreCount(string Genre, int Count);

public sealed record ListStats(
    int WatchedCount,
    int LikedCount,
    int DislikedCount,
    double? AvgRating,
    IReadOnlyList<GenreCount> TopGenres);

public sealed record GuestListItem(string Kind, int TitleId, int? Rating);

public sealed record ListActionResult(
    bool Ok,
    string Error,
    string Message,
    int? ItemId = null,
    int? TitleId = null,
    int? TmdbId = null,
    string? Kind = null,
    int? Rating = null,
    DateTime? CreatedAt = null,
    bool ProfileUpdated = false);

/// <summary>
/// Списки авторизованого юзера (FR-16/17): liked/disliked/watchlist/watched/read
/// + interaction_log + інкрементальний RecomputeAsync після кожної дії.
/// </summary>
public sealed class UserListService(MovieDiscoveryDbContext db, TasteProfileService taste)
{
    /// <summary>Додати тайтл у список юзера. Дублікат → Ok=false already_in_list (409 у контролері).</summary>
    public async Task<ListActionResult> AddAsync(
        string userId, string kind, int? titleId, int? tmdbId, int? rating, CancellationToken ct = default)
    {
        if (!ListKinds.IsValid(kind))
            return Fail("invalid_request", "kind має бути watchlist | watched | read | liked | disliked");
        if (!ListKinds.IsValidRating(rating))
            return Fail("invalid_request", "rating має бути 1–10");

        var title = await FindTitleAsync(titleId, tmdbId, ct);
        if (title is null)
            return Fail("title_not_found", "Тайтл не знайдено");
        if (!ListKinds.AllowedForType(kind, title.Type))
            return Fail("invalid_request", "kind=read допустимий лише для манги (type=manga)");

        var up = await taste.ResolveAsync(userId, ct);
        var exists = await db.ListEntries
            .AnyAsync(l => l.ProfileId == up.ProfileId && l.Kind == kind && l.TitleId == title.Id, ct);
        if (exists)
            return Fail("already_in_list", "Тайтл уже у списку");

        var now = DateTime.UtcNow;
        var entry = new ListEntry
        {
            ProfileId = up.ProfileId, Kind = kind, TitleId = title.Id, Rating = rating,
            CreatedAt = now, WatchedAt = kind is ListKinds.Watched or ListKinds.Read ? now : null,
        };
        db.ListEntries.Add(entry);
        db.InteractionLogs.Add(new InteractionLog
        {
            UserId = userId, TitleId = title.Id,
            Action = ListKinds.ActionForKind(kind) ?? "open", Value = rating, CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await taste.RecomputeAsync(userId, ct);

        return new ListActionResult(true, "", "", entry.Id, title.Id, title.TmdbId, kind, rating, now, ProfileUpdated: true);
    }

    /// <summary>Оновити оцінку запису списку (rating 1–10).</summary>
    public async Task<ListActionResult> UpdateRatingAsync(string userId, int itemId, int? rating, CancellationToken ct = default)
    {
        if (!ListKinds.IsValidRating(rating))
            return Fail("invalid_request", "rating має бути 1–10");
        var up = await taste.ResolveAsync(userId, ct);
        var entry = await db.ListEntries.FirstOrDefaultAsync(l => l.Id == itemId && l.ProfileId == up.ProfileId, ct);
        if (entry is null)
            return Fail("not_found", "Запис списку не знайдено");

        entry.Rating = rating;
        if (entry.Kind is ListKinds.Watched or ListKinds.Read && rating.HasValue && entry.WatchedAt is null)
            entry.WatchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (entry.Kind is ListKinds.Watched or ListKinds.Read)
        {
            // оцінка — окрема дія rate у журналі (профіль перераховується)
            db.InteractionLogs.Add(new InteractionLog
            {
                UserId = userId, TitleId = entry.TitleId, Action = "rate",
                Value = rating, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await taste.RecomputeAsync(userId, ct);
        }
        return new ListActionResult(true, "", "", entry.Id, entry.TitleId, Kind: entry.Kind, Rating: entry.Rating, ProfileUpdated: true);
    }

    /// <summary>Видалити запис списку юзера (404-безпечне: чужі записи не відрізняємо від відсутніх).</summary>
    public async Task<bool> DeleteAsync(string userId, int itemId, CancellationToken ct = default)
    {
        var up = await taste.ResolveAsync(userId, ct);
        var entry = await db.ListEntries
            .FirstOrDefaultAsync(l => l.Id == itemId && l.ProfileId == up.ProfileId, ct);
        if (entry is null) return false;
        db.ListEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Статистика списків юзера (FR-17): watched/liked/disliked, avgRating, topGenres.</summary>
    public async Task<ListStats> StatsAsync(string userId, CancellationToken ct = default)
    {
        var up = await taste.ResolveAsync(userId, ct);
        var entries = await db.ListEntries.AsNoTracking()
            .Include(l => l.Title)
            .Where(l => l.ProfileId == up.ProfileId)
            .ToListAsync(ct);

        var watched = entries.Count(l => l.Kind is ListKinds.Watched or ListKinds.Read);
        var liked = entries.Count(l => l.Kind == ListKinds.Liked);
        var disliked = entries.Count(l => l.Kind == ListKinds.Disliked);
        var ratings = entries
            .Where(l => l.Rating is not null && l.Kind is ListKinds.Watched or ListKinds.Read or ListKinds.Liked)
            .Select(l => l.Rating!.Value).ToList();
        var topGenres = entries
            .Where(l => l.Kind != ListKinds.Disliked)
            .SelectMany(l => l.Title.Genres)
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Take(5)
            .Select(g => new GenreCount(g.Key, g.Count()))
            .ToList();

        return new ListStats(
            watched, liked, disliked,
            ratings.Count == 0 ? null : Math.Round(ratings.Average(), 2),
            topGenres);
    }

    /// <summary>
    /// Перенести гостьові items у профіль юзера (FR-16): ідемпотентно — дублікати (kind,title)
    /// і невалідні записи пропускаються; після перенесення — RecomputeAsync.
    /// </summary>
    public async Task<(int Moved, int Skipped)> MigrateGuestAsync(
        string userId, IReadOnlyList<GuestListItem> items, CancellationToken ct = default)
    {
        var up = await taste.ResolveAsync(userId, ct);
        var titleIds = items.Select(i => i.TitleId).Distinct().ToList();
        var titles = await db.Titles.AsNoTracking()
            .Where(t => titleIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);
        var existing = (await db.ListEntries.AsNoTracking()
                .Where(l => l.ProfileId == up.ProfileId)
                .Select(l => new { l.Kind, l.TitleId })
                .ToListAsync(ct))
            .Select(e => (e.Kind, e.TitleId))
            .ToHashSet();

        int moved = 0, skipped = 0;
        var now = DateTime.UtcNow;
        foreach (var item in items)
        {
            var valid = ListKinds.IsValid(item.Kind)
                        && ListKinds.IsValidRating(item.Rating)
                        && titles.TryGetValue(item.TitleId, out var t);
            if (!valid || existing.Contains((item.Kind, item.TitleId)))
            {
                skipped++;
                continue;
            }

            var title = titles[item.TitleId];
            db.ListEntries.Add(new ListEntry
            {
                ProfileId = up.ProfileId, Kind = item.Kind, TitleId = title.Id, Rating = item.Rating,
                CreatedAt = now, WatchedAt = item.Kind is ListKinds.Watched or ListKinds.Read ? now : null,
            });
            db.InteractionLogs.Add(new InteractionLog
            {
                UserId = userId, TitleId = title.Id,
                Action = ListKinds.ActionForKind(item.Kind) ?? "open", Value = item.Rating, CreatedAt = now,
            });
            existing.Add((item.Kind, item.TitleId));
            moved++;
        }

        await db.SaveChangesAsync(ct);
        if (moved > 0)
            await taste.RecomputeAsync(userId, ct);
        return (moved, skipped);
    }

    private async Task<Core.Domain.Title?> FindTitleAsync(int? titleId, int? tmdbId, CancellationToken ct)
    {
        if (titleId is int id)
            return await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tmdbId is { } tid && tid != 0)
            return await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.TmdbId == tid, ct);
        return null;
    }

    private static ListActionResult Fail(string error, string message) => new(false, error, message);
}
