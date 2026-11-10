using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Синхронізація каталогу аніме/манги. PRIMARY — AniList GraphQL Page(perPage:50, sort=POPULARITY_DESC)
/// (Jikan EOL/недоступний з мережі — джоба dormant, env JIKAN_URL); опційний extra-шар — офіційний
/// MAL API v2 (env MAL_CLIENT_ID, порожній → SyncSkip: warn і шар пропускається). Ідемпотентний
/// upsert за mal_id/anilist_id; прогрес — sync_state (source=anilist|mal-official, kind=anime|manga|mal-season).
/// </summary>
public sealed class AniListCatalogService(
    MovieDiscoveryDbContext db,
    AniListClient anilist,
    MalOfficialClient mal,
    ILogger<AniListCatalogService> log)
{
    public const string AniListSource = AniListClient.Source; // "anilist"
    public const string MalSource = MalOfficialClient.Source; // "mal-official"

    public async Task<int> SyncAnimeAsync(int pages, CancellationToken ct = default) =>
        await SyncCatalogAsync(pages, "ANIME", "anime", AniListMapper.BuildCatalogAnime, ct);

    public async Task<int> SyncMangaAsync(int pages, CancellationToken ct = default) =>
        await SyncCatalogAsync(pages, "MANGA", "manga", AniListMapper.BuildCatalogManga, ct);

    private async Task<int> SyncCatalogAsync(
        int pages, string mediaType, string kind, Func<AniListMedia, Title> build, CancellationToken ct)
    {
        var pagesSafe = Math.Clamp(pages, 1, 20);
        var items = new List<AniListMedia>();
        for (var page = 1; page <= pagesSafe; page++)
        {
            var data = await anilist.CatalogPageAsync(page, mediaType, ct);
            if (data is null) break; // джерело впало — синкуємо, що встигли зібрати
            items.AddRange(data);
            if (data.Count == 0) break;
        }

        var count = await UpsertBatchAsync(items, m => build(m), ct);
        await TouchSyncStateAsync(AniListSource, kind, pagesSafe, count, ct);
        var malCount = await SyncMalTopLayerAsync(pagesSafe, kind, ct);
        log.LogInformation("Синк {Kind}-каталогу: AniList {Count} тайтлів + MAL official {MalCount} (pages={Pages})",
            kind, count, malCount, pagesSafe);
        return count + malCount;
    }

    /// <summary>Опційний extra-шар MAL official top (ranking=all). Без MAL_CLIENT_ID → SyncSkip → warn, шар пропускається.</summary>
    private async Task<int> SyncMalTopLayerAsync(int pages, string kind, CancellationToken ct)
    {
        try
        {
            mal.EnsureConfigured();
            var items = await (kind == "anime"
                ? mal.TopAnimeAsync(pages, ct: ct)
                : mal.TopMangaAsync(pages, ct: ct)) ?? [];
            var count = await UpsertBatchAsync(items, n => MalOfficialMapper.Build(n, kind), ct);
            await TouchSyncStateAsync(MalSource, kind, pages, count, ct);
            return count;
        }
        catch (SyncSkipException ex)
        {
            log.LogWarning("{Message}", ex.UserMessage);
            return 0;
        }
        catch (Exception ex)
        {
            // MAL-шар не валить весь синк: AniList-дані вже записано
            log.LogWarning(ex, "MAL official-шар не вдався ({Kind}) — пропускаю", kind);
            return 0;
        }
    }

    /// <summary>Сезонний шар MAL official (anime/season/{year}/{season}); без client id → SyncSkip угору (warn джоби).</summary>
    public async Task<int> SyncMalSeasonAsync(int year, string season, int pages, CancellationToken ct = default)
    {
        var pagesSafe = Math.Clamp(pages, 1, 10);
        var items = await mal.SeasonAsync(year, season, pagesSafe, ct) ?? [];
        var count = await UpsertBatchAsync(items, n => MalOfficialMapper.Build(n, "anime"), ct);
        await TouchSyncStateAsync(MalSource, "mal-season", pagesSafe, count, ct);
        log.LogInformation("Синк MAL season {Year}/{Season}: {Count} тайтлів (pages={Pages})",
            year, season, count, pagesSafe);
        return count;
    }

    /// <summary>Батч-upsert: один запит кандидатів за mal_id/anilist_id, потім Resolve+Merge кожного патчу.</summary>
    private async Task<int> UpsertBatchAsync<T>(List<T> items, Func<T, Title> build, CancellationToken ct)
    {
        if (items.Count == 0) return 0;

        var patches = new List<Title>(items.Count);
        foreach (var item in items)
        {
            var patch = build(item);
            if (string.IsNullOrWhiteSpace(patch.TitleEn)) continue; // без назви тайтл не створюємо
            if (patch.MalId is not > 0 && patch.AnilistId is not > 0) continue; // без зовнішнього id не апсертимо
            patches.Add(patch);
        }
        if (patches.Count == 0) return 0;

        var malIds = patches.Where(p => p.MalId is > 0).Select(p => p.MalId!.Value).Distinct().ToList();
        var anilistIds = patches.Where(p => p.AnilistId is > 0).Select(p => p.AnilistId!.Value).Distinct().ToList();
        var candidates = await db.Titles
            .Where(t =>
                (t.MalId != null && malIds.Contains(t.MalId.Value)) ||
                (t.AnilistId != null && anilistIds.Contains(t.AnilistId.Value)))
            .ToListAsync(ct);

        foreach (var patch in patches)
        {
            var (target, created) = AnimeUpsertCore.Resolve(candidates, patch);
            if (created)
            {
                db.Titles.Add(target);
                candidates.Add(target);
            }
            else
            {
                AnimeUpsertCore.Merge(target, patch);
            }
        }

        await db.SaveChangesAsync(ct);
        return patches.Count;
    }

    private async Task TouchSyncStateAsync(string source, string kind, int pages, int count, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == source && s.Kind == kind, ct);
        if (state is null)
        {
            state = new SyncState { Source = source, Kind = kind };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastPages = pages;
        state.LastCount = count;
        await db.SaveChangesAsync(ct);
    }
}
