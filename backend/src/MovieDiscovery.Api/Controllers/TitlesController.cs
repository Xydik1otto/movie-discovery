using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Tmdb;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/title")]
public sealed class TitlesController(
    TitleService titles,
    SimilarityService similarity,
    MovieDiscoveryDbContext db,
    IServiceProvider sp, // on-demand-сервіси резолвимо м'яко: до реєстрації в Program.cs GetService → null (без 500)
    ILogger<TitlesController> log) : ControllerBase
{
    /// <summary>Кап очікування on-demand резолву постера — відповідь не блокується довше 1.5с.</summary>
    internal const int OnDemandPosterTimeoutMs = 1500;

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, [FromQuery] string? lang, CancellationToken ct)
    {
        var langSafe = SearchController.Lang(lang);
        var detail = await titles.GetDetailAsync(id, langSafe, ct);
        if (detail is null)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        var t = detail.Title;
        var card = TitleMapper.ToCard(t, langSafe);

        // On-demand доціплення постера/бекдропа (джерело №1 — TMDB): якщо в БД порожньо і є
        // tmdb_id — резолвимо (кеш деталей → один легкий GET), НЕ блокуючи відповідь довше 1.5с;
        // в ОБОХ випадках ставимо точкову фонову джобу, яка оновить БД (черга poster-source-sync).
        var (posterOverride, backdropOverride) = await ResolvePosterOnDemandAsync(t, id, ct);

        var uk = SimilarityTexts.IsUk(langSafe);
        // 10.11: ru — повноцінна мова: повний опис ru → uk → en (фолбек як в Overview картки).
        var isRu = string.Equals(langSafe, "ru", StringComparison.Ordinal);
        return Ok(new TitleDetailResponse
        {
            Id = card.Id,
            TmdbId = card.TmdbId,
            Type = card.Type,
            Title = card.Title,
            OriginalTitle = card.OriginalTitle,
            Year = card.Year,
            Genres = card.Genres,
            Countries = card.Countries,
            RatingTmdb = card.RatingTmdb,
            RatingImdb = card.RatingImdb,
            PosterUrl = posterOverride ?? card.PosterUrl,
            RuntimeMin = card.RuntimeMin,
            Seasons = card.Seasons,
            Overview = card.Overview,
            Actors = [.. t.Actors],
            Director = t.Director,
            Keywords = [.. t.Keywords],
            OverviewFull = isRu
                ? (t.OverviewRu ?? t.OverviewUk ?? t.OverviewEn)
                : uk ? (t.OverviewUk ?? t.OverviewEn) : t.OverviewEn,
            BackdropUrl = backdropOverride ?? TitleMapper.ImgUrl(t.BackdropPath, "w780"),
            TrailerYtId = t.TrailerYtId,
            Providers = detail.Providers
                .Select(p => new ProviderInfo(p.ProviderName, p.Kind, p.Url, p.Source))
                .ToList(),
            // FR-14: компактні бейджі «де подивитись» (лише is_available=true)
            WatchProviders = (await Infrastructure.Watchability.WatchLinkLoader.LoadAvailableAsync(db, [id], ct))
                .TryGetValue(id, out var wp)
                ? wp.Select(w => new CardWatchProvider(w.Provider, w.Kind, w.Url)).ToList()
                : [],
            Status = t.Status,
            OriginalLanguage = t.OriginalLanguage,
            // UX-05: кандидати трейлерів (ok + не-верифіковані; не-ok синк-рядки не віддаються).
            // On-demand search тут НЕ запускається (квота) — лише GET /api/titles/{id}/trailers.
            Trailers = TrailersController.ToInfos(
                await TrailersController.LoadRowsAsync(db, id, ct)),
            // аніме/манга (FR-12)
            Format = card.Format,
            Episodes = card.Episodes,
            Chapters = card.Chapters,
            Volumes = card.Volumes,
            Studios = card.Studios,
            Authors = card.Authors,
            ScoreMal = card.ScoreMal,
            ScoreAnilist = card.ScoreAnilist,
            ScoreShikimori = card.ScoreShikimori,
        });
    }

    /// <summary>
    /// On-demand доціплення постера/бекдропа з TMDB (fill-only): умова — у БД порожній постер
    /// АБО бекдроп і є tmdb_id. Резолв = кеш деталей → один легкий GET; чекаємо максимум
    /// OnDemandPosterTimeoutMs (Task.WhenAny) — що встигли, те підставляється у відповідь;
    /// у ОБОХ випадках (встиг/не встиг) ставимо точкову фонову джобу poster-source-sync,
    /// яка оновить БД. Джерело не сконфігуроване/не зареєстроване → тихий no-op (без 500).
    /// </summary>
    private async Task<(string? PosterUrl, string? BackdropUrl)> ResolvePosterOnDemandAsync(
        Core.Domain.Title t, int titleId, CancellationToken ct)
    {
        var posterMissing = string.IsNullOrWhiteSpace(t.PosterPath);
        var backdropMissing = string.IsNullOrWhiteSpace(t.BackdropPath);
        if (!posterMissing && !backdropMissing) return (null, null);
        var hasTmdb = t.TmdbId is int;
        var hasImdb = !string.IsNullOrWhiteSpace(t.ImdbId);
        if (!hasTmdb && !hasImdb) return (null, null);

        var images = sp.GetService<ITmdbImagesService>();
        if (images is null || !images.IsConfigured)
            return (null, null);

        try
        {
            // В ОБОХ випадках (встиг/не встиг за кап) — точкова фонова джоба: догорне в черзі
            // poster-source-sync і fill-only оновить БД (якщо вже записали — ранній вихід без HTTP).
            sp.GetService<PosterSourceSyncQueue>()?.TryEnqueue(new PosterSourceSyncJob(MaxRows: null, TitleId: titleId));

            var resolveTask = hasTmdb
                ? images.ResolveAsync(t.TmdbId!.Value, t.Type, ct).ContinueWith(
                    static r => r.Result is null ? null : new TmdbImdbResolved(int.MinValue, r.Result.PosterPath, r.Result.BackdropPath),
                    TaskScheduler.Default)
                : images.ResolveByImdbAsync(t.ImdbId!, t.Type, ct);
            var completed = await Task.WhenAny(resolveTask, Task.Delay(OnDemandPosterTimeoutMs, ct));
            if (completed != resolveTask)
            {
                // Не встигли за 1.5с — віддаємо відповідь без очікування; таск лишається
                // дофоново (ResolveAsync не кидає, але спостереження безкоштовне).
                _ = resolveTask.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            }
            else
            {
                var resolved = await resolveTask; // completed — більше не чекає
                if (resolved is not null)
                {
                    // Встигли: підставляємо у відповідь і одразу fill-only в БД
                    // (фонові перегляди цього тайтла вже побачать постер без HTTP).
                    await PersistResolvedPosterAsync(titleId, resolved.PosterPath, resolved.BackdropPath, ct);
                    if (resolved.TmdbId != int.MinValue) await PersistResolvedTmdbIdAsync(titleId, resolved.TmdbId, ct);
                    return (TmdbImagesService.PosterUrl(resolved.PosterPath), TmdbImagesService.BackdropUrl(resolved.BackdropPath));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "On-demand доціплення постера не вдалося (title_id={TitleId})", titleId);
        }
        return (null, null);
    }

    /// <summary>Fill-only запис резолвнутих шляхів у titles (наявні значення не перебиваємо).</summary>
    private async Task PersistResolvedPosterAsync(int titleId, string? posterPath, string? backdropPath, CancellationToken ct)
    {
        try
        {
            var tracked = await db.Titles.FirstOrDefaultAsync(x => x.Id == titleId, ct);
            if (tracked is null) return;
            var changed = false;
            if (!string.IsNullOrWhiteSpace(posterPath) && string.IsNullOrWhiteSpace(tracked.PosterPath))
            {
                tracked.PosterPath = posterPath;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(backdropPath) && string.IsNullOrWhiteSpace(tracked.BackdropPath))
            {
                tracked.BackdropPath = backdropPath;
                changed = true;
            }
            if (changed)
            {
                tracked.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            // Відповідь уже сформована з резолвнутим URL — збоєний запис добере фонова джоба.
            log.LogWarning(ex, "Запис on-demand постера не вдалося (title_id={TitleId}) — добере фонова джоба", titleId);
        }
    }

    /// <summary>Fill-only бекфіл tmdb_id після imdb-місту (fill-only: наявний tmdb_id не чіпаємо).</summary>
    private async Task PersistResolvedTmdbIdAsync(int titleId, int tmdbId, CancellationToken ct)
    {
        try
        {
            var tracked = await db.Titles.FirstOrDefaultAsync(x => x.Id == titleId, ct);
            if (tracked is null || tracked.TmdbId is not null) return;
            tracked.TmdbId = tmdbId;
            tracked.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Некритично: бекфіл добере фонова джоба poster-source-sync (вона теж бекфілить).
            log.LogWarning(ex, "Бекфіл tmdb_id не вдалося (title_id={TitleId})", titleId);
        }
    }

    /// <summary>«Схожі» від картки: 1..limit результатів з поясненнями (uk/en).</summary>
    [HttpGet("{id:int}/similar")]
    public async Task<IActionResult> Similar(int id, [FromQuery] int limit = 20, [FromQuery] string? lang = null, CancellationToken ct = default)
    {
        var langSafe = SearchController.Lang(lang);
        var seed = await titles.ResolveAsync(id, ct);
        if (seed is null)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        var scored = await similarity.SimilarByTitleAsync(seed, limit, langSafe, filters: null, ct);
        return Ok(new { items = scored.Select(s => ToSimilarItem(s, langSafe)).ToList() });
    }

    internal static SimilarItem ToSimilarItem(ScoredTitle s, string lang)
    {
        var card = TitleMapper.ToCard(s.Title, lang);
        var item = new SimilarItem
        {
            Id = card.Id,
            TmdbId = card.TmdbId,
            Type = card.Type,
            Title = card.Title,
            OriginalTitle = card.OriginalTitle,
            Year = card.Year,
            Genres = card.Genres,
            Countries = card.Countries,
            RatingTmdb = card.RatingTmdb,
            RatingImdb = card.RatingImdb,
            PosterUrl = card.PosterUrl,
            RuntimeMin = card.RuntimeMin,
            Seasons = card.Seasons,
            Overview = card.Overview,
            // Codex IV (6): Score.Value — ранжувальний сигнал у SimilarityEngine (same-type
            // бонус ×1.1 свідомо виходить за 1.0); у DTO чесна ДИСПЛЕЙ-семантика: % збігу ≤100,
            // score>1 → 1.0 (максимальний збіг). Ранжування у сервісі лишається на сирових значеннях.
            Score = Math.Round(Math.Min(s.Score.Value, 1.0), 4),
            // FR-21: причини — машинні коди (shared_genres:<genre> / shared_actor:<ім'я> / decade:<YYYY0>s),
            // фронт локалізує; ліміти — жанри ≤3, актори ≤2.
            Reasons = SimilarityReasons.Build(s.Score, s.Title.Year, s.SeedYear),
        };
        return item;
    }
}
