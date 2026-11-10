using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.YouTube;

/// <summary>
/// Чиста логіка рішення «чи записати відео як офіційний трейлер тайтла» (FR-20+).
/// Винесена з YouTubeEnrichService в окремий статичний клас — для прямого unit-тестування.
/// </summary>
public static class YoutubeEnrichLogic
{
    /// <summary>
    /// Чи заповнювати titles.trailer_yt_id цим відео:
    /// 1) поточний id ще порожній (наявний НЕ перезаписуємо — перший офіційний перемагає);
    /// 2) відео взагалі трейлер-подібне (YoutubeFilters.IsTrailer);
    /// 3) «official» + «trailer» у назві/описі (офіційний трейлер, а не довільна згадка слова);
    /// 4) назва відео збігається з тайтлом (YoutubeMatcher: токени назви + рік ±1).
    /// </summary>
    public static bool TreatAsTrailer(Title title, string videoTitle, string videoDescription, string? currentTrailerId)
    {
        if (!string.IsNullOrWhiteSpace(currentTrailerId)) return false;
        if (!YoutubeFilters.IsTrailer(videoTitle, videoDescription)) return false;

        var text = $"{videoTitle} {videoDescription}";
        if (!text.Contains("official", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("trailer", StringComparison.OrdinalIgnoreCase))
            return false;

        return YoutubeMatcher.Matches(title, videoTitle, videoDescription);
    }
}
