using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>Збережені добірки юзера (FR-21). Ізоляція як у TasteProfileService: усі запити
/// жорстко фільтруються за userId власника — ownerId НІКОЛИ не береться з тіла запиту.</summary>
public sealed class CollectionsService(MovieDiscoveryDbContext db)
{
    public const int MaxItems = 30;
    public const int MaxNameLength = 120;
    /// <summary>Розмір серверного пресета (FR-21): 12 тайтлів.</summary>
    public const int PresetSize = 12;

    /// <summary>Створити добірку. Created=null → помилка (Error: invalid_name | too_many_items | unknown_titles).</summary>
    public async Task<(UserCollection? Created, string? Error, IReadOnlyList<long>? UnknownTitleIds)> CreateAsync(
        string userId, string? name, IReadOnlyList<long> titleIds, CancellationToken ct = default)
    {
        var cleanName = (name ?? "").Trim();
        if (cleanName.Length == 0 || cleanName.Length > MaxNameLength)
            return (null, "invalid_name", null);

        // дублікати зливаємо з preservation порядку; ліміт — на унікальні тайтли
        var seen = new HashSet<long>();
        var items = new List<long>();
        foreach (var id in titleIds)
            if (seen.Add(id))
                items.Add(id);
        if (items.Count > MaxItems)
            return (null, "too_many_items", null);

        var idsInt = items.Select(i => (int)i).ToList();
        var existing = idsInt.Count == 0
            ? new HashSet<int>()
            : (await db.Titles.AsNoTracking()
                .Where(t => idsInt.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct)).ToHashSet();
        var unknown = items.Where(i => !existing.Contains((int)i)).ToList();
        if (unknown.Count > 0)
            return (null, "unknown_titles", unknown);

        var collection = new UserCollection
        {
            UserId = userId,
            Name = cleanName,
            CreatedAt = DateTime.UtcNow,
        };
        for (var i = 0; i < items.Count; i++)
            collection.Items.Add(new UserCollectionItem { TitleId = items[i], Ord = i });
        db.UserCollections.Add(collection);
        await db.SaveChangesAsync(ct);
        return (collection, null, null);
    }

    /// <summary>Добірки ЮЗЕРА (ізоляція: чужих тут не буває за побудовою запиту), items у порядку ord.</summary>
    public async Task<List<UserCollection>> GetOwnAsync(string userId, CancellationToken ct = default)
    {
        var cols = await db.UserCollections.AsNoTracking()
            .Include(c => c.Items)
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(ct);
        foreach (var c in cols)
            c.Items.Sort((a, b) => a.Ord.CompareTo(b.Ord));
        return cols;
    }

    /// <summary>Видалити СВОЮ добірку. false → 404 (чужа/неіснуюча не розрізняються — без витоку існування).</summary>
    public async Task<bool> DeleteAsync(string userId, int id, CancellationToken ct = default)
    {
        var own = await db.UserCollections
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (own is null) return false;
        db.UserCollections.Remove(own);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Серверні пресети (FR-21): mood / evening / duration. Ключі НЕ локалізуються — фронт локалізує.
    /// Пріоритет: тайтли з доступними watch_links, потім з uk-назвою, далі рейтинг і популярність.</summary>
    public async Task<List<CollectionPreset>> GetPresetsAsync(CancellationToken ct = default)
    {
        var mood = await PresetAsync(genres: ["comedy", "romance", "music", "musical"], minRating: 7.0, shortRuntime: false, ct);
        var evening = await PresetAsync(genres: ["thriller", "horror", "mystery", "film-noir"], minRating: null, shortRuntime: false, ct);
        // runtime-колонка є (titles.runtime_min / episode_runtime_min) → duration = короткі повнометражні (<95 хв)
        var duration = await PresetAsync(genres: [], minRating: null, shortRuntime: true, ct);

        return
        [
            new CollectionPreset("preset.mood", mood),
            new CollectionPreset("preset.evening", evening),
            new CollectionPreset("preset.duration", duration),
        ];
    }

    private async Task<List<Title>> PresetAsync(
        string[] genres, double? minRating, bool shortRuntime, CancellationToken ct)
    {
        // InMemory-провайдер (тести) не транслює Any-по-масивах/EXISTS-сортування — materialize + C#-пріоритети.
        // Npgsql-шлях — BuildPresetQuery (індексний план; тест SQL-трансляції пінить його).
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var rows = await db.Titles.AsNoTracking().ToListAsync(ct);
            var watchIds = (await db.WatchLinks.AsNoTracking()
                    .Where(w => w.IsAvailable)
                    .Select(w => w.TitleId)
                    .ToListAsync(ct))
                .ToHashSet();
            var genreSet = genres.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return rows
                .Where(t => (genreSet.Count == 0 || t.Genres.Any(g => genreSet.Contains(g)))
                            && (!minRating.HasValue || (t.RatingTmdb ?? t.RatingImdb) >= minRating)
                            && (!shortRuntime || t.Type == "movie" && t.RuntimeMin > 0 && t.RuntimeMin < 95))
                .OrderByDescending(t => watchIds.Contains(t.Id))
                .ThenByDescending(t => t.TitleUk != null)
                .ThenByDescending(t => t.RatingTmdb ?? t.RatingImdb)
                .ThenByDescending(t => t.Popularity)
                .Take(PresetSize)
                .ToList();
        }

        var q = BuildPresetQuery(db.Titles.AsNoTracking(), db.WatchLinks, genres, minRating, shortRuntime);
        return await q.Take(PresetSize).ToListAsync(ct);
    }

    /// <summary>
    /// Запит пресета (FR-21): жанри (GIN-friendly overlap) → опційний поріг рейтингу/короткий метраж;
    /// порядок: доступні watch_links (EXISTS) → uk-назва → рейтинг → популярність.
    /// Public static — тест SQL-трансляції (ToQueryString, без БД) перевіряє план.
    /// </summary>
    public static IQueryable<Title> BuildPresetQuery(
        IQueryable<Title> titles, IQueryable<WatchLink> watchLinks,
        string[] genres, double? minRating, bool shortRuntime)
    {
        // жанри в БД — нормалізований lowercase (усі маппери), тому пряме порівняння (SearchService-конвенція)
        var genreArr = genres.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var q = titles;
        if (genreArr.Length > 0)
            q = q.Where(t => t.Genres.Any(g => genreArr.Contains(g)));
        if (minRating is double mr)
            q = q.Where(t => (t.RatingTmdb ?? t.RatingImdb) >= mr);
        if (shortRuntime)
            q = q.Where(t => t.Type == "movie" && t.RuntimeMin > 0 && t.RuntimeMin < 95);

        return q
            .OrderByDescending(t => watchLinks.Any(w => w.TitleId == t.Id && w.IsAvailable))
            .ThenByDescending(t => t.TitleUk != null)
            .ThenByDescending(t => t.RatingTmdb ?? t.RatingImdb)
            .ThenByDescending(t => t.Popularity);
    }
}

/// <summary>Пресет добірки: ключ локалізації (preset.mood/preset.evening/preset.duration) + тайтли.</summary>
public sealed record CollectionPreset(string Key, List<Title> Items);
