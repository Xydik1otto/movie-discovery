namespace MovieDiscovery.Api.Contracts;

/// <summary>
/// Провайдер перегляду з watch_links (FR-14 + FR-19 + UX-04): GET /api/titles/{id}/watch.
/// audioLang/quality/dub — nullable (FR-19: озвучки й якість, є не в усіх провайдерів).
/// Рішення власника 09.11: уніфікований список = злиття watch_links + providers
/// (WatchOptionsMerger); kind нормалізовано до free|stream|rent|buy|ads (subscription → stream),
/// source — джерело рядка (sweettv|megogo|plex|youtube|internet-archive|justwatch|…).
/// UX-04 (хвиля 2):
///   • audioLangs/subLangs — csv ISO-639-1 («en,ru») з реальних джерел; NULL = мови
///     невідомі (джерело не заявило; НІКОЛИ не uk/ru за замовчуванням).
///   • isAvailable — сумісне bool-поле: true = ПЕРЕВІРЕНО доступно; false = НЕ доступно
///     АБО unknown — точний tri-state у availabilityStatus (available|unavailable|unknown).
///     Неперевірений каталог Plex = unknown, ніколи не «Безкоштовно»-бейдж.
///   • urlKind — direct | find (WatchUrlClassifier): find-URL чесно підписується
///     «Знайти на сервісу» на фронті, не подається як пряме переглядання.
///   • price/currency — чесна ціна платного оффера, якщо джерело її дало (інакше null).
///   • coverage — full | episode | unknown (NULL = не перевірено верифікатором): full =
///     ПЕРЕВІРЕНО лінк веде на повний фільм/весь серіал; episode = фрагмент/епізод
///     (фронт може ховати такі рядки у дефолтному free-фільтрі); unknown/NULL — чесна
///     невизначеність, а не «повний». Пише WatchLinkVerificationService (coverage + verified_at).
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
    string? Source = null,
    string? AudioLangs = null,
    string? SubLangs = null,
    string? Price = null,
    string? Currency = null,
    string? UrlKind = null,
    string? AvailabilityStatus = null,
    string? Coverage = null);

/// <summary>Компактний бейдж провайдера для TitleCard (лише is_available=true).</summary>
public sealed record CardWatchProvider(string Provider, string Kind, string? Url);

/// <summary>Відповідь «де подивитись».</summary>
public sealed class TitleWatchResponse
{
    public List<WatchProviderInfo> Providers { get; set; } = [];
}
