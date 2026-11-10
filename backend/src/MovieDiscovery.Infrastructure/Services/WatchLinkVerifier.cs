using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>
/// Результат прогону watch-верифікації: скільки рядків перевірено і які вердикти.
/// </summary>
public sealed record WatchLinkVerificationResult(
    int Checked,      // рядків, де верифікація завершилась вердиктом (ok | gone)
    int Full,         // coverage = full (перевірено повний фільм/весь серіал)
    int Episode,      // coverage = episode (фрагмент/епізод)
    int Unavailable,  // 404/410 → is_available=false (watch_links), coverage не чіпається
    int Transient);   // 429/5xx/таймаут/бот-стіна — рядок НЕ оновлено, наступний прогін добере

/// <summary>
/// Джоба watch-верифікації: чесний автоматичний провірник лінків «де подивитись».
/// Обходить watch_links і providers (verified_at IS NULL АБО старша за 30 днів),
/// популярність тайтла DESC, батчами по 50; для кожного лінка — HTTP GET (15 с таймаут,
/// пауза 400 мс, ретраї 429/502/503 з Retry-After ≤ 3 с) і визначення покриття:
/// full = перевірено повний фільм/весь серіал; episode = фрагмент/епізод; unknown = чесна
/// невизначеність (false-positive «full» — корінь скарги на Breaking Bad).
/// Провал верифікації (404/410) НЕ означає episode: coverage лишається незмінним,
/// прапорець недоступності йде наявним IsAvailable-шляхом (watch_links.is_available=false).
/// </summary>
public sealed record WatchLinkVerificationJob(int? MaxRows);

/// <summary>Черга watch-верифікації (патерн WikipediaPosterSyncQueue: BoundedChannel 10, DropOldest).</summary>
public sealed class WatchLinkVerificationQueue
{
    private readonly Channel<WatchLinkVerificationJob> _channel =
        Channel.CreateBounded<WatchLinkVerificationJob>(
            new BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(WatchLinkVerificationJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<WatchLinkVerificationJob> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка watch-верифікації: черга → WatchLinkVerificationService (у власному scope).</summary>
public sealed class WatchLinkVerificationBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<WatchLinkVerificationBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<WatchLinkVerificationQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<WatchLinkVerificationService>();
                var result = await service.RunAsync(job.MaxRows, stoppingToken);
                log.LogInformation(
                    "Watch-верифікація завершена: checked={Checked}, full={Full}, episode={Episode}, unavailable={Unavailable}, transient={Transient}",
                    result.Checked, result.Full, result.Episode, result.Unavailable, result.Transient);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка watch-верифікації (maxRows={MaxRows})", job.MaxRows);
            }
        }
    }
}

