using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using Npgsql;

namespace MovieDiscovery.Infrastructure.YouTube;

/// <summary>Джоба трейлер-синку (UX-05): топ-N тайтлів без трейлер-кандидатів → search → title_trailers.</summary>
public sealed record TitleTrailerSyncJob(int Limit);

/// <summary>Черга трейлер-синку.</summary>
public sealed class TitleTrailerSyncQueue
{
    private readonly System.Threading.Channels.Channel<TitleTrailerSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<TitleTrailerSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(TitleTrailerSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<TitleTrailerSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка трейлер-джоб (search+матч+верифікація → title_trailers).</summary>
public sealed class TitleTrailerSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TitleTrailerSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<TitleTrailerSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TitleTrailerSyncService>();
                if (!service.IsConfigured)
                {
                    log.LogWarning("YOUTUBE_API_KEY не задано — трейлер-синхронізація пропущена (dormant)");
                    continue;
                }
                var added = await service.RunAsync(job.Limit, stoppingToken);
                log.LogInformation("Трейлер-джоба завершена: тайтлів={Titles}, кандидатів={Added}",
                    job.Limit, added);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка трейлер-синхронізації (limit={Limit})", job.Limit);
            }
        }
    }
}

/// <summary>
/// Незалежний трейлер-шлях (UX-05): НЕ проходить через EnsureYoutubeLinkAsync/short-circuit
/// watch-шляху і не залежить від watch_links. На тайтл — 1 search (~100 units) + 1× videos.list
/// (1 unit) + oEmbed-верифікація (безкоштовно). Запит — натуральна фраза «{назва uk|en} трейлер
/// українською» (доведено ефективнішою за «{назва} {рік} full movie»: той запит давав 0 трейлерів).
/// Зберігаються ВСІ кандидати, що пройшли фільтри (не лише перший), кожен з мовою/офіційністю/статусом.
/// </summary>
public sealed class TitleTrailerSyncService(
    YoutubeClient client,
    TrailerVerifier verifier,
    MovieDiscoveryDbContext db,
    ICacheStore cache,
    IOptions<YoutubeOptions> options,
    ILogger<TitleTrailerSyncService> log,
    YouTubeDailySearchQuota? quota = null)
{
    private readonly YoutubeOptions _opt = options.Value;

    public bool IsConfigured => client.IsConfigured;

    /// <summary>Натуральна фраза пошуку трейлера (uk-назва пріоритетна, фолбек en).</summary>
    public static string BuildQuery(Title t)
    {
        var name = (!string.IsNullOrWhiteSpace(t.TitleUk) ? t.TitleUk! : t.TitleEn ?? "").Trim();
        if (name.Length == 0) return "";
        return $"{name} трейлер українською".Trim();
    }

    /// <summary>
    /// Батч-синк: топ-N тайтлів БЕЗ жодних трейлер-рядків (манга — не відео-контент)
    /// за popularity → search+фільтри+матч+верифікація → title_trailers.
    /// Квота-помилка API — м'яко зупиняє прогін (як у watch-синку).
    /// </summary>
    public async Task<int> RunAsync(int limit, CancellationToken ct = default)
    {
        if (!IsConfigured) return 0;

        var withRows = db.TitleTrailers.Select(x => x.TitleId).Distinct();
        var ids = await db.Titles.AsNoTracking()
            .Where(t => t.Type != "manga" && !withRows.Contains(t.Id))
            .OrderByDescending(t => t.Popularity)
            .ThenByDescending(t => t.Year ?? 0)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(t => t.Id)
            .ToListAsync(ct);

        var added = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var title = await db.Titles.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (title is null) continue;
                added += await SyncOneAsync(title, ct);
            }
            catch (YoutubeApiException ex)
            {
                log.LogWarning("Трейлер-синк зупинено (title {Id}): {Msg}", id, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка трейлер-синку тайтла {Id}", id);
            }
        }

        await UpdateSyncStateAsync(added, ids.Count, ct);
        log.LogInformation("Трейлер-синк завершено: тайтлів {Titles}, кандидатів {Added}", ids.Count, added);
        return added;
    }

    /// <summary>On-demand синк ОДНОГО тайтла (GET /api/titles/{id}/trailers, коли кандидатів нема).</summary>
    public async Task<int> SyncTitleAsync(int titleId, CancellationToken ct = default)
    {
        if (!IsConfigured) return 0;
        var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == titleId, ct);
        if (title is null) return 0;
        // Денний ГЛОБАЛЬНИЙ ліміт search-викликів (90/день) — on-demand-шлях (GET .../trailers):
        // гейт ПІСЛЯ дешевих виходів (не налаштований/тайтл нема) і ПЕРЕД search-спробою.
        // Перевищення → YouTubeQuotaExceededException (не YoutubeApiException! — м'який catch
        // нижче не проковтує, контролер мапить на 429 quota_exceeded). Батч RunAsync йде
        // напряму в SyncOneAsync і гейт минає (адмінський прогін не блокуємо денним лімітом).
        // quota == null (пряме new у тестах) → без ліміту.
        if (quota is not null && !quota.TryConsume())
            throw new YouTubeQuotaExceededException();
        try
        {
            return await SyncOneAsync(title, ct);
        }
        catch (YoutubeApiException ex)
        {
            log.LogWarning("On-demand трейлер-пошук недоступний (title {Id}): {Msg}", titleId, ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Single-flight (review Codex IV-05): конкурентні SyncOneAsync на ТОЙ САМИЙ тайтл
    /// (окремі екземпляри сервісу = окремі HTTP-запити) виконують пошук ОДИН раз —
    /// другий чекає на той самий Task, без подвійної квоти і без краху на UNIQUE.
    /// Статичний — спільний для всіх екземплярів класу в межах процесу.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<Task<int>>> InFlight = new();

    /// <summary>Search → фільтри → верифікація → upsert УСІХ кандидатів. Повертає кількість ok-кандидатів.</summary>
    public async Task<int> SyncOneAsync(Title title, CancellationToken ct = default)
    {
        var lazy = InFlight.GetOrAdd(title.Id,
            _ => new Lazy<Task<int>>(() => SyncOneCoreAsync(title, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        finally
        {
            // знімаємо КЛЮЧ лише якщо це ще наш lazy (інший запит міг уже поставити свій)
            ((System.Collections.Generic.ICollection<KeyValuePair<int, Lazy<Task<int>>>>)InFlight)
                .Remove(new KeyValuePair<int, Lazy<Task<int>>>(title.Id, lazy));
        }
    }

    private async Task<int> SyncOneCoreAsync(Title title, CancellationToken ct)
    {
        var query = BuildQuery(title);
        if (query.Length == 0) return 0;

        var items = await SearchTrailersCachedAsync(query, ct);
        if (items.Count == 0) return 0;

        // 1) дешевий локальний фільтр за snippet (трейлер-слово, шум, назва+рік) — до витрат на API
        var prefiltered = items
            .Where(i => TrailerCandidateFilter.IsTrailerCandidate(i.Title, i.Description))
            .Where(i => TrailerCandidateFilter.MatchesAnyName(title, i.Title, i.Description))
            .ToList();
        if (prefiltered.Count == 0) return 0;

        // 2) верифікація: 1× videos.list(status) + oEmbed (безкоштовно)
        var verified = await verifier.VerifyAsync(prefiltered.Select(i => i.VideoId).ToList(), ct);

        var added = 0;
        foreach (var v in verified)
        {
            // 3) тривалість ≤5 хв — лише коли duration відомий (gone-кандидати без даних
            //    зберігаємо як gone: чесна документація «був кандидат — теперь недоступний»)
            if (v.Exists && !TrailerCandidateFilter.DurationOk(YoutubeParser.ParseDuration(v.Duration))) continue;

            var status = v.VerifiedStatus;
            var lang = TrailerLang.Detect(v.DefaultAudioLanguage, v.DefaultLanguage, v.Title, v.Description, v.ChannelTitle);
            var official = TrailerChannelAllowlist.IsOfficialChannel(v.ChannelTitle)
                           || IsOfficialPhrase(v.Title, v.Description);

            if (await UpsertAsync(title, v.YtId, lang, official, "youtube", status, ct)) added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        log.LogInformation("Трейлер-синк title={TitleId}: кандидатів {Total}, збережено {Added}",
            title.Id, verified.Count, added);
        return added;
    }

    /// <summary>«official»/«офіційний» + трейлер-слово у назві/описі → офіційний (не лише allowlist).</summary>
    private static bool IsOfficialPhrase(string title, string description)
    {
        var text = $"{title} {description}";
        var official = text.Contains("official", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("офіційний", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("официальный", StringComparison.OrdinalIgnoreCase);
        return official && TrailerCandidateFilter.IsTrailerCandidate(title, description);
    }

    /// <summary>search.list без videoDuration-фільтру (трейлери короткі), relevanceLanguage=uk — м'яка підказка.</summary>
    private async Task<IReadOnlyList<YoutubeSearchItem>> SearchTrailersCachedAsync(string query, CancellationToken ct)
    {
        var key = $"search-trailer:{query.Trim().ToLowerInvariant()}";
        var cached = await cache.GetAsync("youtube", key, ct);
        if (cached is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<List<YoutubeSearchItem>>(cached) ?? [];
            }
            catch (JsonException)
            {
                // протухлий формат кешу — перезапитуємо
            }
        }

        var items = await client.SearchTrailersAsync(query, _opt.TrailerMaxResults, "uk", ct);
        await cache.SetAsync("youtube", key, JsonSerializer.Serialize(items), _opt.CacheTtlSeconds, ct);
        return items;
    }

    /// <summary>
    /// Унікальність порушено: Postgres 23505 (Npgsql) або "unique"/"duplicate key" у ланцюжку
    /// повідомлень. InMemory (тести) кидає InvalidOperationException, Npgsql — DbUpdateException
    /// → фільтруємо ВЕСЬ ланцюжок InnerException, а не лише тип обгортки.
    /// </summary>
    public static bool IsUniqueViolation(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: "23505" }) return true;
            var msg = e.Message;
            if (msg.Contains("unique", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Ідемпотентний upsert (title_id, yt_id). Нова верифікація НЕ затирає кращий старий статус:
    /// ok лишається ok; gone оновлюється лише з gone; NULL (не верифіковано) перезаписується.
    /// </summary>
    private async Task<bool> UpsertAsync(
        Title title, string ytId, string? lang, bool official, string source, string? verifiedStatus, CancellationToken ct)
    {
        var existing = await db.TitleTrailers
            .FirstOrDefaultAsync(x => x.TitleId == title.Id && x.YtId == ytId, ct);

        if (existing is not null)
        {
            var upgraded = false;
            if (lang is not null && existing.Lang is null) { existing.Lang = lang; upgraded = true; }
            if (official && !existing.Official) { existing.Official = true; upgraded = true; }
            if (verifiedStatus is not null && existing.VerifiedStatus != "ok")
            {
                existing.VerifiedStatus = verifiedStatus;
                existing.VerifiedAt = DateTime.UtcNow;
                upgraded = true;
            }
            if (!upgraded) return false;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Трейлер-кандидат оновлено: title={TitleId}, video={YtId}, lang={Lang}, status={Status}",
                title.Id, ytId, lang, verifiedStatus);
            return existing.VerifiedStatus == "ok" || existing.VerifiedStatus is null;
        }

        var newEntry = new TitleTrailer
        {
            TitleId = title.Id,
            YtId = ytId,
            Lang = lang,
            Official = official,
            Source = source,
            VerifiedAt = verifiedStatus is null ? null : DateTime.UtcNow,
            VerifiedStatus = verifiedStatus,
        };
        db.TitleTrailers.Add(newEntry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Рев'ю Codex: гонка двох синків на той самий (title_id, yt_id) — UNIQUE
            // замикає захист; конкурентний рядок вже існує, дубль не потрібен.
            db.Entry(newEntry).State = EntityState.Detached;
            log.LogDebug("Трейлер-кандидат уже записано конкурентно: title={TitleId}, video={YtId}", title.Id, ytId);
            return false;
        }
        log.LogInformation("Трейлер-кандидат додано: title={TitleId}, video={YtId}, lang={Lang}, official={Official}, status={Status}",
            title.Id, ytId, lang, official, verifiedStatus);
        return verifiedStatus == "ok" || verifiedStatus is null;
    }

    /// <summary>sync_state source=youtube-trailers kind=sync (прогрес батч-синку).</summary>
    private async Task UpdateSyncStateAsync(int added, int titles, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == "youtube-trailers" && s.Kind == "sync", ct);
        if (state is null)
        {
            state = new SyncState { Source = "youtube-trailers", Kind = "sync" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = added;
        await db.SaveChangesAsync(ct);
    }
}
