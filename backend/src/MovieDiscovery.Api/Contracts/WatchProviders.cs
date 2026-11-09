namespace MovieDiscovery.Api.Contracts;

/// <summary>
/// Провайдер перегляду з watch_links (FR-14 + FR-19): GET /api/titles/{id}/watch.
/// audioLang/quality/dub — nullable (FR-19: озвучки й якість, є не в усіх провайдерів).
/// Рішення власника 09.11: уніфікований список = злиття watch_links + providers
/// (WatchOptionsMerger); kind нормалізовано до free|stream|rent|buy|ads (subscription → stream),
/// source — джерело рядка (sweettv|megogo|plex|youtube|internet-archive|justwatch|…).
/// </summary>
public sealed record WatchProviderInfo(
    string Provider,
    string Kind,
    string Region,
    string? Url,
    bool IsAvailable,
    string? AudioLang = null,
    int? Quality = null,
    string? Dub = null,
    string? Source = null);

/// <summary>Компактний бейдж провайдера для TitleCard (лише is_available=true).</summary>
public sealed record CardWatchProvider(string Provider, string Kind, string? Url);

/// <summary>Відповідь «де подивитись».</summary>
public sealed class TitleWatchResponse
{
    public List<WatchProviderInfo> Providers { get; set; } = [];
}
