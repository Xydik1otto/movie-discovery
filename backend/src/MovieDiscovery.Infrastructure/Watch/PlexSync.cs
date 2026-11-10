using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MovieDiscovery.Infrastructure.Watch;

/// <summary>Джоба plex-синку: діапазон років + максимум year-сторінок за прогін.</summary>
public sealed record PlexSyncJob(int? MaxPages, int? YearFrom, int? YearTo);

/// <summary>Черга plex-синхронізації (202 → фонова джоба).</summary>
public sealed class PlexSyncQueue
{
    private readonly System.Threading.Channels.Channel<PlexSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<PlexSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(PlexSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<PlexSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Результат прогону plex-sync.</summary>
public sealed record PlexSyncResult(int Pages, int Items, int Matched, int Deleted, int Added, int SkippedPages);

/// <summary>Кандидат матчингу (проєкція titles — без навігацій; InMemory/Npgsql сумісно).</summary>
public sealed record PlexCandidate(int Id, string? TitleEn, string? TitleUk, string? TitleRu, int? Year);

/// <summary>
/// Безкоштовний каталог Plex → рядки providers (source=plex, kind=free, region=UA):
/// crawling year-сторінок /movie-database/{year} (server-render; 1920..поточний+1) →
/// слаг+назва+рік → матч за нормалізованою назвою + рік ±1 → upsert Provider.
/// Тайтли НЕ створюються (каталог — IMDb/інші синки); матчиться лише наявне.
/// Заміна рядків джерела — ІДЕМПОТЕНТНА за прогін: свіжі матчі замінюють source=plex-рядки
/// у вікні років прогону (повний діапазон = повна заміна; capped-прогін (MaxPages) не чіпає
/// рядки інших років). Сторінка, що не взялася після ретраїв (Plex edge буває стійкий 503),
/// пропускається — повторний прогін добирає (ідемпотентно).
/// </summary>
public sealed class PlexSyncService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    IOptions<PlexOptions> options,
    ILogger<PlexSyncService> log)
{
    public const string Source = "plex";
    public const string ProviderName = "plex";
    public const string Kind = "free";
    public const string Region = "UA";

    private readonly PlexOptions _opt = options.Value;
    private readonly PlexClient _client = new(http, options, log);

    public async Task<PlexSyncResult> RunAsync(PlexSyncJob job, CancellationToken ct = default)
    {
        var to = job.YearTo ?? (_opt.YearTo > 0 ? _opt.YearTo : DateTime.UtcNow.Year + 1);
        var from = job.YearFrom ?? _opt.YearFrom;
        if (from > to) (from, to) = (to, from); // захист від переплутаних меж
        var maxPages = Math.Max(1, job.MaxPages ?? _opt.MaxPages);

        // від нових до старих: свіжі релізи — найкорисніша частина free-каталогу
        var years = Enumerable.Range(from, to - from + 1).OrderByDescending(y => y).Take(maxPages).ToList();

        // кандидати без року матчаться з будь-якою сторінкою — один запит на прогін
        var nullYear = await db.Titles.AsNoTracking()
            .Where(t => t.Type == "movie" && t.Year == null)
            .Select(t => new PlexCandidate(t.Id, t.TitleEn, t.TitleUk, t.TitleRu, t.Year))
            .ToListAsync(ct);

        var matched = new List<(PlexCatalogItem Item, int TitleId)>();
        var usedTitles = new HashSet<int>();
        var pages = 0;
        var items = 0;
        var skipped = 0;

        foreach (var year in years)
        {
            ct.ThrowIfCancellationRequested();

            var html = await _client.GetYearPageHtmlAsync(year, ct);
            if (html is null)
            {
                skipped++;
                continue;
            }
            pages++;

            var pageItems = PlexParser.ParseYearPageLinks(html, year);
            items += pageItems.Count;
            if (pageItems.Count == 0)
            {
                log.LogWarning("plex: /movie-database/{Year}: жодного тайтла не розібрано — сторінка пропущена", year);
                await DelayAsync(ct);
                continue;
            }

            var candidates = await db.Titles.AsNoTracking()
                .Where(t => t.Type == "movie" && t.Year != null && t.Year >= year - 1 && t.Year <= year + 1)
                .Select(t => new PlexCandidate(t.Id, t.TitleEn, t.TitleUk, t.TitleRu, t.Year))
                .ToListAsync(ct);

            var pageMatched = MatchItems(pageItems, candidates.Concat(nullYear), year);
            foreach (var (item, titleId) in pageMatched)
                if (usedTitles.Add(titleId)) // перший слаг тайтла виграє (детерміновано за порядком сторінки)
                    matched.Add((item, titleId));

            log.LogInformation("plex: /movie-database/{Year}: тайтлів={Items}, матчів={Matched} (усього {Total})",
                year, pageItems.Count, pageMatched.Count, matched.Count);
            await DelayAsync(ct);
        }

        var deleted = await ReplaceAsync(matched, from - 1, to + 1, ct);
        var added = matched.Count;

        await UpdateSyncStateAsync(added, pages, ct);
        log.LogInformation("Plex sync завершено: сторінок={Pages}, тайтлів={Items}, матчів={Matched}, рядків {Deleted}→{Added}, пропущено сторінок={Skipped}",
            pages, items, matched.Count, deleted, added, skipped);
        return new PlexSyncResult(pages, items, matched.Count, deleted, added, skipped);
    }

    /// <summary>
    /// Матчинг сторінки: словник нормалізованих назв → найкращий кандидат (менший |Δyear|,
    /// потім менший Id) + item.Year проти року кандидата ±1 (null з будь-якого боку не блокує).
    /// Чиста функція — тестується без БД/HTTP.
    /// </summary>
    public static List<(PlexCatalogItem Item, int TitleId)> MatchItems(
        IReadOnlyList<PlexCatalogItem> items, IEnumerable<PlexCandidate> candidates, int pageYear)
    {
        var byName = new Dictionary<string, PlexCandidate>(StringComparer.Ordinal);
        foreach (var c in candidates
                     .OrderBy(c => Math.Abs((c.Year ?? pageYear) - pageYear))
                     .ThenBy(c => c.Id))
        {
            foreach (var name in Names(c))
                byName.TryAdd(name, c); // перший (найближчий за роком) виграє ключ
        }

        var matched = new List<(PlexCatalogItem, int)>();
        var usedTitles = new HashSet<int>();
        foreach (var item in items)
        {
            var key = PlexParser.NormalizeTitle(item.Title);
            if (key.Length == 0) continue;
            if (!byName.TryGetValue(key, out var c)) continue;
            if (item.Year is { } iy && c.Year is { } cy && Math.Abs(iy - cy) > 1) continue;
            if (!usedTitles.Add(c.Id)) continue;
            matched.Add((item, c.Id));
        }
        return matched;
    }

    private static IEnumerable<string> Names(PlexCandidate c)
    {
        foreach (var raw in new[] { c.TitleEn, c.TitleUk, c.TitleRu })
        {
            var n = PlexParser.NormalizeTitle(raw);
            if (n.Length > 0) yield return n;
        }
    }

    /// <summary>Рядок providers для матча: source=plex, kind=free, region=UA, url=BaseUrl+href.
    /// UX-04: мови НЕ вигадуємо — audio_langs/sub_langs лишаються NULL (каталог Plex мови
    /// озвучки не зявляє; невідома мова = NULL, ніколи не uk/ru за замовчуванням).
    /// isAvailable вердикт дає PlexAvailabilityService (перевірка «Where to Watch»), не каталог.</summary>
    public static Provider BuildProvider(int titleId, PlexCatalogItem item, string baseUrl) => new()
    {
        TitleId = titleId,
        ProviderName = ProviderName,
        Kind = Kind,
        Url = PlexParser.MovieUrl(baseUrl, item.Href),
        Source = Source,
        Region = Region,
        AudioLangs = null, // свідомо NULL: мови не вигадуємо (UX-04)
        SubLangs = null,   // свідомо NULL
        UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>Заміна рядків джерела у вікні років: DELETE source=plex (рік тайтла у вікні або null) → INSERT свіжі.</summary>
    private async Task<int> ReplaceAsync(List<(PlexCatalogItem Item, int TitleId)> matched, int from, int to, CancellationToken ct)
    {
        var stale = db.Providers
            .Where(p => p.Source == Source)
            .Where(p => db.Titles.Any(t => t.Id == p.TitleId && (t.Year == null || (t.Year >= from && t.Year <= to))));

        int deleted;
        var isNpgsql = db.Database.IsRelational()
                       && db.Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase);
        if (isNpgsql)
        {
            // серверний DELETE одним запитом (може бути 100k+ рядків за повний діапазон)
            deleted = await stale.ExecuteDeleteAsync(ct);
        }
        else
        {
            // InMemory (тести): ExecuteDelete не підтримується — materialize + RemoveRange
            var rows = await stale.ToListAsync(ct);
            db.Providers.RemoveRange(rows);
            deleted = rows.Count;
            await db.SaveChangesAsync(ct); // DELETE перед INSERT — не тригерить UNIQUE(title, provider, kind, source, region)
        }

        foreach (var batch in Batch(matched, 2000))
        {
            db.Providers.AddRange(batch.Select(b => BuildProvider(b.TitleId, b.Item, _opt.BaseUrl)));
            await db.SaveChangesAsync(ct);
        }
        return deleted;
    }

    /// <summary>Батчі пар (матч, titleId) для SaveChanges без роздутого ChangeTracker.</summary>
    internal static IEnumerable<List<(PlexCatalogItem Item, int TitleId)>> Batch(
        List<(PlexCatalogItem Item, int TitleId)> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    private Task DelayAsync(CancellationToken ct) =>
        _opt.RequestDelayMs > 0 ? Task.Delay(_opt.RequestDelayMs, ct) : Task.CompletedTask;

    private async Task UpdateSyncStateAsync(int added, int pages, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "movie", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "movie" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = added;
        state.LastPages = pages;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Фонова обробка plex-джоб (crawling year-сторінок → матч → providers).</summary>
public sealed class PlexSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<PlexSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<PlexSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PlexSyncService>();
                var result = await service.RunAsync(job, stoppingToken);
                log.LogInformation("Plex джоба завершена: сторінок={Pages}, тайтлів={Items}, матчів={Matched}, рядків {Deleted}→{Added}, пропущено={Skipped}",
                    result.Pages, result.Items, result.Matched, result.Deleted, result.Added, result.SkippedPages);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка plex-синхронізації (maxPages={MaxPages}, years={From}..{To})",
                    job.MaxPages, job.YearFrom, job.YearTo);
            }
        }
    }
}

