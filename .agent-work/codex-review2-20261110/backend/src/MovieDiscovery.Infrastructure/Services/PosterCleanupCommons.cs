using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

/// <summary>Результат прогону чистки wrong-Commons постерів.</summary>
public sealed record CommonsCleanupResult(int Candidates, int Confirmed, int Nulled, int Skipped);

/// <summary>
/// IV-09: одноразова адмін-джоба заміни ПІДТВЕРДЖЕНИХ wrong-Commons постерів (НЕ масове видалення
/// Commons-зображень). Джерело дефекту — попередній WikidataNamesSync (P18 → poster_url): P18 може
/// бути логотипом/іконкою, а не постером (реальний приклад: title 581458 Star Trek Nemesis (2002),
/// poster_path = Star_Trek_Nemesis_Logo_2.webp). Fill-only синк wikipedia-posters НЕ чіпає непорожні,
/// тому legacy-логотип пережив би pipeline.
///
/// Правило кандидата (документоване, консервативне — див. звіт UX-02):
///   1) poster_path містить wikimedia.org І
///   2) ім'я файлу містить «logo» або «icon» (без урахування регістру) АБО має розширення .svg
/// Верифікація: усі кандидати проганяються через Commons imageinfo (mediatype; батчі ≤50, пауза
/// ≥700мс) — SVG/DRAWING підтверджується метаданими; logo/icon-імена — рішучі й без API (API-лог
/// додає доказ у журнал). Підтверджені → poster_path=NULL (наступний fill-only прогін
/// wikipedia-posters запише справжній постер, або фронт покаже чесну заглушку).
/// Не-кандидати (фотопости, трейлерні кадри, .webp без logo-імені) НЕ чіпаються — обмеження
/// зафіксоване в звіті. Ідемпотентність: повторний прогін не знаходить кандидатів → 0.
/// Запускається АДМІН-джобою (POST /api/admin/sync/posters-cleanup — код у звіті), не з GET-шляхів.
/// </summary>
public sealed class CommonsWrongPosterCleanupService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    ILogger<CommonsWrongPosterCleanupService> log)
{
    public const string Source = "posters-cleanup";

    public const string WikimediaMarker = "wikimedia.org";
    public const string LogoMarker = "logo";
    public const string IconMarker = "icon";
    public const string SvgMarker = ".svg";

    /// <summary>MediaWiki batch size (Commons API дозволяє ≤50 titles на запит).</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Пауза між запитами до Commons (ввічливість ≥700мс).</summary>
    public int RequestDelayMs { get; set; } = 700;

