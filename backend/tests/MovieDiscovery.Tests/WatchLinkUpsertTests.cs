using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

/// <summary>UNIQUE(title_id, provider, kind, region): повторний sync не дублює, оновлює.</summary>
public class WatchLinkUpsertTests
{
    private static WatchLink Link(int titleId = 1, string provider = "archive", string kind = "free", string region = "*", string? url = null) =>
        new() { TitleId = titleId, Provider = provider, Kind = kind, Region = region, Url = url ?? "https://x", IsAvailable = true };

    [Fact]
    public void Resolve_Creates_When_Key_Missing()
    {
        var (link, created) = WatchLinkUpsertCore.Resolve([], Link());
        Assert.True(created);
        Assert.Equal("archive", link.Provider);
        Assert.Equal("free", link.Kind);
        Assert.Equal("*", link.Region);
    }

    [Fact]
    public void Second_Sync_Updates_Same_Row_Not_Duplicate()
    {
        var existing = Link(url: "https://old", region: "UA");
        existing.Id = 77;
        existing.CheckedAt = DateTime.UtcNow.AddDays(-1);

        var patch = Link(url: "https://new", region: "UA");
        var (resolved, created) = WatchLinkUpsertCore.Resolve([existing], patch);

        Assert.False(created);
        Assert.Same(existing, resolved);
        WatchLinkUpsertCore.Merge(resolved, patch);
        Assert.Equal(77, resolved.Id);              // той самий рядок
        Assert.Equal("https://new", resolved.Url);  // url оновлено
        Assert.True(resolved.CheckedAt >= DateTime.UtcNow.AddSeconds(-5)); // checked_at свіжий
    }

    [Fact]
    public void Different_Kind_Is_Separate_Link()
    {
        var free = Link(kind: "free", titleId: 1);
        var sub = Link(kind: "subscription", titleId: 1);

        var (_, created) = WatchLinkUpsertCore.Resolve([free], sub);
        Assert.True(created); // free і subscription — різні рядки
    }

    [Fact]
    public void Different_Provider_Is_Separate_Link()
    {
        var archive = Link(provider: "archive", titleId: 1);
        var sweettv = Link(provider: "sweettv", kind: "subscription", region: "UA", titleId: 1);

        var (_, created) = WatchLinkUpsertCore.Resolve([archive], sweettv);
        Assert.True(created);
    }

    [Fact]
    public void Merge_Marks_Unavailable_On_Recheck()
    {
        var target = Link();
        target.IsAvailable = true;

        var gone = Link();
        gone.IsAvailable = false;
        WatchLinkUpsertCore.Merge(target, gone);

        Assert.False(target.IsAvailable); // останній стан перевірки
    }

    [Fact]
    public void Key_Is_Stable_With_Lang_Signature()
    {
        Assert.Equal("1|archive|free|*|", WatchLinkUpsertCore.Key(Link())); // NULL-мова → порожня сигнатура
        var withLang = Link();
        withLang.AudioLangs = "uk,ru";
        Assert.Equal("1|archive|free|*|ru,uk", WatchLinkUpsertCore.Key(withLang)); // CanonicalKey: sorted csv
    }

    [Fact]
    public void Same_Link_Different_AudioLangs_Is_Separate_Row()
    {
        var ru = Link();
        ru.AudioLangs = "ru";
        var uk = Link();
        uk.AudioLangs = "uk";

        var (_, created) = WatchLinkUpsertCore.Resolve([ru], uk);
        Assert.True(created); // uk/ru-варіанти того самого провайдера НЕ колапсують (UX-04)

        var (resolved2, created2) = WatchLinkUpsertCore.Resolve([ru, uk], new WatchLink
        {
            TitleId = 1, Provider = "archive", Kind = "free", Region = "*",
            Url = "https://x", IsAvailable = true, AudioLangs = "uk",
        });
        Assert.False(created2); // та сама мовна сигнатура → той самий рядок
        Assert.Same(uk, resolved2);
    }

