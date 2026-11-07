namespace MovieDiscovery.Api.Contracts;

/// <summary>Провайдер перегляду з watch_links (FR-14): GET /api/titles/{id}/watch.</summary>
public sealed record WatchProviderInfo(string Provider, string Kind, string Region, string? Url, bool IsAvailable);

/// <summary>Компактний бейдж провайдера для TitleCard (лише is_available=true).</summary>
public sealed record CardWatchProvider(string Provider, string Kind, string? Url);

/// <summary>Відповідь «де подивитись».</summary>
public sealed class TitleWatchResponse
{
    public List<WatchProviderInfo> Providers { get; set; } = [];
}