// ─── UX-04 (хвиля 2): tri-state доступності Plex («Where to Watch» на сторінці тайтла) ───

/// <summary>Вердикт перевірки доступності Plex: available | unavailable | unknown.</summary>
public sealed record PlexAvailabilityResult(string Status, IReadOnlyList<string> Services)
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";
    public const string Unknown = "unknown";

    public static readonly PlexAvailabilityResult UnknownResult = new(Unknown, []);
}

/// <summary>
/// Парсер вердикту «Where to Watch» з SSR/RSC-розмітки сторінки тайтла (чисті функції — тести).
/// Реальна структура (проби хвилі-2, mtm_plex.html / plex_matrix.html у scratch):
///   available  — RSC-флайт містить AvailabilityCard-компоненти
///                ({"title":"Apple TV","subtitles":["Buy UAH 7.99"],…,"_component":"AvailabilityCard"});
///   unavailable — дія {"visible":true,"id":"noAvailabilities","label":"No Streaming Availability"};
///   unknown    — флайт не зібрався/ні того, ні іншого (bot-wall, змінена розмітка) — НЕ оцінюємо.
/// Картки описують пропозиції СТОРІНКИ (в т.ч. інші сервіси, гео-регіон запитувача) —
/// це ТІЛЬКИ факт «у Plex є розділ where-to-watch з пропозиціями», мови звідси НЕ беруться.
/// </summary>
public static partial class PlexAvailabilityParser
{
    public static PlexAvailabilityResult Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return PlexAvailabilityResult.UnknownResult;
        var flight = PlexParser.ParseFlightChunks(html);
        if (flight.Length == 0) return PlexAvailabilityResult.UnknownResult; // RSC не зібрався — вердикту нема

