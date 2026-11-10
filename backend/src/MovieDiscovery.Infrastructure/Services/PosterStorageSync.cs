using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Storage;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>Опції poster-storage синку (постери з poster_path у власне S3-сховище MinIO).</summary>
public sealed class PosterStorageSyncOptions
{
    /// <summary>Скільки тайтлів брати за одну вибірку-батч.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Пауза МІЖ запитами за зображеннями (ввічливість до джерел) — 500мс за контрактом.</summary>
    public int RequestDelayMs { get; set; } = 500;

    /// <summary>Максимальна кількість спроб на одне завантаження (ретраї 403/429/502/503/504).</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>База backoff БЕЗ Retry-After: base * attempt.</summary>
    public int RetryBackoffBaseMs { get; set; } = 1000;

    /// <summary>Стеля Retry-After/backoff — ≤3с за контрактом.</summary>
    public int RetryCapSeconds { get; set; } = 3;

    /// <summary>Таймаут ОДНОГО завантаження зображення — 30с за контрактом.</summary>
    public int DownloadTimeoutSeconds { get; set; } = 30;

    /// <summary>User-Agent завантажень (джерела типу Commons/AniList люблять ідентифікованих клієнтів).</summary>
    public string UserAgent { get; set; } = "KinoNavigator/1.0 (movie-discovery; poster sync to own storage)";
}

/// <summary>Джоба poster-storage синку: MaxRows — ліміт тайтлів прогону (1..500000 або null = без ліміту).</summary>
public sealed record PosterStorageSyncJob(int? MaxRows);

/// <summary>Результат прогону poster-storage синку.</summary>
public sealed record PosterStorageSyncResult(int Stored, int Skipped, int Processed);

