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
    public void Key_Is_Stable()
    {
        Assert.Equal("1|archive|free|*", WatchLinkUpsertCore.Key(Link()));
    }
}
