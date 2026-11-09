using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

/// <summary>
/// WatchOptionsMerger (рішення власника 09.11): уніфікований список «де подивитись» —
/// злиття watch_links + providers, дедуп за (provider+url), нормалізація kind
/// (free|stream|rent|buy|ads) і порядок: free/ads першими, далі пріоритет джерела.
/// </summary>
public class WatchOptionsMergerTests
{
    private static WatchOptionRow Link(
        string provider = "sweettv", string kind = "free", string? url = "https://sweet.tv/film",
        string? source = null, string? region = "UA", bool? isAvailable = true,
        string? audioLang = null, int? quality = null, string? dub = null) =>
        new(provider, kind, url, source, region, isAvailable, audioLang, quality, dub);

    private static WatchOptionRow Prov(
        string provider = "Netflix", string kind = "stream", string? url = "https://justwatch.com/x",
        string? source = "justwatch", string? region = "UA") =>
        new(provider, kind, url, source, region);

    [Fact]
    public void Merge_Combines_WatchLinks_And_Providers()
    {
        var merged = WatchOptionsMerger.Merge(
            [Link(provider: "sweettv", kind: "free")],
            [Prov(provider: "Netflix", kind: "stream")]);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, o => Assert.NotEqual(string.Empty, o.Source));
    }

    [Fact]
    public void Empty_Inputs_Give_Empty_List()
    {
        Assert.Empty(WatchOptionsMerger.Merge([], []));
    }

    [Fact]
    public void Dedupe_Same_Provider_Url_Keeps_WatchLink_Metadata()
    {
        var merged = WatchOptionsMerger.Merge(
            [Link(provider: "sweettv", kind: "free", url: "https://x", audioLang: "ukr", quality: 1080, dub: "ukr")],
            [Prov(provider: "sweettv", kind: "free", url: "https://x", source: "sweettv")]);

        var o = Assert.Single(merged);
        Assert.Equal("sweettv", o.Source);
        Assert.Equal("ukr", o.AudioLang);   // метадані FR-19 збережено
        Assert.Equal(1080, o.Quality);
        Assert.Equal("ukr", o.Dub);
        Assert.True(o.IsAvailable);
    }

    [Fact]
    public void Dedupe_Different_Url_Keeps_Both()
    {
        var merged = WatchOptionsMerger.Merge(
            [Link(provider: "sweettv", url: "https://sweet.tv/film")],
            [Prov(provider: "sweettv", url: "https://justwatch.com/sweettv")]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Dedupe_Provider_Is_Case_Insensitive()
    {
        var merged = WatchOptionsMerger.Merge(
            [Link(provider: "SweetTV", url: "https://x")],
            [Prov(provider: "sweettv", url: "https://x")]);

        Assert.Single(merged);
    }

    [Fact]
    public void Same_Provider_Url_Free_Wins_Over_Subscription()
    {
        // Той самий URL і free, і subscription — free НЕ ховаємо за перемикачем платних.
        var merged = WatchOptionsMerger.Merge(
            [Link(provider: "megogo", kind: "subscription", url: "https://m")],
            [Prov(provider: "megogo", kind: "free", url: "https://m", source: "megogo")]);

        var o = Assert.Single(merged);
        Assert.Equal("free", o.Kind);
        Assert.Equal("megogo", o.Source);
    }

    [Fact]
    public void Subscription_From_WatchLinks_Becomes_Stream()
    {
        var merged = WatchOptionsMerger.Merge([Link(kind: "subscription")], []);

        var o = Assert.Single(merged);
        Assert.Equal("stream", o.Kind);
    }

    [Theory]
    [InlineData("flatrate", "stream")]
    [InlineData("subscription", "stream")]
    [InlineData("free_with_ads", "ads")]
    [InlineData("ads", "ads")]
    [InlineData("FREE", "free")]
    [InlineData("", "stream")]      // порожнє/невідоме — платний дефолт
    [InlineData("whatever", "stream")]
    public void NormalizeKind_Maps_All_Variants(string raw, string expected)
    {
        Assert.Equal(expected, WatchOptionsMerger.NormalizeKind(raw));
    }

    [Fact]
    public void Source_Derived_From_Provider_Name()
    {
        var merged = WatchOptionsMerger.Merge(
            [Link(provider: "archive"), Link(provider: "tmdb-streaming"), Link(provider: "youtube"), Link(provider: "rare-tv")],
            []);

        Assert.Equal("youtube", merged.Single(o => o.Provider == "youtube").Source);
        Assert.Equal("internet-archive", merged.Single(o => o.Provider == "archive").Source); // archive → internet-archive
        Assert.Equal("justwatch", merged.Single(o => o.Provider == "tmdb-streaming").Source); // легасі TMDB → justwatch
        Assert.Equal("rare-tv", merged.Single(o => o.Provider == "rare-tv").Source);          // невідомий → сам provider
    }

    [Fact]
    public void Providers_Row_Source_Used_As_Is()
    {
        var merged = WatchOptionsMerger.Merge([], [Prov(provider: "Apple iTunes", source: "justwatch")]);

        Assert.Equal("justwatch", Assert.Single(merged).Source);
    }

    [Fact]
    public void Order_Kind_Ranks_Before_Source_Priority()
    {
        var merged = WatchOptionsMerger.Merge(
        [
            Link(provider: "youtube", kind: "free", url: "https://y"),          // free, source youtube
            Link(provider: "archive", kind: "free", url: "https://a"),          // free, source internet-archive
            Link(provider: "sweettv", kind: "subscription", url: "https://s"),  // stream, source sweettv
            Link(provider: "megogo", kind: "rent", url: "https://mg"),          // rent, source megogo
        ],
        [
            Prov(provider: "Apple iTunes", kind: "buy", source: "justwatch", url: "https://buy"),
            Prov(provider: "Prime Video", kind: "stream", source: "justwatch", url: "https://pv"),
        ]);

        // free (за пріоритетом джерела: youtube < internet-archive) → stream (sweettv < justwatch)
        // → rent → buy; kind-ранг сильніший за пріоритет джерела.
        Assert.Equal(
            ["https://y", "https://a", "https://s", "https://pv", "https://mg", "https://buy"],
            merged.Select(o => o.Url).ToArray());
    }

    [Fact]
    public void Order_Within_Same_Kind_Follows_Source_Priority()
    {
        var merged = WatchOptionsMerger.Merge(
        [
            Link(provider: "justwatch-x", kind: "free", source: "justwatch", url: "https://jw"),
            Link(provider: "archive", kind: "free", url: "https://ia"),
            Link(provider: "plex", kind: "free", source: "plex", url: "https://plex"),
            Link(provider: "sweettv", kind: "free", url: "https://st"),
        ],
        []);

        Assert.Equal(
            ["https://st", "https://plex", "https://ia", "https://jw"], // sweettv → plex → internet-archive → justwatch
            merged.Select(o => o.Url).ToArray());
    }

    [Fact]
    public void Unavailable_WatchLink_Kept_With_Flag()
    {
        var merged = WatchOptionsMerger.Merge([Link(isAvailable: false)], []);

        var o = Assert.Single(merged);
        Assert.False(o.IsAvailable); // фронт сам фільтрує (isAvailable !== false)
    }
}