        if (flight.Contains("\"id\":\"noAvailabilities\"", StringComparison.Ordinal))
            return new PlexAvailabilityResult(PlexAvailabilityResult.Unavailable, []);

        var chunks = flight.Split("\"_component\":\"AvailabilityCard\"");
        if (chunks.Length <= 1) return PlexAvailabilityResult.UnknownResult; // ні карток, ні маркера відсутності

        var services = new List<string>();
        for (var i = 1; i < chunks.Length; i++)
        {
            var svc = LastServiceBefore(chunks[i - 1]);
            if (svc is not null && !services.Contains(svc, StringComparer.OrdinalIgnoreCase)) services.Add(svc);
        }
        return new PlexAvailabilityResult(PlexAvailabilityResult.Available, services);
    }

    /// <summary>Сервіс картки: "title":"X","subtitles": — сусідство title+subtitles є тільки в AvailabilityCard
    /// (метадата тайтла теж має "title", але без "subtitles" поруч — не хибне спрацювання).</summary>
    private static string? LastServiceBefore(string chunk)
    {
        var best = -1;
        string? value = null;
        foreach (Match m in ServiceTitle().Matches(chunk))
        {
            best = m.Index;
            value = m.Groups["svc"].Value;
        }
        return best >= 0 ? value : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"title\":\"(?<svc>[^\"]+)\",\"subtitles\":")]
    private static partial System.Text.RegularExpressions.Regex ServiceTitle();
}

