namespace MovieDiscovery.Infrastructure.Watchability;

public sealed record RegionalWatchSource(string Name, string Country, string Catalog, string[] TitlePaths, bool AnimeOnly = false)
{
    public bool Matches(Uri url) => CatalogUri.Host.Equals(url.Host, StringComparison.OrdinalIgnoreCase) &&
        TitlePaths.Any(path => url.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase) &&
            url.AbsolutePath.TrimEnd('/').Length > path.Length);

    private Uri CatalogUri { get; } = new(Catalog);
}

public static class RegionalWatchSources
{
    private static readonly string[] BilibiliCountries = ["ID", "MY", "PH", "SG", "TH", "VN"];

    private static readonly RegionalWatchSource[] Sources =
    [
        new("SWEET.TV", "UA", "https://sweet.tv/ua-uk", ["/movie/", "/series/", "/ua-uk/movie/", "/ua-uk/series/"]),
        new("MEGOGO", "UA", "https://megogo.net/ua", ["/ua/view/"]),
        new("Player.pl", "PL", "https://player.pl/", ["/filmy-online/", "/seriale-online/"]),
        new("TVP VOD", "PL", "https://vod.tvp.pl/", ["/filmy-fabularne/", "/seriale/"]),
        new("BBC iPlayer", "GB", "https://www.bbc.co.uk/iplayer", ["/iplayer/episodes/", "/iplayer/episode/"]),
        new("ITVX", "GB", "https://www.itv.com/watch", ["/watch/"]),
        new("ARD Mediathek", "DE", "https://www.ardmediathek.de/daserste", ["/film/", "/serie/", "/video/"]),
        new("ZDF", "DE", "https://www.zdf.de/", ["/filme/", "/serien/"]),
        new("ARTE", "DE", "https://www.arte.tv/de/", ["/de/videos/"]),
        new("ARTE", "FR", "https://www.arte.tv/fr/", ["/fr/videos/"]),
        new("RTVE Play", "ES", "https://www.rtve.es/play/", ["/play/videos/"]),
        new("RaiPlay", "IT", "https://www.raiplay.it/", ["/programmi/", "/video/"]),
        new("ABC iview", "AU", "https://iview.abc.net.au/", ["/show/", "/video/"]),
        new("SBS On Demand", "AU", "https://www.sbs.com.au/ondemand/", ["/ondemand/movie/", "/ondemand/tv-series/"]),
        new("CBC Gem", "CA", "https://gem.cbc.ca/", []),
        new("ICI TOU.TV", "CA", "https://ici.tou.tv/", []),
        new("PBS", "US", "https://www.pbs.org/", ["/show/", "/video/"]),
        new("Plex", "US", "https://watch.plex.tv/", ["/movie/", "/show/"]),
        new("Plex", "CA", "https://watch.plex.tv/", ["/movie/", "/show/"]),
        new("Crunchyroll", "*", "https://www.crunchyroll.com/", ["/series/", "/watch/"], AnimeOnly: true),
        new("d Anime Store", "JP", "https://animestore.docomo.ne.jp/animestore/CH/danime", [], AnimeOnly: true),
        .. BilibiliCountries.Select(code => new RegionalWatchSource("BiliBili", code, "https://www.bilibili.tv/en/anime",
            ["/en/play/", "/id/play/", "/th/play/", "/vi/play/"], AnimeOnly: true)),
    ];

    public static IEnumerable<RegionalWatchSource> Get(string type, string? country) => type == "manga" ? [] :
        Sources.Where(source => (!source.AnimeOnly || type == "anime") &&
            (country is null || source.Country == "*" || source.Country == country));
}