/// <summary>
/// Фоновий синк постерів у ВЛАСНЕ сховище MinIO: тайтли з непорожнім poster_path і БЕЗ
/// poster_local (упорядковані popularity DESC) → завантажити зображення → PutAsync у бакет
/// → titles.poster_local = "/api/img/posters/{key}". Ключ = posters/{titleId:x8}{ext}, ext
/// за Content-Type (.jpg/.webp/.png; невідомий → .jpg). Ретраї 403/429/502/503/504 шанують
/// Retry-After (стеля ≤3с), таймаут запиту 30с, пауза 500мс МІЖ запитами. Порожні/4xx/фейки
/// ytimg — чесний skip з логом (йдуть іншій чистці), ідемпотентний повторний прогін добирає.
/// Запускається АДМІН-джобою (POST /api/admin/sync/poster-storage — реєстрації робить оркестратор),
/// НІКОЛИ з GET-шляхів каталогу.
/// </summary>
public sealed class PosterStorageSyncService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    IPosterStorage storage,
    ILogger<PosterStorageSyncService> log,
    IOptions<PosterStorageSyncOptions>? options = null)
{
    public const string Source = "poster-storage";

    private readonly PosterStorageSyncOptions _opt = options?.Value ?? new PosterStorageSyncOptions();

    /// <summary>Розширення за Content-Type відповіді (невідомий → .jpg — плакати практично завжди jpeg).</summary>
    public static string ExtensionForContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return ".jpg";
        var t = contentType.ToLowerInvariant();
        if (t.Contains("jpeg") || t.Contains("jpg")) return ".jpg";
        if (t.Contains("webp")) return ".webp";
        if (t.Contains("png")) return ".png";
        return ".jpg";
    }

    /// <summary>Валідація MaxRows: null або 1..500000 (за контрактом).</summary>
    public static bool IsValidMaxRows(int? maxRows) => maxRows is null or (>= 1 and <= 500_000);

    /// <summary>
    /// poster_path → URL для завантаження. Абсолютні http(s) — придатні (Commons/shikimori/anilist);
    /// TMDB-шляхи ("/abc.jpg") НЕ качаємо (TMDB відхилено власником — джерела нема), ytimg-фейки
    /// відфільтровані ще на вибірці; порожнє/не-URL → null (чесний skip).
    /// </summary>
    public static string? ResolveSourceUrl(string? posterPath)
    {
        if (string.IsNullOrWhiteSpace(posterPath)) return null;
        var p = posterPath.Trim();
        return p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? p
            : null;
    }

    /// <summary>Ключ об'єкта у сховищі: posters/{titleId:x8}{ext} (за контрактом).</summary>
    public static string StorageKeyFor(int titleId, string ext) => $"posters/{titleId:x8}{ext}";

    /// <summary>Публічний API-шлях: /api/img/{key} = /api/img/posters/00000001.jpg (маршрут PosterImagesController).</summary>
    public static string PublicUrlFor(string key) => $"/api/img/{key}";

    /// <summary>
    /// Ретрай-пауза: Retry-After від джерела шануємо ДО стелі (≤3с — контракти квот);
    /// без заголовка — backoff base*attempt, теж обрізаний стелею.
    /// </summary>
    internal static TimeSpan RetryDelay(TimeSpan? retryAfterDelta, TimeSpan fallback, TimeSpan cap)
    {
        if (retryAfterDelta is { } d && d >= TimeSpan.Zero)
            return d > cap ? cap : d;
        return fallback > cap ? cap : fallback;
    }

    /// <summary>Повний прогін: вибірка кандидатів → завантаження → PutAsync → poster_local → sync_state.</summary>
    public async Task<PosterStorageSyncResult> RunAsync(int? maxRows, CancellationToken ct = default)
    {
        if (!IsValidMaxRows(maxRows))
            throw new ArgumentOutOfRangeException(nameof(maxRows), maxRows, "maxRows має бути в діапазоні 1..500000 або null.");

        if (!storage.Enabled)
        {
            // Fail-closed: сховище вимкнене — ніяких HTTP-запитів і ніяких PUT (чесно й одразу).
            log.LogInformation("poster-storage: сховище вимкнено (Enabled=false) — прогін пропущено без запитів");
            return new PosterStorageSyncResult(0, 0, 0);
        }

        // Кандидати матеріалізуємо ОДИН разів (≤9k рядків: poster_path непорожній лише у 9001 із 2.86 млн).
        // TRACKED-вибірка повних сутностей: мутуємо PosterLocal прямо на них (Attach-патч конфліктував би
        // з уже отрендженими інстансами), а сіп-рядки (4xx/таймаут) лишаються poster_local=null — повторна
        // TOP-вибірка зациклилась би, тож курсор іде по матеріалізованому списку.
        var candidates = await db.Titles
            .Where(t => t.PosterPath != null
                        && t.PosterPath != ""
                        && t.PosterLocal == null
                        && !t.PosterPath.Contains(PosterFakeCleanupService.YtimgMarker)) // ytimg-фейки — чужа чистка
            .OrderByDescending(t => t.Popularity)
            .ThenBy(t => t.Id)
            .ToListAsync(ct);

        var stored = 0;
        var skipped = 0;
        var processed = 0;
        var batches = 0;

        for (var offset = 0; offset < candidates.Count; offset += _opt.BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            if (maxRows is { } cap && processed >= cap) break;
            batches++;

            var batch = candidates.Skip(offset).Take(_opt.BatchSize);
            foreach (var t in batch)
            {
                if (maxRows is { } cap2 && processed >= cap2) break;

                var url = ResolveSourceUrl(t.PosterPath);
                if (url is null)
                {
                    skipped++;
                    log.LogInformation("poster-storage: title={TitleId} — poster_path не є http(s)-URL, skip (чесний лог)", t.Id);
                    continue;
                }

                processed++;
                var (ok, bytes, contentType) = await DownloadAsync(url, ct);
                if (!ok || bytes is null || bytes.Length == 0)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var ext = ExtensionForContentType(contentType);
                    var key = StorageKeyFor(t.Id, ext);
                    await using var ms = new MemoryStream(bytes);
                    await storage.PutAsync(key, ms, contentType ?? "image/jpeg", ct);

                    t.PosterLocal = PublicUrlFor(key); // точковий UPDATE poster_local (одна змінена властивість)
                    await db.SaveChangesAsync(ct);
                    stored++;
                    log.LogInformation("poster-storage: title={TitleId} → {Key} ({Bytes} B, {Type})", t.Id, key, bytes.Length, contentType);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skipped++;
                    log.LogWarning(ex, "poster-storage: title={TitleId} — не вдалося зберегти постер, skip (ідемпотентний повторний прогін добере)", t.Id);
                }

                // Пауза МІЖ запитами (500мс) — ввічливість до зовнішніх джерел зображень.
                await Task.Delay(_opt.RequestDelayMs, ct);
            }

            log.LogInformation("poster-storage: батч #{Batches}: {Count} тайтлів (stored={Stored}, skipped={Skipped})",
                batches, Math.Min(_opt.BatchSize, candidates.Count - offset), stored, skipped);
        }

        await UpdateSyncStateAsync(stored, processed, ct);
        log.LogInformation("Poster-storage sync завершено: stored={Stored}, skipped={Skipped}, processed={Processed}",
            stored, skipped, processed);
        return new PosterStorageSyncResult(stored, skipped, processed);
    }

    /// <summary>
    /// Одне завантаження з ретраями 403/429/502/503/504 (Retry-After ≤3с), таймаут запиту 30с,
    /// інші 4xx/5xx — чесний skip без ретраїв (не транзитні).
    /// </summary>
    private async Task<(bool Ok, byte[]? Bytes, string? ContentType)> DownloadAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(_opt.UserAgent);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_opt.DownloadTimeoutSeconds)); // таймаут 30с/запит

                using var resp = await http.SendAsync(req, timeout.Token);
                var code = (int)resp.StatusCode;

                if (code is 403 or 429 or 502 or 503 or 504)
                {
                    if (attempt >= _opt.MaxAttempts)
                    {
                        log.LogWarning("poster-storage: {Url} → {Status} після {Attempts} спроб — skip", url, code, attempt);
                        return (false, null, null);
                    }
                    var delay = RetryDelay(
                        resp.Headers.RetryAfter?.Delta,
                        TimeSpan.FromMilliseconds((long)_opt.RetryBackoffBaseMs * attempt),
                        TimeSpan.FromSeconds(_opt.RetryCapSeconds));
                    log.LogWarning("poster-storage: {Url} → {Status} — пауза {Delay}s (спроба {Attempt}/{Max})",
                        url, code, delay.TotalSeconds, attempt, _opt.MaxAttempts);
                    await Task.Delay(delay, ct);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    log.LogInformation("poster-storage: {Url} → {Status} — skip (не транзитна помилка)", url, code);
                    return (false, null, null);
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(timeout.Token);
                if (bytes.Length == 0)
                {
                    log.LogInformation("poster-storage: {Url} → порожнє тіло — skip (чесний лог)", url);
                    return (false, null, null);
                }

                var contentType = resp.Content.Headers.ContentType?.MediaType;
                return (true, bytes, contentType);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Таймаут 30с (не скасування джоби) — skip без ретраю.
                log.LogWarning("poster-storage: {Url} — таймаут {Seconds}s — skip", url, _opt.DownloadTimeoutSeconds);
                return (false, null, null);
            }
        }
    }

    private async Task UpdateSyncStateAsync(int stored, int processed, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "original", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "original" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = stored;
        state.LastPages = processed;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Черга poster-storage-джоб — копія патерну WikipediaPosterSyncQueue (bounded, DropOldest).</summary>
public sealed class PosterStorageSyncQueue
{
    private readonly System.Threading.Channels.Channel<PosterStorageSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<PosterStorageSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(PosterStorageSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<PosterStorageSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка poster-storage-джоб (завантаження постерів у MinIO → poster_local).</summary>
public sealed class PosterStorageSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<PosterStorageSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<PosterStorageSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<PosterStorageSyncService>();
                var result = await service.RunAsync(job.MaxRows, stoppingToken);
                log.LogInformation("Poster-storage джоба завершена: stored={Stored}, skipped={Skipped}, processed={Processed}",
                    result.Stored, result.Skipped, result.Processed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка poster-storage синку (maxRows={MaxRows})", job.MaxRows);
            }
        }
    }
}
