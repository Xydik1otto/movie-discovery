using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Ідемпотентне ядро upsert аніме/манги за зовнішніми id: mal_id → anilist_id → shikimori_id.
/// Чисті функції (без I/O) — покриваються тестами; AnimeTitleUpsert — тонка EF-обгортка.
/// </summary>
public static class AnimeUpsertCore
{
    /// <summary>Знайти наявний тайтл серед кандидатів за зовнішніми id; не знайдено → сам патч (створення).</summary>
    public static (Title Title, bool Created) Resolve(IReadOnlyList<Title> candidates, Title patch)
    {
        var existing =
            ById(candidates, patch.MalId, t => t.MalId) ??
            ById(candidates, patch.AnilistId, t => t.AnilistId) ??
            ById(candidates, patch.ShikimoriId, t => t.ShikimoriId);
        return existing is null ? (patch, true) : (existing, false);
    }

    private static Title? ById(IReadOnlyList<Title> candidates, int? id, Func<Title, int?> selector) =>
        id is null ? null : candidates.FirstOrDefault(t => selector(t) == id);

    /// <summary>
    /// Дедуп list-рядків У МЕЖАХ одного батчу по (kind, тайтл). Різні зовнішні id
    /// (mal/anilist/shikimori) можуть зійтися на одному тайтлі — без дедупу два рядки
    /// з однаковим (profile, kind, title) падають на SaveChanges з 23505 (UNIQUE
    /// ix_lists_profile_id_kind_title_id). Title-об'єкт спільний (ReferenceEquals),
    /// тому хеш по посиланню коректний і для ще-не-збережених (Id=0) записів;
    /// оцінка пізнішого дубля переноситься на запланований рядок.
    /// </summary>
    public static List<ListEntry> DedupeBatchRows(List<ListEntry> rows)
    {
        var deduped = new List<ListEntry>(rows.Count);
        var seen = new HashSet<(string Kind, Title Title)>();
        foreach (var row in rows)
        {
            if (!seen.Add((row.Kind, row.Title)))
            {
                var kept = deduped.Single(r => r.Kind == row.Kind && ReferenceEquals(r.Title, row.Title));
                if (row.Rating is { } r) kept.Rating = r;
                continue;
            }
            deduped.Add(row);
        }
        return deduped;
    }

    /// <summary>Оновити наявний тайтл патчем: score/episodes/год оновлюємо, title_uk null'ом НЕ затираємо,
    /// масиви замінюємо тільки непорожніми патчами (повторний upsert → той самий стан, без дублів).
    /// </summary>
    public static void Merge(Title target, Title patch)
    {
        if (!string.IsNullOrWhiteSpace(patch.TitleEn)) target.TitleEn = patch.TitleEn;
        if (!string.IsNullOrWhiteSpace(patch.TitleUk)) target.TitleUk = patch.TitleUk;
        // title_ru — fill-only (політика власника 10.11): Wikidata-синк або раніший імпорт
        // вже можуть мати ru-назву → непорожню НЕ перезаписуємо (на відміну від en/uk)
        if (string.IsNullOrWhiteSpace(target.TitleRu) && !string.IsNullOrWhiteSpace(patch.TitleRu))
            target.TitleRu = patch.TitleRu;
        if (!string.IsNullOrWhiteSpace(patch.OverviewEn)) target.OverviewEn = patch.OverviewEn;
        if (!string.IsNullOrWhiteSpace(patch.OverviewUk)) target.OverviewUk = patch.OverviewUk;
        if (patch.Year is > 0) target.Year = patch.Year;
        if (!string.IsNullOrWhiteSpace(patch.Format)) target.Format = patch.Format;
        if (patch.Episodes is > 0) target.Episodes = patch.Episodes;
        if (patch.Chapters is > 0) target.Chapters = patch.Chapters;
        if (patch.Volumes is > 0) target.Volumes = patch.Volumes;
        if (patch.Genres is { Length: > 0 }) target.Genres = patch.Genres;
        if (patch.Studios is { Length: > 0 }) target.Studios = patch.Studios;
        if (patch.Authors is { Length: > 0 }) target.Authors = patch.Authors;
        if (patch.ScoreMal is > 0) target.ScoreMal = patch.ScoreMal;
        if (patch.ScoreAnilist is > 0) target.ScoreAnilist = patch.ScoreAnilist;
        if (patch.ScoreShikimori is > 0) target.ScoreShikimori = patch.ScoreShikimori;
        if (patch.RatingTmdb is > 0) target.RatingTmdb = patch.RatingTmdb; // MAL official: rating_tmdb = mean
        if (patch.Popularity > target.Popularity) target.Popularity = patch.Popularity;
        if (!string.IsNullOrWhiteSpace(patch.PosterPath)) target.PosterPath = patch.PosterPath;
        if (!string.IsNullOrWhiteSpace(patch.Status)) target.Status = patch.Status;

        // зовнішні id дозаповнюємо (наприклад, тайтл створено з AniList — потім прийшов MAL-патч)
        if (target.MalId is null) target.MalId = patch.MalId;
        if (target.AnilistId is null) target.AnilistId = patch.AnilistId;
        if (target.ShikimoriId is null) target.ShikimoriId = patch.ShikimoriId;

        target.UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>EF-обгортка ідемпотентного upsert одного тайтла (імпорт: по одному патчу на запис).</summary>
public sealed class AnimeTitleUpsert(MovieDiscoveryDbContext db)
{
    public async Task<(Title Title, bool Created)> UpsertAsync(Title patch, CancellationToken ct = default)
    {
        var ids = new[] { patch.MalId, patch.AnilistId, patch.ShikimoriId }
            .OfType<int>().Distinct().ToArray();

        var candidates = ids.Length == 0
            ? []
            : await db.Titles
                .Where(t =>
                    (t.MalId != null && ids.Contains(t.MalId.Value)) ||
                    (t.AnilistId != null && ids.Contains(t.AnilistId.Value)) ||
                    (t.ShikimoriId != null && ids.Contains(t.ShikimoriId.Value)))
                .ToListAsync(ct);

        var (target, created) = AnimeUpsertCore.Resolve(candidates, patch);
        if (created) db.Titles.Add(target);
        else AnimeUpsertCore.Merge(target, patch);
        await db.SaveChangesAsync(ct);
        return (target, created);
    }
}