/// <summary>
/// Перевірка «Where to Watch» сторінки тайтла Plex (UX-04 tri-state): isAvailable=true
/// ЛИШЕ після живої перевірки сторінки /movie/{slug}; до того — unknown («у каталозі
/// Plex, доступність неперевірена»), unknown ≠ «Безкоштовно»-бейдж.
/// Кеш ОБМЕЖЕНИЙ: TTL (PlexOptions.AvailabilityCacheMinutes, 12 год) + максимум записів
/// (AvailabilityCacheMax, prune найстаріших). robots.txt watch.plex.tv: /movie/* дозволено
/// (Disallow лише /account,/embed,/libraries,/live,/search,/watch/*, more-services/share/review).
/// Збій мережі/bot-wall → unknown (НЕ available і НЕ unavailable) — чесна невизначеність.
/// </summary>
public sealed class PlexAvailabilityService(
    HttpClient http,
    IOptions<PlexOptions> options,
    ILogger<PlexAvailabilityService> log)
{
    private sealed record Entry(DateTime At, PlexAvailabilityResult Verdict);

    private readonly PlexOptions _opt = options.Value;
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);

    /// <summary>Вердикт для title-URL (cached). Ніколи не кидає: збій → unknown.</summary>
    public async Task<PlexAvailabilityResult> GetAsync(string? url, CancellationToken ct = default)
    {
        var key = (url ?? "").Trim();
        if (key.Length == 0) return PlexAvailabilityResult.UnknownResult;

        var now = DateTime.UtcNow;
        var ttl = TimeSpan.FromMinutes(Math.Max(1, _opt.AvailabilityCacheMinutes));
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < ttl)
            return hit.Verdict;

        var verdict = await FetchAsync(key, ct);
        // unknown кешуємо на коротший TTL: могла бути транзитна помилка edge/bot-wall
        var entryTtl = verdict.Status == PlexAvailabilityResult.Unknown ? TimeSpan.FromMinutes(30) : ttl;
        _cache[key] = new Entry(now + entryTtl, verdict);
        Prune(now);
        log.LogInformation("plex availability: {Url} → {Status} ({Count} сервісів)", key, verdict.Status, verdict.Services.Count);
        return verdict;
    }

    private async Task<PlexAvailabilityResult> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Max(1000, _opt.AvailabilityTimeoutMs));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(_opt.UserAgent);
            req.Headers.Accept.ParseAdd("text/html");
            using var resp = await http.SendAsync(req, timeout.Token);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("plex availability: {Url} → HTTP {Status} — unknown", url, (int)resp.StatusCode);
                return PlexAvailabilityResult.UnknownResult; // bot-wall/edge — чесно невідомо
            }
            var html = await resp.Content.ReadAsStringAsync(timeout.Token);
            return PlexAvailabilityParser.Parse(html);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PlexAvailabilityResult.UnknownResult; // таймаут — unknown, не фейлимо /watch
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "plex availability: {Url} — помилка, unknown", url);
            return PlexAvailabilityResult.UnknownResult;
        }
    }

    /// <summary>Prune: викидаємо прострочені й найстаріші, поки не вліземо в ліміт.</summary>
    private void Prune(DateTime now)
    {
        var max = Math.Max(100, _opt.AvailabilityCacheMax);
        if (_cache.Count <= max) return;
        var expired = _cache.Where(kv => now >= kv.Value.At).Select(kv => kv.Key).Take(_cache.Count - max).ToList();
        foreach (var k in expired) _cache.TryRemove(k, out _);
        while (_cache.Count > max)
        {
            var oldest = _cache.MinBy(kv => kv.Value.At);
            if (oldest.Key is null || !_cache.TryRemove(oldest.Key, out _)) break;
        }
    }
}
