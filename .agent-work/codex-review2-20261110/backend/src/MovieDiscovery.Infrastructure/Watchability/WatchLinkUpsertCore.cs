using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>
/// Ідемпотентне ядро upsert watch_links: ключ (title, provider, kind, region, мова-сигнатура
/// audio_langs) — UX-04: uk/ru-варіанти того самого провайдера НЕ зливаються в один рядок
/// (UNIQUE(title_id, provider, kind, region, COALESCE(audio_langs,''))).
/// УЗГОДЖЕННЯ (Codex IV-04): нормалізація сигнатури ОДНА для всіх трьох шарів —
/// upsert-ключ тут, dedup-ключ WatchOptionsMerger і міграційний UNIQUE-індекс — це
/// WatchLanguages.CanonicalKey (ISO-639-1, lowercase, sorted csv; NULL → «»).
/// Чисті функції (без I/O) — покриті тестами: повторний sync НЕ дублює і НЕ губить мови,
/// uk+ru одночасно — окремі рядки, старі NULL-рядки не дублюються.
/// </summary>
public static class WatchLinkUpsertCore
{
    /// <summary>Знайти наявне посилання за ключем (title, provider, kind, region, мова-сигнатура);
    /// нема → сам патч (створення). Сигнатура нормалізується CanonicalKey (sorted csv, NULL → «»)
    /// — збігається з міграційним UNIQUE-індексом COALESCE(audio_langs,'').</summary>
    public static (WatchLink Link, bool Created) Resolve(IEnumerable<WatchLink> existing, WatchLink patch)
    {
        var patchSig = WatchLanguages.CanonicalKey(patch.AudioLangs);
        var found = existing.FirstOrDefault(l =>
            l.TitleId == patch.TitleId &&
            string.Equals(l.Provider, patch.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Kind, patch.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(l.Region, patch.Region, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(WatchLanguages.CanonicalKey(l.AudioLangs), patchSig, StringComparison.Ordinal));
        return found is null ? (patch, true) : (found, false);
    }

    /// <summary>Оновити наявне посилання: url/is_available — свіжий стан перевірки, checked_at = зараз;
    /// озвучка/якість (FR-19) — лише коли патч їх має (NULL у патчі не затирає наявне).
    /// UX-04: набори мов (audio_langs/sub_langs csv) — той самий принцип: патч із відомим
    /// набором ЗАМІНЮЄ (свіжа правда джерела), NULL-патч лишає наявне.</summary>
    public static void Merge(WatchLink target, WatchLink patch)
    {
        if (!string.IsNullOrWhiteSpace(patch.Url)) target.Url = patch.Url;
        target.IsAvailable = patch.IsAvailable;
        target.CheckedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(patch.AudioLang)) target.AudioLang = patch.AudioLang;
        if (patch.Quality is { } q) target.Quality = q;
        if (!string.IsNullOrWhiteSpace(patch.Dub)) target.Dub = patch.Dub;
        if (!string.IsNullOrWhiteSpace(patch.AudioLangs)) target.AudioLangs = patch.AudioLangs.Trim();
        if (!string.IsNullOrWhiteSpace(patch.SubLangs)) target.SubLangs = patch.SubLangs.Trim();
    }

    /// <summary>Ключ посилання для діагностики/логів (UX-04: з мовною сигнатурою — та сама
    /// нормалізація CanonicalKey, що в upsert-ключа й міграційному індексі).</summary>
    public static string Key(WatchLink l) =>
        $"{l.TitleId}|{l.Provider}|{l.Kind}|{l.Region}|{WatchLanguages.CanonicalKey(l.AudioLangs)}";
}
