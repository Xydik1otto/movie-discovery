using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Watchability;

public sealed class WatchWebOptions
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public int DailyRequests { get; set; } = 32;
    public int SweepIntervalMinutes { get; set; } = 60;
}

public sealed partial class WebWatchSearch(HttpClient http, IOptions<WatchWebOptions> options)
{
    public const string Source = "brave-web";
    public const string CandidateKind = "candidate";
    public const string PendingKind = "pending-review";
    public const string ApprovedKind = "reviewed";
    public const string RejectedKind = "rejected";
    public static readonly TimeSpan CandidateLifetime = TimeSpan.FromDays(30);

    private static readonly Dictionary<string, (string Market, string Language, string Watch)> Locales = new(StringComparer.Ordinal)
    {
        ["UA"] = ("ALL", "uk", "дивитися"), ["PL"] = ("PL", "pl", "oglądaj"), ["DE"] = ("DE", "de", "ansehen"),
        ["FR"] = ("FR", "fr", "regarder"), ["IT"] = ("IT", "it", "guarda"), ["ES"] = ("ES", "es", "ver"),
        ["GB"] = ("GB", "en-gb", "watch"), ["US"] = ("US", "en", "watch"), ["CA"] = ("CA", "en", "watch"),
        ["AU"] = ("AU", "en", "watch"), ["JP"] = ("JP", "jp", "視聴"), ["ID"] = ("ID", "", "tonton lang:id"),
        ["MY"] = ("MY", "ms", "tonton"), ["PH"] = ("PH", "en", "watch"), ["SG"] = ("ALL", "en", "watch"),
        ["TH"] = ("ALL", "th", "รับชม"), ["VN"] = ("ALL", "vi", "xem"),
    };

    public static IEnumerable<string> Countries => Locales.Keys;

    public static bool CanSearch(string type, string country) => type != "manga" && Locales.ContainsKey(country);

    public async Task<List<Provider>> FindAsync(Title title, string country, CancellationToken ct)
    {
        var year = title.Year?.ToString(CultureInfo.InvariantCulture);
        var names = (country == "UA" ? new[] { title.TitleUk, title.TitleEn } : new[] { title.TitleEn, title.TitleUk })
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim()).ToArray();
        if (names.Length == 0 || year is null || !Locales.TryGetValue(country, out var locale)) return [];

        var sources = RegionalWatchSources.Get(title.Type, country).Where(source => source.TitlePaths.Length > 0).ToArray();
        var queryTitle = TitleNormalizer.Normalize(names[0]);
        if (queryTitle.Length == 0) return [];
        queryTitle = string.Join(' ', queryTitle.Split(' ').Take(32));
        if (queryTitle.Length > 180) queryTitle = queryTitle[..180];
        var query = $"\"{queryTitle}\" {year} {locale.Watch}";
        var hostRules = string.Join('\n', sources.Select(source => $"$boost=4,site={new Uri(source.Catalog).Host}").Distinct());
        var goggles = hostRules.Length > 0
            ? "&goggles=" + Uri.EscapeDataString("! name: Official viewing services\n" + hostRules)
            : "";
        var language = locale.Language.Length > 0 ? $"&search_lang={locale.Language}" : "";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.search.brave.com/res/v1/web/search?result_filter=web&count=12&safesearch=strict&spellcheck=false" +
            $"&country={locale.Market}{language}&q={Uri.EscapeDataString(query)}{goggles}");
        request.Headers.Add("X-Subscription-Token", options.Value.ApiKey.Trim());
        request.Headers.Add("X-Loc-Country", country);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SearchResponse>(ct);
        var matches = new Dictionary<string, Provider>(StringComparer.OrdinalIgnoreCase);
        var unknownCount = 0;
        foreach (var result in payload?.Web?.Results ?? [])
        {
            if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
                !url.IsDefaultPort || url.UserInfo.Length != 0 || url.AbsoluteUri.Length > 512)
                continue;
            var source = sources.FirstOrDefault(source => source.Matches(url));
            if (!MatchesTitle(result, names, year)) continue;
            if (source is null && (unknownCount >= 2 || !IsReviewable(url))) continue;

            var provider = source?.Name ?? url.IdnHost;
            if (matches.TryAdd(provider, new Provider
            {
                TitleId = title.Id,
                ProviderName = provider,
                Kind = source is null ? PendingKind : CandidateKind,
                Source = Source,
                Region = country,
                Url = url.GetLeftPart(UriPartial.Path),
            }) && source is null) unknownCount++;
        }
        return [.. matches.Values];
    }

    private static bool IsReviewable(Uri url) =>
        url.HostNameType == UriHostNameType.Dns && PublicDomain().IsMatch(url.IdnHost) &&
        !NonViewingHost().IsMatch(url.IdnHost) && ViewingPath().IsMatch(url.AbsolutePath) &&
        WatchUrlClassifier.Classify(url.GetLeftPart(UriPartial.Path)) == WatchUrlClassifier.Direct;

    private static bool MatchesTitle(SearchResult result, string[] names, string year)
    {
        var heading = WebUtility.HtmlDecode(HtmlTag().Replace(result.Title ?? "", ""));
        var description = WebUtility.HtmlDecode(HtmlTag().Replace(result.Description ?? "", ""));
        var candidate = ProviderSuffix().Split(heading, 2)[0].Trim();
        var yearMatch = ReleaseYear().Match(candidate);
        if (yearMatch.Success)
        {
            if (yearMatch.Groups[1].Value != year) return false;
            candidate = candidate[..yearMatch.Index].Trim();
        }
        else if (!YearToken().Matches(description).Select(match => match.Value).Distinct().SequenceEqual([year])) return false;

        var normalized = TitleNormalizer.Normalize(candidate);
        return normalized.Length > 0 && names.Any(name => TitleNormalizer.Normalize(name) == normalized);
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+[|–—]\s+|\s+-\s+")]
    private static partial Regex ProviderSuffix();

    [GeneratedRegex(@"\s+\((\d{4})\)\s*$")]
    private static partial Regex ReleaseYear();

    [GeneratedRegex(@"(?<!\d)(?:18|19|20)\d{2}(?!\d)")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex PublicDomain();

    [GeneratedRegex(@"\.(?:local|localhost|invalid|internal|test|example|onion)$|(?:^|\.)(?:imdb|themoviedb|justwatch|wikipedia|youtube|reddit|facebook|tiktok|instagram|rottentomatoes|letterboxd)\.[a-z.]+$", RegexOptions.IgnoreCase)]
    private static partial Regex NonViewingHost();

    [GeneratedRegex(@"/(?:watch|movie|movies|film|series|show|anime|title)/[^/]+/?$", RegexOptions.IgnoreCase)]
    private static partial Regex ViewingPath();

    private sealed record SearchResponse(WebResults? Web);
    private sealed record WebResults(List<SearchResult>? Results);
    private sealed record SearchResult(string? Title, string? Url, string? Description);
}
