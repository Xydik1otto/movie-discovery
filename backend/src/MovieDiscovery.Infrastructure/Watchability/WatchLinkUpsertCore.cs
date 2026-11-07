using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>
/// Ідемпотентне ядро upsert watch_links (FR-14): UNIQUE(title_id, provider, kind, region).
/// Чисті функції (без I/O) — покриті тестами: повторний sync НЕ дублює, оновлює url/is_available/checked_at.
/// </summary>
public static class WatchLinkUpsertCore
{
    /// <summary>Знайти наявне посилання за ключем (title, provider, kind, region); нема → сам патч (створення).</summary>
    public static (WatchLink Link, bool Created) Resolve(IEnumerable<WatchLink> existing, WatchLink patch)
    {
        var found = existing.FirstOrDefault(l =>
            l.TitleId == patch.TitleId &&
            string.Equals(l.Provider, patch.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Kind, patch.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Region, patch.Region, StringComparison.OrdinalIgnoreCase));
        return found is null ? (patch, true) : (found, false);
    }

    /// <summary>Оновити наявне посилання: url/is_available — свіжий стан перевірки, checked_at = зараз;
    /// озвучка/якість (FR-19) — лише коли патч їх має (NULL у патчі не затирає наявне).</summary>
    public static void Merge(WatchLink target, WatchLink patch)
    {
        if (!string.IsNullOrWhiteSpace(patch.Url)) target.Url = patch.Url;
        target.IsAvailable = patch.IsAvailable;
        target.CheckedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(patch.AudioLang)) target.AudioLang = patch.AudioLang;
        if (patch.Quality is { } q) target.Quality = q;
        if (!string.IsNullOrWhiteSpace(patch.Dub)) target.Dub = patch.Dub;
    }

    /// <summary>Ключ посилання для діагностики/логів.</summary>
    public static string Key(WatchLink l) => $"{l.TitleId}|{l.Provider}|{l.Kind}|{l.Region}";
}