/// <summary>
/// Верифікатор watch-лінків: обходить базу (watch_links + providers), HTTP-перевіряє URL
/// і пише coverage + verified_at. Реєструється як typed HttpClient
/// (AddHttpClient&lt;WatchLinkVerificationService&gt; з Timeout 15 с) + singleton-черга
/// + HostedService — wiring у Program.cs робить оркестратор.
/// </summary>
public sealed class WatchLinkVerificationService(
    MovieDiscoveryDbContext db,
    HttpClient http,
    ILogger<WatchLinkVerificationService> log,
    TimeSpan? throttle = null)
{
    public const int BatchSize = 50;

    private const int RetryAttempts = 3;             // спроби на 429/502/503
    private const int HtmlScanBytes = 512 * 1024;    // читаємо лише голову сторінки (мета + ytInitialPlayerResponse)
    internal const int FullMovieMinSeconds = 45 * 60; // > 45 хв → full-кандидат повнометражки (фільм)
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly string UserAgent = "KinoNavigatorBot/1.0 (+watch-link verification)";

    private readonly TimeSpan _throttle = throttle ?? TimeSpan.FromMilliseconds(400);

    public async Task<WatchLinkVerificationResult> RunAsync(int? maxRows, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30); // verified_at старша за 30 днів — перевірити знову
        var stats = new Stats();
        var budget = maxRows is { } m ? Math.Max(0, m) : int.MaxValue;

        // Спочатку watch_links (перевірені лінки з метаданими FR-19 — те, що бачить юзер),
        // потім providers-каталог. Бюджет діляться.
        budget = await VerifyWatchLinksAsync(budget, cutoff, stats, ct);
        if (budget > 0)
            await VerifyProvidersAsync(budget, cutoff, stats, ct);

        log.LogInformation(
            "Watch-верифікація: checked={Checked}, full={Full}, episode={Episode}, unavailable={Unavailable}, transient={Transient}",
            stats.Checked, stats.Full, stats.Episode, stats.Unavailable, stats.Transient);
        return new WatchLinkVerificationResult(stats.Checked, stats.Full, stats.Episode, stats.Unavailable, stats.Transient);
    }

    private sealed class Stats
    {
        public int Checked;
        public int Full;
        public int Episode;
        public int Unavailable;
        public int Transient;
    }

    // ── watch_links ────────────────────────────────────────────────────────────

    private async Task<int> VerifyWatchLinksAsync(int budget, DateTime cutoff, Stats stats, CancellationToken ct)
    {
        var done = new HashSet<int>();
        while (budget > 0)
        {
            // verified_at IS NULL ПЕРШИМИ (ніколи не перевірені), потім прострочені;
            // усередині — популярність тайтла DESC. «done» не дає зациклитись на
            // transient-рядках (їх verified_at не змінюється — вони випадуть лише з цього прогону).
            var page = await db.WatchLinks.AsNoTracking()
                .Where(w => !done.Contains(w.Id) && (w.VerifiedAt == null || w.VerifiedAt < cutoff))
                .Join(db.Titles.AsNoTracking(),
                    w => w.TitleId, t => t.Id,
                    (w, t) => new { w.Id, w.VerifiedAt, Popularity = t.Popularity, MediaType = t.Type })
                .OrderByDescending(x => x.VerifiedAt == null)
                .ThenByDescending(x => x.Popularity)
                .ThenBy(x => x.Id)
                .Take(Math.Min(BatchSize, budget))
                .ToListAsync(ct);
            if (page.Count == 0) break;

            var ids = page.Select(x => x.Id).ToList();
            var meta = page.ToDictionary(x => x.Id, x => x.MediaType);
            var rows = await db.WatchLinks.Where(w => ids.Contains(w.Id)).ToListAsync(ct);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(row.Url))
                {
                    // нема чого перевіряти — чесна позначка «перевірено» без покриття
                    row.VerifiedAt = DateTime.UtcNow;
                    row.Coverage = ResolveCoverage(WatchUrlClassifier.Unknown, row.Coverage);
                    stats.Checked++;
                }
                else
                {
                    var mediaType = meta.GetValueOrDefault(row.Id);
                    var outcome = await VerifyOneUrlAsync(row.Url.Trim(), row.Provider, mediaType, ct);
                    ApplyWatchLink(row, outcome, stats);
                }
                done.Add(row.Id);
                budget--;
                if (budget <= 0) break;
                await Task.Delay(_throttle, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        return budget;
    }

    private void ApplyWatchLink(WatchLink row, FetchOutcome outcome, Stats stats)
    {
        switch (outcome.Kind)
        {
            case OutcomeKind.Ok:
                // свіжий стан перевірки: доступність + coverage (unknown НЕ затирає
                // наявний definite — fetch не спростував попередню верифікацію)
                row.IsAvailable = true;
                row.CheckedAt = DateTime.UtcNow;
                row.VerifiedAt = DateTime.UtcNow;
                row.Coverage = ResolveCoverage(outcome.Coverage, row.Coverage);
                stats.Checked++;
                if (outcome.Coverage == WatchUrlClassifier.Full) stats.Full++;
                else if (outcome.Coverage == WatchUrlClassifier.Episode) stats.Episode++;
                break;
            case OutcomeKind.Gone:
                // провал верифікації ≠ episode: coverage не чіпаємо, недоступність — наявний шлях
                row.IsAvailable = false;
                row.CheckedAt = DateTime.UtcNow;
                row.VerifiedAt = DateTime.UtcNow;
                stats.Checked++;
                stats.Unavailable++;
                break;
            default: // Transient | Unverified — чесно не верифікували, нічого не пишемо
                stats.Transient++;
                break;
        }
    }

    // ── providers (каталог JustWatch/Plex/Megogo/…) ───────────────────────────

    private async Task VerifyProvidersAsync(int budget, DateTime cutoff, Stats stats, CancellationToken ct)
    {
        var done = new HashSet<int>();
        while (budget > 0)
        {
            var page = await db.Providers.AsNoTracking()
                .Where(p => !done.Contains(p.Id) && (p.VerifiedAt == null || p.VerifiedAt < cutoff))
                .Join(db.Titles.AsNoTracking(),
                    p => p.TitleId, t => t.Id,
                    (p, t) => new { p.Id, p.VerifiedAt, Popularity = t.Popularity, MediaType = t.Type })
                .OrderByDescending(x => x.VerifiedAt == null)
                .ThenByDescending(x => x.Popularity)
                .ThenBy(x => x.Id)
                .Take(Math.Min(BatchSize, budget))
                .ToListAsync(ct);
            if (page.Count == 0) break;

            var ids = page.Select(x => x.Id).ToList();
            var meta = page.ToDictionary(x => x.Id, x => x.MediaType);
            var rows = await db.Providers.Where(p => ids.Contains(p.Id)).ToListAsync(ct);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(row.Url))
                {
                    // нема чого перевіряти — чесна позначка «перевірено» без покриття
                    row.VerifiedAt = DateTime.UtcNow;
                    row.Coverage = ResolveCoverage(WatchUrlClassifier.Unknown, row.Coverage);
                    stats.Checked++;
                }
                else
                {
                    var mediaType = meta.GetValueOrDefault(row.Id);
                    var outcome = await VerifyOneUrlAsync(row.Url.Trim(), row.ProviderName, mediaType, ct);
                    // у providers нема прапорця доступності (tri-state рахується на читанні) —
                    // пишемо лише coverage + verified_at
                    switch (outcome.Kind)
                    {
                        case OutcomeKind.Ok:
                            row.VerifiedAt = DateTime.UtcNow;
                            row.Coverage = ResolveCoverage(outcome.Coverage, row.Coverage);
                            stats.Checked++;
                            if (outcome.Coverage == WatchUrlClassifier.Full) stats.Full++;
                            else if (outcome.Coverage == WatchUrlClassifier.Episode) stats.Episode++;
                            break;
                        case OutcomeKind.Gone:
                            row.VerifiedAt = DateTime.UtcNow; // мертвий каталог-рядок: coverage не чіпаємо
                            stats.Checked++;
                            stats.Unavailable++;
                            break;
                        default:
                            stats.Transient++;
                            break;
                    }
                }
                done.Add(row.Id);
                budget--;
                if (budget <= 0) break;
                await Task.Delay(_throttle, ct);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>unknown-вердикт не затирає наявний definite (перевірений) покриття.</summary>
    private static string? ResolveCoverage(string decided, string? existing) =>
        decided != WatchUrlClassifier.Unknown
            ? decided
            : existing is WatchUrlClassifier.Full or WatchUrlClassifier.Episode ? existing : null;

    // ── HTTP + вердикт ─────────────────────────────────────────────────────────

    private enum OutcomeKind { Ok, Gone, Transient, Unverified }

    private sealed record FetchOutcome(OutcomeKind Kind, string Coverage = WatchUrlClassifier.Unknown)
    {
        public static FetchOutcome Ok(string coverage) => new(OutcomeKind.Ok, coverage);
        public static FetchOutcome Gone { get; } = new(OutcomeKind.Gone);
        public static FetchOutcome Transient { get; } = new(OutcomeKind.Transient);
        public static FetchOutcome Unverified { get; } = new(OutcomeKind.Unverified);
    }

    private static bool IsYouTube(string url, string providerName)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (host.Contains("youtube") || host == "youtu.be") return true;
        }
        return providerName.Contains("youtube", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Верифікувати один URL: GET з ретраями → вердикт покриття/доступності.</summary>
    private async Task<FetchOutcome> VerifyOneUrlAsync(string url, string providerName, string? mediaType, CancellationToken ct)
    {
        if (!IsYouTube(url, providerName))
        {
            var main = await GetWithRetryAsync(url, ct);
            return DecideFromPage(main, url, providerName, mediaType, isYouTube: false);
        }

        // YouTube: API-free оцінка — спершу дешевий oEmbed (title без квоти), далі — голова
        // сторінки перегляду (lengthSeconds/itemprop-duration). Форма watch?v= не розрізняє
        // фільм/епізод, тому вирішують ЛИШЕ живі сигнали, інакше чесний unknown.
        var oembed = await GetWithRetryAsync(BuildOembedUrl(url), ct);
        if (oembed.Transient) return FetchOutcome.Transient;
        if (oembed.Html is null)
        {
            // 404/410 oEmbed = відео видалено; 401 = embed заборонено (відео живе) — йдемо на сторінку
            if (oembed.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return FetchOutcome.Gone;
        }
        else
        {
            var oembedTitle = ExtractOembedTitle(oembed.Html);
            if (oembedTitle != null && WatchCoverageHeuristics.HasEpisodeMarker(oembedTitle))
                return FetchOutcome.Ok(WatchUrlClassifier.Episode);
        }

        var page = await GetWithRetryAsync(url, ct);
        return DecideFromPage(page, url, providerName, mediaType, isYouTube: true);
    }

    private static string BuildOembedUrl(string watchUrl) =>
        "https://www.youtube.com/oembed?url=" + Uri.EscapeDataString(watchUrl) + "&format=json";

    private FetchOutcome DecideFromPage(
        FetchResult result, string url, string providerName, string? mediaType, bool isYouTube)
    {
        if (result.Transient) return FetchOutcome.Transient;
        if (result.Html is null)
            return result.Status is HttpStatusCode.NotFound or HttpStatusCode.Gone
                ? FetchOutcome.Gone
                : FetchOutcome.Unverified; // 403 бот-стіна та інше — чесно не верифікували

        var title = WatchPageSignals.ExtractTitle(result.Html);
        var duration = WatchPageSignals.ExtractDurationSeconds(result.Html);
        var coverage = WatchCoverageHeuristics.Decide(
            WatchUrlClassifier.ClassifyCoverage(url, providerName, mediaType),
            title, duration, mediaType, isYouTube);
        return FetchOutcome.Ok(coverage);
    }

    /// <summary>Результат GET: 200 → Html (голова сторінки ≤512 КБ); 404/410/інше → Status; мережа/таймаут → Transient.</summary>
    private sealed record FetchResult(HttpStatusCode Status, string? Html, bool Transient);

    /// <summary>GET з ретраями 429/502/503 (Retry-After ≤ 3 с). 200 → Html прочитано ДО dispose відповіді.</summary>
    private async Task<FetchResult> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= RetryAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Accept.ParseAdd("text/html,application/json;q=0.9,*/*;q=0.8");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(RequestTimeout);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

                if (resp.StatusCode == HttpStatusCode.OK)
                    return new FetchResult(resp.StatusCode, await ReadHeadAsync(resp, ct), Transient: false);
                if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    return new FetchResult(resp.StatusCode, null, Transient: false);
                if (resp.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
                {
                    if (attempt < RetryAttempts)
                    {
                        await Task.Delay(RetryDelay(resp.Headers.RetryAfter), ct);
                        continue;
                    }
                    return new FetchResult(resp.StatusCode, null, Transient: true);
                }
                return new FetchResult(resp.StatusCode, null, Transient: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // зупинка джоби — не таймаут
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                // таймаут (15 с) або мережева помилка: ретрай, після останньої — transient
                if (attempt >= RetryAttempts)
                {
                    log.LogWarning("Watch-верифікація: мережа/таймаут для {Url}: {Msg}", url, ex.Message);
                    return new FetchResult(0, null, Transient: true);
                }
                await Task.Delay(500, ct);
            }
        }
        return new FetchResult(0, null, Transient: true);
    }

    /// <summary>Retry-After ≤ 3 с; без заголовка — 0.5 с (обережний дефолт).</summary>
    private static TimeSpan RetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        var cap = TimeSpan.FromSeconds(3);
        if (retryAfter?.Delta is { } d) return d > cap ? cap : (d < TimeSpan.Zero ? TimeSpan.Zero : d);
        if (retryAfter?.Date is { } date)
        {
            var delta = date - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero) return delta > cap ? cap : delta;
        }
        return TimeSpan.FromMilliseconds(500);
    }

    /// <summary>Прочитати голову відповіді (≤512 КБ) — достатньо для мета-тегів і lengthSeconds.</summary>
    private static async Task<string> ReadHeadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[HtmlScanBytes];
        var total = 0;
        int read;
        while (total < buf.Length &&
               (read = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), ct)) > 0)
            total += read;
        return Encoding.UTF8.GetString(buf, 0, total);
    }

    private static string? ExtractOembedTitle(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Сигнали зі сторінки/відповіді (чисті функції): тривалість і заголовок.
/// Мінімум надійних сигналів — невизначене лишається unknown.
/// </summary>
public static partial class WatchPageSignals
{
    /// <summary>Тривалість у секундах із HTML: YouTube lengthSeconds/approxDurationMs, ISO-8601
    /// (itemprop="duration", JSON-LD "duration"), og:video:duration. Немає/невідомий формат → null.</summary>
    public static int? ExtractDurationSeconds(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        if (int.TryParse(MatchGroup(LengthSecondsRx(), html), out var lenSec) && lenSec > 0)
            return lenSec;

        if (MatchGroup(ApproxDurationMsRx(), html) is { } msRaw &&
            double.TryParse(msRaw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var msVal) && msVal > 0)
            return (int)Math.Round(msVal / 1000.0);

        if (IsoItempropRx().Match(html) is { Success: true } a && ParseIsoDuration(a.Groups["v"].Value) is { } isoA && isoA > 0)
            return isoA;
        if (JsonLdDurationRx().Match(html) is { Success: true } b && ParseIsoDuration(b.Groups["v"].Value) is { } isoB && isoB > 0)
            return isoB;

        if (int.TryParse(MatchGroup(OgDurationRx(), html), out var ogSec) && ogSec > 0)
            return ogSec;
        return null;
    }

    /// <summary>Заголовок сторінки: og:title, інакше &lt;title&gt;. Тільки ці дві точки — слово «епізод»
    /// у меню/навігації сторінки НЕ є маркером епізодності.</summary>
    public static string? ExtractTitle(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = OgTitleA().Match(html) is { Success: true } a ? a : OgTitleB().Match(html);
        var raw = m.Success ? m.Groups["v"].Value : null;
        if (string.IsNullOrEmpty(raw))
        {
            var t = TitleTag().Match(html);
            raw = t.Success ? t.Groups["v"].Value : null;
        }
        raw = raw is null ? null : System.Net.WebUtility.HtmlDecode(raw).Trim();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static string? MatchGroup(Regex re, string input)
    {
        var m = re.Match(input);
        return m.Success ? m.Groups["v"].Value : null;
    }

    /// <summary>ISO-8601 PT1H35M / PT95M / PT30M10S → секунди; невідомий формат → null.</summary>
    public static int? ParseIsoDuration(string iso)
    {
        var m = IsoPartsRx().Match((iso ?? "").Trim());
        if (!m.Success) return null;
        int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        int s = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        var total = h * 3600 + min * 60 + s;
        return total > 0 ? total : null;
    }

    [GeneratedRegex(@"^PT(?:(\d{1,3})H)?(?:(\d{1,3})M)?(?:(\d{1,3})S)?$", RegexOptions.IgnoreCase)]
    private static partial Regex IsoPartsRx();

    [GeneratedRegex(@"""lengthSeconds""\s*:\s*""(?<v>\d{1,7})""", RegexOptions.IgnoreCase)]
    private static partial Regex LengthSecondsRx();

    [GeneratedRegex(@"""approxDurationMs""\s*:\s*""?(?<v>\d{7,})""?", RegexOptions.IgnoreCase)]
    private static partial Regex ApproxDurationMsRx();

    [GeneratedRegex(@"itemprop=""duration""[^>]*content=""(?<v>[^""]+)""|content=""(?<v>PT[^""]+)""[^>]*itemprop=""duration""", RegexOptions.IgnoreCase)]
    private static partial Regex IsoItempropRx();

    [GeneratedRegex(@"""(?:duration|timeRequired)""\s*:\s*""(?<v>PT[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdDurationRx();

    [GeneratedRegex(@"<meta[^>]+(?:property|name)=""og:video:duration""[^>]+content=""(?<v>\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgDurationRx();

    [GeneratedRegex(@"<meta[^>]+(?:property|name)=""og:title""[^>]+content=""(?<v>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleA();

    [GeneratedRegex(@"<meta[^>]+content=""(?<v>[^""]*)""[^>]+(?:property|name)=""og:title""", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitleB();

    [GeneratedRegex(@"<title[^>]*>(?<v>[^<]+)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();
}

/// <summary>
/// Вердикт покриття зі зібраних сигналів: маркер епізоду в заголовку (найспецифічніший
/// сигнал) → episode; definite URL-клас → він; duration &gt; 45 хв → full (фільм; для
/// серіалу 45 хв — звичайний епізод, сигнал не спрацьовує); YouTube + series + «Season N»
/// у назві (без номера епізоду) → повний сезон = full. Інакше — unknown.
/// </summary>
public static partial class WatchCoverageHeuristics
{
    public const int FullMovieMinSeconds = 45 * 60;

    public static bool HasEpisodeMarker(string title) => EpisodeMarkerRx().IsMatch(title);

    public static bool HasSeasonMarker(string title) => SeasonMarkerRx().IsMatch(title);

    public static bool IsSeries(string? mediaType) =>
        string.Equals((mediaType ?? "").Trim(), "series", StringComparison.OrdinalIgnoreCase);

    public static string Decide(string urlClass, string? title, int? durationSeconds, string? mediaType, bool isYouTube = false)
    {
        if (!string.IsNullOrWhiteSpace(title) && HasEpisodeMarker(title))
            return WatchUrlClassifier.Episode;
        if (urlClass is WatchUrlClassifier.Full or WatchUrlClassifier.Episode)
            return urlClass;
        if (durationSeconds is { } d && d >= FullMovieMinSeconds && !IsSeries(mediaType))
            return WatchUrlClassifier.Full;
        if (isYouTube && IsSeries(mediaType) && !string.IsNullOrWhiteSpace(title) && HasSeasonMarker(title))
            return WatchUrlClassifier.Full; // плейліст/викладка «Season 1» = повний сезон
        return WatchUrlClassifier.Unknown;
    }

    /// <summary>Маркери ЕПІЗОДУ: S01E06, «episode 6», «Ep. 6», «серія 6»/«6 серія», «епізод 6» (uk + ru). Голе
    /// «Season 1» НЕ маркер епізоду (це рівень сезону).</summary>
    [GeneratedRegex(
        @"\b(?:s\d{1,2}\s?e\d{1,3}|(?:ep(?:isode)?|сер(?:ія|ії|ію|ия|ии|ию)|епізод|эпизод)\s*[.:№#-]?\s*\d{1,3}|\d{1,3}\s*№?\s*(?:сер(?:ія|ії|ію|ия|ии|ию)|епізод|эпизод|episode))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeMarkerRx();

    /// <summary>Голий маркер сезону («Season 1»/«Сезон 1» без номера епізоду).</summary>
    [GeneratedRegex(@"\b(?:season|сезон)\s*\d{1,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonMarkerRx();
}
