using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Api.Contracts;

public sealed record WatchSearchLink(string Provider, string Region, string Url, string? Source = null);

/// <summary>Official catalog destinations, independent of title offers and playback rights.</summary>
internal static class WatchProviderDirectory
{
    public static IEnumerable<WatchSearchLink> Get(string type, string title, string? region) =>
        RegionalWatchSources.Get(type, region).Select(source => new WatchSearchLink(source.Name, source.Country,
            source.Name == "Crunchyroll" && !string.IsNullOrWhiteSpace(title)
                ? $"{source.Catalog}search?q={Uri.EscapeDataString(title.Trim())}"
                : source.Catalog));
}