    // ── Codex IV-04: uk+ru одночасно в ОДНОМУ провайдері + ідемпотентний повторний sync ──

    [Fact]
    public void Uk_And_Ru_Together_In_Same_Provider_Are_Two_Rows_And_Re_Sync_Is_Idempotent()
    {
        // Прогін 1: uk-варіант, потім ru-варіант того самого (provider, kind, region, url).
        var stored = new List<WatchLink>();
        foreach (var langs in new[] { "uk", "ru" })
        {
            var patch = Link();
            patch.AudioLangs = langs;
            var (resolved, created) = WatchLinkUpsertCore.Resolve(stored, patch);
            if (created) stored.Add(resolved);
        }
        Assert.Equal(2, stored.Count); // uk і ru — ДВА окремі рядки (не колапс)
        Assert.Contains(stored, l => l.AudioLangs == "uk");
        Assert.Contains(stored, l => l.AudioLangs == "ru");

        // Прогін 2 (ідемпотентність): ті самі мовні варіанти НЕ додають дублів.
        var countBefore = stored.Count;
        foreach (var langs in new[] { "uk", "ru" })
        {
            var patch = Link();
            patch.AudioLangs = langs;
            patch.Url = "https://refreshed";
            var (resolved, created) = WatchLinkUpsertCore.Resolve(stored, patch);
            Assert.False(created); // попаде в існуючий рядок
            WatchLinkUpsertCore.Merge(resolved, patch);
        }
        Assert.Equal(countBefore, stored.Count); // дублів нема
        Assert.Contains(stored, l => l.Url == "https://refreshed"); // url оновлено в обох
        Assert.All(stored, l => Assert.NotEqual("uk,ru", l.AudioLangs)); // мови НЕ злились
    }

    [Fact]
    public void Re_Sync_Normalizes_Csv_Order_And_Case_To_Same_Row()
    {
        var stored = new WatchLink { TitleId = 1, Provider = "archive", Kind = "free", Region = "*", Url = "https://x", IsAvailable = true, AudioLangs = "uk,ru" };

        // інший порядок/регістр того самого набору → ТА САМА сигнатура → той самий рядок
        var patch = Link();
        patch.AudioLangs = "RU,UK";
        var (resolved, created) = WatchLinkUpsertCore.Resolve([stored], patch);
        Assert.False(created);
        Assert.Same(stored, resolved);
    }

    [Fact]
    public void Legacy_Null_Lang_Rows_Not_Duplicated_By_Re_Sync()
    {
        // старі рядки (NULL мова) — повторний sync без мов НЕ створює дубля
        var existing = Link();
        existing.AudioLangs = null;

        var patch = Link(); // AudioLangs = null
        var (resolved, created) = WatchLinkUpsertCore.Resolve([existing], patch);
        Assert.False(created);
        Assert.Same(existing, resolved);

        // і рядок БЕЗ мов не поглинає новий мовний варіант: «uk» поруч — окремий рядок
        var ukPatch = Link();
        ukPatch.AudioLangs = "uk";
        var (_, createdUk) = WatchLinkUpsertCore.Resolve([existing], ukPatch);
        Assert.True(createdUk);
    }

    [Fact]
    public void CanonicalKey_Sorted_CaseInsensitive_UnknownFiltered()
    {
        Assert.Equal("", WatchLanguages.CanonicalKey(null));
        Assert.Equal("", WatchLanguages.CanonicalKey("klingon"));          // все-невідоме → «» (NULL)
        Assert.Equal("en,ru", WatchLanguages.CanonicalKey("RU, en"));      // сортування + regístr
        Assert.Equal("en,ru,uk", WatchLanguages.CanonicalKey("ukrajinska, russkiy, eng"));
        Assert.Equal("en", WatchLanguages.CanonicalKey("en, klingon"));    // невідомі відкидаються
    }
}
