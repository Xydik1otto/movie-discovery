using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

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

    /// <summary>Рядок providers для матча: source=plex, kind=free, region=UA, url=BaseUrl+href.</summary>
    public static Provider BuildProvider(int titleId, PlexCatalogItem item, string baseUrl) => new()
    {
        TitleId = titleId,
        ProviderName = ProviderName,
        Kind = Kind,
        Url = PlexParser.MovieUrl(baseUrl, item.Href),
        Source = Source,
        Region = Region,
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