    /// <summary>Ім'я файлу з poster_path (останній сегмент шляху, без query, URL-decoded).</summary>
    public static string? FileName(string? posterUrl)
    {
        if (string.IsNullOrWhiteSpace(posterUrl)) return null;
        var path = posterUrl.Split('?', '#')[0];
        var file = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]).Trim();
        return file.Length > 0 ? file : null;
    }

    /// <summary>Кандидат за правилом імені (logo/icon/svg) — без звернення до API.</summary>
    public static bool MatchesNameRule(string? posterUrl)
    {
        var file = FileName(posterUrl)?.ToLowerInvariant();
        if (file is null) return false;
        return file.Contains(LogoMarker) || file.Contains(IconMarker)
            || file.EndsWith(SvgMarker) || file.EndsWith(".svgz");
    }

    /// <summary>
    /// Повний прогін: кандидати → Commons imageinfo (mediatype) → підтверджені → poster_path=NULL.
    /// </summary>
    public async Task<CommonsCleanupResult> RunAsync(int? maxRows, CancellationToken ct = default)
    {
        var candidates = await db.Titles
            .Where(t => t.PosterPath != null &&
                        t.PosterPath.Contains(WikimediaMarker) &&
                        (t.PosterPath.Contains(LogoMarker) ||
                         t.PosterPath.Contains(IconMarker) ||
                         t.PosterPath.Contains(SvgMarker)))
            .OrderBy(t => t.Id)
            .ToListAsync(ct);
        if (maxRows is { } cap && candidates.Count > cap)
            candidates = candidates.Take(cap).ToList();

        var confirmed = new List<Title>();
        var mediatypes = await VerifyMediatypesAsync(
            candidates.Where(c => !MatchesNameRule(c.PosterPath)).ToList(), ct);

        foreach (var c in candidates)
        {
            // Рішення: logo/icon-імена — рішучі; SVG — за mediatype=DRAWING з Commons;
            // файл без верифікації (API-збій) НЕ занулюємо — консервативно.
            if (MatchesNameRule(c.PosterPath) || mediatypes.GetValueOrDefault(FileName(c.PosterPath) ?? "") == "DRAWING")
                confirmed.Add(c);
            else
                log.LogInformation("posters-cleanup: кандидат НЕ підтверджено — лишаємо (id={Id}, file={File}, mediatype={Mt})",
                    c.Id, FileName(c.PosterPath), mediatypes.GetValueOrDefault(FileName(c.PosterPath) ?? ""));
        }

        foreach (var c in confirmed)
            c.PosterPath = null; // metadata-missing → наступний fill-only прогін запише справжній постер
        if (confirmed.Count > 0)
            await db.SaveChangesAsync(ct);

        await WriteSyncStateAsync(candidates.Count, confirmed.Count, ct);
        log.LogInformation("posters-cleanup (wrong-Commons): candidates={Candidates}, confirmed={Confirmed}, nulled={Nulled}",
            candidates.Count, confirmed.Count, confirmed.Count);
        return new CommonsCleanupResult(candidates.Count, confirmed.Count, confirmed.Count, candidates.Count - confirmed.Count);
    }

    /// <summary>Commons imageinfo (mediatype) для кандидатів БЕЗ іменного правила; батчі ≤50, пауза ≥700мс, ретраї 429/503.</summary>
    internal async Task<Dictionary<string, string>> VerifyMediatypesAsync(List<Title> needVerify, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // File:назва → filename

        for (var offset = 0; offset < needVerify.Count; offset += BatchSize)
        {
            var chunk = needVerify.Skip(offset).Take(BatchSize).ToList();
            var titles = new List<string>();
            foreach (var c in chunk)
            {
                var file = FileName(c.PosterPath);
                if (file is null) continue;
                titles.Add($"File:{file}");
                fileByTitle[$"File:{file}"] = file;
            }
            if (titles.Count == 0) continue;

            try
            {
                var json = await FetchImageInfoAsync(titles, ct);
                foreach (var (file, mt) in ParseMediatypes(json, fileByTitle))
                    result[file] = mt;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Верифікація не взялась навіть після ретраїв → ці кандидати НЕ підтверджені
                // метаданими; их не NULL-имо (без іменного правила немає підстав) —_logged_ межа.
                log.LogWarning(ex, "posters-cleanup: Commons imageinfo батч ({Count}) не взявся — кандидати лишаються", titles.Count);
            }
            await Task.Delay(RequestDelayMs, ct);
        }
        return result;
    }

    private async Task<string> FetchImageInfoAsync(List<string> titles, CancellationToken ct)
    {
        var url = "https://commons.wikimedia.org/w/api.php?action=query&format=json&formatversion=2" +
                  "&prop=imageinfo&iiprop=mediatype%7Cmime&titles=" +
                  string.Join("%7C", titles.Select(Uri.EscapeDataString));
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("KinoNavigator/1.0 (movie-discovery; contact via repo issues)");
            using var resp = await http.SendAsync(req, ct);
            if ((int)resp.StatusCode is 429 or 503 or 502 or 504)
            {
                if (attempt >= maxAttempts)
                    throw new HttpRequestException($"Commons imageinfo {resp.StatusCode} після {attempt} спроб");
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30 * attempt, 60));
                await Task.Delay(retryAfter, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }

    /// <summary>Парсер imageinfo: pages[].imageinfo[0].mediatype → filename (missing сторінки пропускаються).</summary>
    public static Dictionary<string, string> ParseMediatypes(string json, Dictionary<string, string> fileByTitle)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var page in arr.EnumerateArray())
        {
            if (page.TryGetProperty("missing", out _)) continue;
            var title = page.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (title is null || !fileByTitle.TryGetValue(title, out var file)) continue;
            if (!page.TryGetProperty("imageinfo", out var ii) || ii.ValueKind != JsonValueKind.Array ||
                ii.GetArrayLength() == 0) continue;
            var mt = ii[0].TryGetProperty("mediatype", out var m) ? m.GetString() : null;
            if (!string.IsNullOrEmpty(mt)) result[file] = mt!;
        }
        return result;
    }

    private async Task WriteSyncStateAsync(int candidates, int nulled, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "original", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "original" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = nulled;
        state.LastPages = candidates;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Джоба чистки постерів: фейкові маркери (ytimg/missing_original) + підтверджені wrong-Commons.</summary>
public sealed record PosterCleanupJob(int? MaxRows);

/// <summary>Черга posters-cleanup.</summary>
public sealed class PosterCleanupQueue
{
    private readonly System.Threading.Channels.Channel<PosterCleanupJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<PosterCleanupJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(PosterCleanupJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<PosterCleanupJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Фонова обробка posters-cleanup-джоб: спершу фейкові маркери (ytimg/missing_original),
/// потім підтверджені wrong-Commons (логотипи/іконки/SVG). Кожен сервіс у власному scope.
/// </summary>
public sealed class PosterCleanupBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<PosterCleanupBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<PosterCleanupQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var fake = scope.ServiceProvider.GetRequiredService<PosterFakeCleanupService>();
                var fakeRes = await fake.RunAsync(stoppingToken);
                log.LogInformation("posters-cleanup: фейкові маркери — cleaned={Cleaned} (ytimg={Yt}, missing={Missing})",
                    fakeRes.Cleaned, fakeRes.Ytimg, fakeRes.MissingOriginal);

                var commons = scope.ServiceProvider.GetRequiredService<CommonsWrongPosterCleanupService>();
                var commonsRes = await commons.RunAsync(job.MaxRows, stoppingToken);
                log.LogInformation("posters-cleanup: wrong-Commons — candidates={C}, confirmed={N}, nulled={Nulled}, skipped={Skipped}",
                    commonsRes.Candidates, commonsRes.Confirmed, commonsRes.Nulled, commonsRes.Skipped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка posters-cleanup (maxRows={MaxRows})", job.MaxRows);
            }
        }
    }
}
