using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Синхронізація аніме/манга-каталогу з Jikan: top/anime?filter=bypopularity + seasons/now (аніме),
/// top/manga (манга). Ідемпотентний upsert за mal_id; прогрес — у sync_state (source=jikan).
/// </summary>
public sealed class AnimeCatalogService(
    MovieDiscoveryDbContext db,
    JikanClient jikan,
    ILogger<AnimeCatalogService> log)
{
    public const string JikanSource = "jikan";

    public async Task<int> SyncAnimeAsync(int pages, CancellationToken ct = default)
    {
        var pagesSafe = Math.Clamp(pages, 1, 10);
        var items = new List<JikanMedia>();

        var top = await jikan.TopAnimeAsync(pagesSafe, ct);
        if (top is not null) items.AddRange(top);
        var season = await jikan.CurrentSeasonAsync(pagesSafe, ct);
        if (season is not null) items.AddRange(season);

        var count = await UpsertBatchAsync(items, JikanAnimeMapper.Build, ct);
        await TouchSyncStateAsync("anime", pagesSafe, count, ct);
        log.LogInformation("Синк аніме-каталогу: {Count} тайтлів (pages={Pages})", count, pagesSafe);
        return count;
    }

    public async Task<int> SyncMangaAsync(int pages, CancellationToken ct = default)
    {
        var pagesSafe = Math.Clamp(pages, 1, 10);
        var top = await jikan.TopMangaAsync(pagesSafe, ct);
        var items = top ?? [];
        var count = await UpsertBatchAsync(items, JikanMangaMapper.Build, ct);
        await TouchSyncStateAsync("manga", pagesSafe, count, ct);
        log.LogInformation("Синк манга-каталогу: {Count} тайтлів (pages={Pages})", count, pagesSafe);
        return count;
    }

    /// <summary>Батч-upsert: один запит кандидатів по всіх зовнішніх id, потім Resolve+Merge кожного патчу.</summary>
    private async Task<int> UpsertBatchAsync(List<JikanMedia> items, Func<JikanMedia, Title> build, CancellationToken ct)
    {
        if (items.Count == 0) return 0;

        var patches = new List<Title>(items.Count);
        foreach (var m in items)
        {
            if (m.MalId <= 0) continue;
            var patch = build(m);
            if (string.IsNullOrWhiteSpace(patch.TitleEn)) continue; // без назви тайтл не створюємо
            patches.Add(patch);
        }
        if (patches.Count == 0) return 0;

        var malIds = patches.Where(p => p.MalId is not null).Select(p => p.MalId!.Value).Distinct().ToList();
        var candidates = malIds.Count == 0
            ? []
            : await db.Titles
                .Where(t => t.MalId != null && malIds.Contains(t.MalId.Value))
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

    private async Task TouchSyncStateAsync(string kind, int pages, int count, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == JikanSource && s.Kind == kind, ct);
        if (state is null)
        {
            state = new SyncState { Source = JikanSource, Kind = kind };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastPages = pages;
        state.LastCount = count;
        await db.SaveChangesAsync(ct);
    }
}
