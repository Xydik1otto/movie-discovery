using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>Результат прогону чистки фейкових постерів.</summary>
public sealed record FakePosterCleanupResult(int Cleaned, int Ytimg, int MissingOriginal);

/// <summary>
/// UX-02: ідемпотентна чистка ФЕЙКОВИХ постерів у titles.poster_path (адмін-виклик, не ручний UPDATE):
/// 1) i.ytimg.com — YouTube-thumbnail писав YouTubeEnrichService як «постер» (дефект видалено,
///    див. YouTubeEnrichService.UpsertLinkAsync); прев'ю відео не є постером тайтла;
/// 2) missing_original.jpg — службова заглушка shikimori (assets/globals/missing_original.jpg),
///    яку аніме/манга-синк записував як постер; це не постер, а «запис відсутній».
/// Обидва маркери шукаються підстроко- (LIKE '%…%': хвостові ?v=… query-параметри теж ловляться),
/// знайдені poster_path обнуляються → фронт показує чесну заглушку, а fill-only синк
/// wikipedia-posters (в силу наявності imdb_id) потім запише справжній постер.
/// Повторний прогін знаходить 0 (ідемпотентність) — метод безпечний для ретраїв адміна.
/// </summary>
public sealed class PosterFakeCleanupService(MovieDiscoveryDbContext db, ILogger<PosterFakeCleanupService> log)
{
    /// <summary>Підстрока YouTube-thumbnail у poster_path (i.ytimg.com/…/hqdefault.jpg).</summary>
    public const string YtimgMarker = "i.ytimg.com";

    /// <summary>Підстрока shikimori-заглушки (…/missing_original.jpg, можливі ?timestamp хвости).</summary>
    public const string MissingOriginalMarker = "missing_original.jpg";

    /// <summary>Обнулити фейкові poster_path. Повертає лічильники; SaveChanges лише коли є що чистити.</summary>
    public async Task<FakePosterCleanupResult> RunAsync(CancellationToken ct = default)
    {
        var fakes = await db.Titles
            .Where(t => t.PosterPath != null &&
                        (t.PosterPath.Contains(YtimgMarker) || t.PosterPath.Contains(MissingOriginalMarker)))
            .ToListAsync(ct);

        var ytimg = 0;
        var missing = 0;
        foreach (var title in fakes)
        {
            if (title.PosterPath!.Contains(YtimgMarker)) ytimg++;
            else missing++;
            title.PosterPath = null; // metadata-missing: постера немає — чесна NULL, не фейковий URL
        }

        var cleaned = fakes.Count;
        if (cleaned > 0)
            await db.SaveChangesAsync(ct);

        log.LogInformation(
            "Чистка фейкових постерів завершено: cleaned={Cleaned} (ytimg={Ytimg}, missing_original={MissingOriginal})",
            cleaned, ytimg, missing);
        return new FakePosterCleanupResult(cleaned, ytimg, missing);
    }
}
