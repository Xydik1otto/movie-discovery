using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Фоновий імпорт переглянутого/прочитаного з трекерів (FR-13) за публічним нікнеймом.
/// Для кожного джерела свій сценарій; статуси: completed → watched (аніме) / read (манга)
/// з оцінкою користувача → rating 1–10; watching/reading → watchlist; on_hold/dropped → скіп.
/// Матчинг за mal_id/anilist_id/shikimori_id → ідемпотентний upsert + list row.
/// stats оновлюється в БД кожні ~20 записів (живий прогрес для UI).
/// </summary>
public sealed class ImportService(
    MovieDiscoveryDbContext db,
    JikanClient jikan,
    ShikimoriClient shikimori,
    AniListClient anilist,
    ILogger<ImportService> log)
{
    private const int BatchSize = 20;

    public async Task<bool> RunAsync(int jobId, CancellationToken ct = default)
    {
        var job = await db.ImportJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status is "running" or "done") return false; // ідемпотентність перезапуску

        job.Status = "running";
        await db.SaveChangesAsync(ct);

        var stats = new ImportStats();
        try
        {
            var profile = await ResolveProfileAsync(job, ct)
                ?? throw new SourceAccessException(
                    job.ProfileId is { } pid
                        ? $"Профіль з id={pid} не знайдено — передайте існуючий profileId або залишіть порожнім (використається «Головний»)"
                        : "Профіль за замовчуванням не знайдено — створіть профіль");

            switch (job.Source)
            {
                case ImportSourceCatalog.Mal:
                    await RunMalAsync(job, profile.Id, stats, ct);
                    break;
                case ImportSourceCatalog.Anilist:
                    await RunAniListAsync(job, profile.Id, stats, ct);
                    break;
                case ImportSourceCatalog.Shikimori:
                    await RunShikimoriAsync(job, profile.Id, stats, ct);
                    break;
                default:
                    throw new SourceAccessException($"Невідоме джерело імпорту: «{job.Source}»");
            }

            job.Status = "done";
            job.Stats = stats.ToJsonDocument();
            job.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Імпорт #{Id} ({Source}/{User}) завершено: watched={W}, read={R}, watchlist={L}",
                job.Id, job.Source, job.Username, stats.AnimeWatched, stats.MangaRead, stats.WatchlistAdded);
            return true;
        }
        catch (SourceAccessException ex)
        {
            log.LogWarning("Імпорт #{Id} ({Source}/{User}) не вдався: {Message}", jobId, job.Source, job.Username, ex.UserMessage);
            await FailAsync(job, stats, ex.UserMessage, ct);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Імпорт #{Id} ({Source}/{User}) впав з неочікуваною помилкою", jobId, job.Source, job.Username);
            await FailAsync(job, stats, $"Помилка імпорту: {ex.Message}", ct);
            return true;
        }
    }

    // ─── Джерела ───

    private async Task RunMalAsync(ImportJob job, int profileId, ImportStats stats, CancellationToken ct)
    {
        var anime = await jikan.UserAnimeListAsync(job.Username, ct)
            ?? throw new SourceAccessException(
                $"Профіль MyAnimeList «{job.Username}» не знайдено або його список приватний");
        var manga = await jikan.UserMangaListAsync(job.Username, ct)
            ?? throw new SourceAccessException(
                $"Список манги MyAnimeList «{job.Username}» не знайдено або приватний");

        await ProcessJikanEntriesAsync(job, profileId, stats, anime, isManga: false, ct);
        await ProcessJikanEntriesAsync(job, profileId, stats, manga, isManga: true, ct);
    }

    private async Task RunShikimoriAsync(ImportJob job, int profileId, ImportStats stats, CancellationToken ct)
    {
        var user = await shikimori.UserResolveAsync(job.Username, ct)
            ?? throw new SourceAccessException(
                $"Профіль Shikimori «{job.Username}» не знайдено або приватний");

        // всі rates без status-фільтра: розбір по статусу на місці (watching → watchlist тощо)
        var anime = await FetchAllRatesAsync((page, c) => shikimori.AnimeRatesAsync(user.Id, page, null, c), ct);
        var manga = await FetchAllRatesAsync((page, c) => shikimori.MangaRatesAsync(user.Id, page, null, c), ct);

        await ProcessShikimoriRatesAsync(job, profileId, stats, anime, isManga: false, ct);
        await ProcessShikimoriRatesAsync(job, profileId, stats, manga, isManga: true, ct);
    }

    private async Task RunAniListAsync(ImportJob job, int profileId, ImportStats stats, CancellationToken ct)
    {
        var userId = await anilist.UserIdAsync(job.Username, ct)
            ?? throw new SourceAccessException(
                $"Профіль AniList «{job.Username}» не знайдено або приватний");

        var animeLists = await anilist.MediaListCollectionAsync(userId, "ANIME", ct);
        var mangaLists = await anilist.MediaListCollectionAsync(userId, "MANGA", ct);

        // списки (включно з кастомними) можуть дублювати entry за media.id — дедуп на місці
        await ProcessAniListEntriesAsync(job, profileId, stats,
            AniListFlatten.Flatten(animeLists), isManga: false, ct);
        await ProcessAniListEntriesAsync(job, profileId, stats,
            AniListFlatten.Flatten(mangaLists), isManga: true, ct);
    }

    // ─── Обробка записів (батчами по 20, з проміжним збереженням stats) ───

    private async Task ProcessJikanEntriesAsync(
        ImportJob job, int profileId, ImportStats stats,
        List<JikanUserEntry> entries, bool isManga, CancellationToken ct)
    {
        foreach (var batch in entries.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var planned = new List<PlannedItem>();
            foreach (var entry in batch)
            {
                if (isManga) stats.MangaTotal++; else stats.AnimeTotal++;
                var media = isManga ? entry.Manga : entry.Anime;
                var kind = ListKinds.KindForStatus(entry.Status, isManga);
                if (media is null || kind is null)
                {
                    stats.Skipped++;
                    if (media is null)
                        stats.Errors.Add($"{(isManga ? "manga" : "anime")} #{media?.MalId ?? 0}: запис списку без даних тайтла");
                    continue;
                }
                var patch = isManga ? JikanMangaMapper.Build(media) : JikanAnimeMapper.Build(media);
                planned.Add(new PlannedItem(kind, ListKinds.RatingFromScore(entry.Score), patch));
            }
            await ApplyBatchAsync(job, profileId, stats, planned, ct);
        }
    }

    private async Task ProcessShikimoriRatesAsync(
        ImportJob job, int profileId, ImportStats stats,
        List<ShikimoriRate> rates, bool isManga, CancellationToken ct)
    {
        foreach (var batch in rates.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var planned = new List<PlannedItem>();
            foreach (var rate in batch)
            {
                if (isManga) stats.MangaTotal++; else stats.AnimeTotal++;
                var kind = ListKinds.KindForStatus(rate.Status, isManga);
                var media = isManga ? rate.Manga : rate.Anime;
                if (kind is null || media is null)
                {
                    stats.Skipped++;
                    if (media is null) stats.Errors.Add($"shikimori #{rate.TargetId}: rate без даних тайтла");
                    continue;
                }
                var patch = isManga ? ShikimoriMapper.BuildManga(rate) : ShikimoriMapper.BuildAnime(rate);
                planned.Add(new PlannedItem(kind, ListKinds.RatingFromScore(rate.Score), patch));
            }
            await ApplyBatchAsync(job, profileId, stats, planned, ct);
        }
    }

    private async Task ProcessAniListEntriesAsync(
        ImportJob job, int profileId, ImportStats stats,
        List<AniListEntry> entries, bool isManga, CancellationToken ct)
    {
        foreach (var batch in entries.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var planned = new List<PlannedItem>();
            foreach (var entry in batch)
            {
                if (isManga) stats.MangaTotal++; else stats.AnimeTotal++;
                var kind = ListKinds.KindForStatus(entry.Status, isManga);
                if (kind is null || entry.Media is null)
                {
                    stats.Skipped++;
                    if (entry.Media is null) stats.Errors.Add($"anilist: entry без media (list status={entry.Status})");
                    continue;
                }
                var patch = isManga ? AniListMapper.BuildManga(entry) : AniListMapper.BuildAnime(entry);
                planned.Add(new PlannedItem(kind, ListKinds.RatingFromScore(entry.Score), patch));
            }
            await ApplyBatchAsync(job, profileId, stats, planned, ct);
        }
    }

    /// <summary>Upсерт тайтлів батчу + ідемпотентні list-рядки + проміжний прогрес.</summary>
    private async Task ApplyBatchAsync(
        ImportJob job, int profileId, ImportStats stats, List<PlannedItem> planned, CancellationToken ct)
    {
        if (planned.Count > 0)
        {
            var malIds = planned.Select(p => p.Patch.MalId).OfType<int>().Distinct().ToList();
            var anilistIds = planned.Select(p => p.Patch.AnilistId).OfType<int>().Distinct().ToList();
            var shikimoriIds = planned.Select(p => p.Patch.ShikimoriId).OfType<int>().Distinct().ToList();

            var candidates = await db.Titles
                .Where(t =>
                    (t.MalId != null && malIds.Contains(t.MalId.Value)) ||
                    (t.AnilistId != null && anilistIds.Contains(t.AnilistId.Value)) ||
                    (t.ShikimoriId != null && shikimoriIds.Contains(t.ShikimoriId.Value)))
                .ToListAsync(ct);

            var rows = new List<ListEntry>();
            foreach (var item in planned)
            {
                var patch = item.Patch;
                if (string.IsNullOrWhiteSpace(patch.TitleEn))
                {
                    stats.Skipped++;
                    stats.Errors.Add("запис без назви — пропущено");
                    continue;
                }

                var (target, created) = AnimeUpsertCore.Resolve(candidates, patch);
                if (created)
                {
                    db.Titles.Add(target);
                    candidates.Add(target);
                    stats.Created++;
                }
                else
                {
                    AnimeUpsertCore.Merge(target, patch);
                    stats.Matched++;
                }

                rows.Add(new ListEntry
                {
                    ProfileId = profileId,
                    Kind = item.Kind,
                    Title = target,
                    Rating = item.Rating,
                    CreatedAt = DateTime.UtcNow,
                    WatchedAt = item.Kind is ListKinds.Watched or ListKinds.Read ? DateTime.UtcNow : null,
                });

                switch (item.Kind)
                {
                    case ListKinds.Watched: stats.AnimeWatched++; break;
                    case ListKinds.Read: stats.MangaRead++; break;
                    default: stats.WatchlistAdded++; break;
                }
            }

            // ідемпотентність списків: UNIQUE(profile, kind, title) → існуючий рядок оновити rating, не дублювати
            var existingIds = rows.Where(r => r.Title.Id != 0).Select(r => r.Title.Id).Distinct().ToList();
            var existingRows = existingIds.Count == 0
                ? []
                : await db.ListEntries
                    .Where(l => l.ProfileId == profileId && existingIds.Contains(l.TitleId))
                    .ToListAsync(ct);

            var fresh = new List<ListEntry>();
            foreach (var row in rows)
            {
                var existing = row.Title.Id != 0
                    ? existingRows.FirstOrDefault(l => l.TitleId == row.Title.Id && l.Kind == row.Kind)
                    : null;
                if (existing is not null)
                {
                    if (row.Rating is { } r && existing.Rating != r)
                        existing.Rating = r; // оновити оцінку, якщо прийшла з імпорту
                    continue;
                }
                fresh.Add(row);
            }
            if (fresh.Count > 0) db.ListEntries.AddRange(fresh);
        }

        await FlushAsync(job, stats, ct); // прогрес у БД кожні ~20 записів
    }

    private async Task FlushAsync(ImportJob job, ImportStats stats, CancellationToken ct)
    {
        job.Stats = stats.ToJsonDocument();
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<ShikimoriRate>> FetchAllRatesAsync(
        Func<int, CancellationToken, Task<List<ShikimoriRate>?>> fetchPage, CancellationToken ct)
    {
        var all = new List<ShikimoriRate>();
        for (var page = 1; page <= 40; page++) // 500/стор., страховий ліміт сторінок
        {
            ct.ThrowIfCancellationRequested();
            var rates = await fetchPage(page, ct);
            if (rates is null) break;
            all.AddRange(rates);
            if (rates.Count < 500) break;
        }
        return all;
    }

    private async Task<Profile?> ResolveProfileAsync(ImportJob job, CancellationToken ct) =>
        job.ProfileId is { } pid
            ? await db.Profiles.FirstOrDefaultAsync(p => p.Id == pid, ct)
            : await db.Profiles
                .OrderByDescending(p => p.IsDefault)
                .ThenBy(p => p.Id)
                .FirstOrDefaultAsync(ct);

    private async Task FailAsync(ImportJob job, ImportStats stats, string message, CancellationToken ct)
    {
        job.Status = "failed";
        job.Error = message;
        job.Stats = stats.ToJsonDocument();
        job.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>План одного імпорт-запису: kind списку, оцінка, патч тайтла.</summary>
    private sealed record PlannedItem(string Kind, int? Rating, Title Patch);
}

/// <summary>Розгортка AniList-списків у плоский список entries з дедупом за media.id (кастомні списки дублюють).</summary>
public static class AniListFlatten
{
    public static List<AniListEntry> Flatten(AniListCollection? collection)
    {
        if (collection is null) return [];
        var seen = new HashSet<int>();
        var result = new List<AniListEntry>();
        foreach (var list in collection.Lists)
        {
            foreach (var entry in list.Entries)
            {
                if (entry.Media is null || !seen.Add(entry.Media.Id)) continue;
                result.Add(entry);
            }
        }
        return result;
    }
}
