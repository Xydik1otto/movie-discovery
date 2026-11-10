using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Embeddings;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Sync;
using MovieDiscovery.Infrastructure.Tmdb;
using MovieDiscovery.Infrastructure.Watch;
using MovieDiscovery.Infrastructure.Watchability;
using MovieDiscovery.Infrastructure.Wikipedia;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/admin")]
public sealed class AdminController(
    MovieDiscoveryDbContext db,
    SyncQueue syncQueue,
    EmbeddingQueue embeddingQueue,
    AnimeSyncQueue animeSyncQueue,
    ImdbSyncQueue imdbSyncQueue,
    ImdbAkasSyncQueue imdbAkasSyncQueue,
    ImdbCrewSyncQueue imdbCrewSyncQueue,
    WikidataNamesSyncQueue wikidataNamesSyncQueue,
    YoutubeSyncQueue youtubeSyncQueue,
    WatchSyncQueue watchSyncQueue,
    JustWatchSyncQueue justWatchSyncQueue,
    MegogoSyncQueue megogoSyncQueue,
    PlexSyncQueue plexSyncQueue,
    WikipediaPosterSyncQueue wikipediaPosterSyncQueue,
    PosterCleanupQueue posterCleanupQueue,
    PosterStorageSyncQueue posterStorageSyncQueue,
    PosterSourceSyncQueue posterSourceSyncQueue,
    WatchLinkVerificationQueue watchLinkVerificationQueue,
    Microsoft.Extensions.Configuration.IConfiguration configuration,
    Microsoft.Extensions.Options.IOptions<MalOfficialOptions> malOptions,
    Microsoft.Extensions.Options.IOptions<TmdbOptions> tmdbOptions) : ControllerBase
{
    /// <summary>Перевірка X-Admin-Key — спільний AdminGate (логіка колись була продубльована тут).</summary>
    private IActionResult? CheckAdmin() => AdminGate.Verify(HttpContext, configuration);

    /// <summary>Поставити popular/discover-синхронізацію у чергу: 202 {queued:true}.</summary>
    [HttpPost("sync/popular")]
    public IActionResult SyncPopular([FromBody] SyncPopularRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var type = (req?.Type ?? "all").ToLowerInvariant();
        if (type is not ("movie" or "series" or "all"))
            return BadRequest(new { error = "invalid_request", message = "type має бути movie | series | all" });

        var pages = req?.Pages ?? 5;
        if (pages is < 1 or > 500)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–500" });

        if (!syncQueue.TryEnqueue(new SyncJob(pages, type)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true });
    }

    /// <summary>Синк аніме-каталогу (AniList POPULARITY_DESC × pages; Jikan — dormant): 202 {queued:true, source:"anilist"}.</summary>
    [HttpPost("sync/anime")]
    public IActionResult SyncAnime([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 20)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–20" });

        if (!animeSyncQueue.TryEnqueue(new AnimeSyncJob(pages, "anime")))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "anilist" });
    }

    /// <summary>Синк манга-каталогу (AniList POPULARITY_DESC × pages; Jikan — dormant): 202 {queued:true, source:"anilist"}.</summary>
    [HttpPost("sync/manga")]
    public IActionResult SyncManga([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 20)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–20" });

        if (!animeSyncQueue.TryEnqueue(new AnimeSyncJob(pages, "manga")))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "anilist" });
    }

    /// <summary>
    /// Сезонний шар офіційного MAL API v2 (anime/season/{year}/{season}): 202 {queued:true, source:"mal-official"}.
    /// MAL_CLIENT_ID не задано → 400 з підказкою (реєстрація безкоштовного client ID).
    /// </summary>
    [HttpPost("sync/mal-season")]
    public IActionResult SyncMalSeason([FromBody] MalSeasonRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        if (string.IsNullOrWhiteSpace(malOptions.Value.ClientId))
            return BadRequest(new
            {
                error = "mal_not_configured",
                message = "MAL_CLIENT_ID не задано: зареєструй безкоштовний client ID на myanimelist.net/apis.php і пропиши MAL_CLIENT_ID у .env",
            });

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 10)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–10" });

        var season = (req?.Season ?? MalOfficialClient.SeasonForMonth(DateTime.UtcNow.Month)).Trim().ToLowerInvariant();
        if (!MalOfficialClient.Seasons.Contains(season))
            return BadRequest(new { error = "invalid_request", message = "season має бути winter | spring | summer | fall" });

        var year = req?.Year ?? DateTime.UtcNow.Year;
        if (year is < 1900 or > 2999)
            return BadRequest(new { error = "invalid_request", message = "year має бути 1900–2999" });

        if (!animeSyncQueue.TryEnqueue(new AnimeSyncJob(pages, "mal-season", year, season)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "mal-official" });
    }

    /// <summary>
    /// FR-15: масивний імпорт IMDb datasets (title.basics + title.ratings) — 202 {queued:true, source:"imdb"}.
    /// mediaTypes: movie|series (порожньо/відсутнє → обидва); maxRows — smoke-ліміт рядків після фільтру типів.
    /// </summary>
    [HttpPost("sync/imdb")]
    public IActionResult SyncImdb([FromBody] ImdbSyncRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var types = new List<string>();
        foreach (var t in req?.MediaTypes ?? [])
        {
            var v = t.Trim().ToLowerInvariant();
            if (v is not ("movie" or "series"))
                return BadRequest(new { error = "invalid_request", message = "mediaTypes мають бути movie | series" });
            if (!types.Contains(v)) types.Add(v);
        }

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && mr is < 1 or > 10_000_000)
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–10 000 000" });

        if (!imdbSyncQueue.TryEnqueue(new ImdbSyncJob(types, maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "imdb" });
    }

    /// <summary>
    /// FR-19: title.akas (isOriginalTitle=1) → titles.country (ISO-2): 202 {queued:true, source:"imdb-akas"}.
    /// maxRows — опційний smoke-ліміт рядків після фільтрів.
    /// </summary>
    [HttpPost("sync/imdb-akas")]
    public IActionResult SyncImdbAkas([FromBody] MaxRowsRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && mr is < 1 or > 100_000_000)
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–100 000 000" });

        if (!imdbAkasSyncQueue.TryEnqueue(new ImdbAkasSyncJob(maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "imdb-akas" });
    }

    /// <summary>
    /// Розширення FR-19/20: IMDb crew + principals (actor|actress|self, перші ~8) + name.basics
    /// → titles.directors/actors (movie|series, без uk-джерел): 202 {queued:true, source:"imdb-crew"}.
    /// </summary>
    [HttpPost("sync/imdb-crew")]
    public IActionResult SyncImdbCrew([FromBody] MaxRowsRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && mr is < 1 or > 10_000_000)
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–10 000 000" });

        if (!imdbCrewSyncQueue.TryEnqueue(new ImdbCrewSyncJob(maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "imdb-crew" });
    }

    /// <summary>
    /// Назви/постери з Wikidata (uk label + ru-фолбек + P18): 202 {queued:true, source:"wikidata-names"}.
    /// maxRows — опційний smoke-ліміт рядків (смуок на одному бакеті).
    /// </summary>
    [HttpPost("sync/wikidata-names")]
    public IActionResult SyncWikidataNames([FromBody] MaxRowsRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && mr is < 1 or > 100_000_000)
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–100 000 000" });

        if (!wikidataNamesSyncQueue.TryEnqueue(new WikidataNamesSyncJob(maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "wikidata-names" });
    }

    /// <summary>
    /// FR: франшизи/саги (Wikidata P179 + P155/P156) → title_series: 202 {queued:true, source:"title-series"}.
    /// maxRows — опційний smoke-ліміт рядків (смуок на одному батчі). Черга через [FromServices] —
    /// конструктор і решта файлу не змінюються.
    /// </summary>
    [HttpPost("sync/title-series")]
    public IActionResult SyncTitleSeries(
        [FromBody] MaxRowsRequest? req,
        [FromServices] MovieDiscovery.Infrastructure.Series.TitleSeriesSyncQueue titleSeriesSyncQueue)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && mr is < 1 or > 100_000_000)
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–100 000 000" });

        if (!titleSeriesSyncQueue.TryEnqueue(new MovieDiscovery.Infrastructure.Series.TitleSeriesSyncJob(maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "title-series" });
    }

    /// <summary>
    /// FR-20: YouTube як джерело перегляду: топ-N тайтлів без youtube-лінку → пошук+матч
    /// → watch_links: 202 {queued:true, source:"youtube"}. Без YOUTUBE_API_KEY джоба
    /// пропускається з warn (dormant, нічого не ламається).
    /// </summary>
    [HttpPost("sync/youtube")]
    public IActionResult SyncYoutube([FromBody] YoutubeSyncRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var limit = req?.Limit ?? 200;
        if (limit is < 1 or > 10_000)
            return BadRequest(new { error = "invalid_request", message = "limit має бути 1–10 000" });

        if (!youtubeSyncQueue.TryEnqueue(new YoutubeSyncJob(limit)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "youtube" });
    }

    /// <summary>FR-14: Internet Archive feature_films → watch_links (free): 202 {queued:true, source:"archive"}.</summary>
    [HttpPost("sync/archive-films")]
    public IActionResult SyncArchiveFilms([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 100)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–100" });

        if (!watchSyncQueue.TryEnqueue(new WatchSyncJob("archive-films", pages, 0)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "archive" });
    }

    /// <summary>FR-14: sweet.tv каталог+картки → titles (uk) + watch_links (UA): 202 {queued:true, source:"sweettv"}.</summary>
    [HttpPost("sync/sweettv")]
    public IActionResult SyncSweetTv([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 1;
        if (pages is < 1 or > 20)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–20" });

        if (!watchSyncQueue.TryEnqueue(new WatchSyncJob("sweettv", pages, 0)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "sweettv" });
    }

    /// <summary>JustWatch (усі провайдери «де подивитись», рішення власника 09.11): 202 {queued:true, source:"justwatch"}.</summary>
    [HttpPost("sync/justwatch")]
    public IActionResult SyncJustWatch([FromBody] JustWatchSyncJob? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxPages = req?.MaxPages ?? 10;
        if (maxPages is < 1 or > 100)
            return BadRequest(new { error = "invalid_request", message = "maxPages має бути 1–100" });
        var limit = req?.Limit ?? 100;
        if (limit is < 0 or > 100_000)
            return BadRequest(new { error = "invalid_request", message = "limit має бути 0–100 000" });

        if (!justWatchSyncQueue.TryEnqueue(new JustWatchSyncJob(maxPages, limit)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "justwatch" });
    }

    /// <summary>Megogo free-каталог UA (pages — на КОЖНУ колекцію): 202 {queued:true, source:"megogo"}.</summary>
    [HttpPost("sync/megogo")]
    public IActionResult SyncMegogo([FromBody] SyncPagesRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var pages = req?.Pages ?? 50;
        if (pages is < 1 or > 50)
            return BadRequest(new { error = "invalid_request", message = "pages має бути 1–50 (на колекцію)" });

        if (!megogoSyncQueue.TryEnqueue(new MegogoSyncJob(pages)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "megogo" });
    }

    /// <summary>Plex free-каталог (movie-database роки): 202 {queued:true, source:"plex"}.</summary>
    [HttpPost("sync/plex")]
    public IActionResult SyncPlex([FromBody] PlexSyncJob? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var job = req ?? new PlexSyncJob(null, null, null);
        if (job.MaxPages is { } mp && (mp < 1 || mp > 120))
            return BadRequest(new { error = "invalid_request", message = "maxPages має бути 1–120" });

        if (!plexSyncQueue.TryEnqueue(job))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "plex" });
    }

    /// <summary>UX-02: Wikipedia-постери (QID-first, fill-only): 202 {queued:true, source:"wikipedia-posters"}.</summary>
    [HttpPost("sync/wikipedia-posters")]
    public IActionResult SyncWikipediaPosters([FromBody] WikipediaPosterSyncJob? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && (mr < 1 || mr > 3_000_000))
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–3 000 000" });

        if (!wikipediaPosterSyncQueue.TryEnqueue(new WikipediaPosterSyncJob(maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "wikipedia-posters" });
    }

    /// <summary>UX-02/IV-09: чистка фейкових (ytimg/missing_original) + заміна підтверджених wrong-Commons постерів.</summary>
    [HttpPost("cleanup/posters")]
    public IActionResult CleanupPosters([FromBody] PosterCleanupJob? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && (mr < 1 || mr > 100_000))
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–100 000" });

        if (!posterCleanupQueue.TryEnqueue(new PosterCleanupJob(maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга переповнена" });

        return Accepted(new { queued = true, source = "posters-cleanup" });
    }

    /// <summary>Постери-MinIO (10.11): перенос існуючих poster_path у бакет (poster_local).</summary>
    [HttpPost("sync/poster-storage")]
    public IActionResult SyncPosterStorage([FromBody] PosterStorageSyncJob? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && (mr < 1 || mr > 500_000))
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–500 000" });

        if (!posterStorageSyncQueue.TryEnqueue(new PosterStorageSyncJob(maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга переповнена" });

        return Accepted(new { queued = true, source = "poster-storage" });
    }

    /// <summary>Джерела постерів (10.11): TMDB/Shikimori/Jikan → poster_path+backdrop_path (fill-only, popularity DESC).</summary>
    [HttpPost("sync/poster-source")]
    public IActionResult SyncPosterSource([FromBody] PosterSourceSyncJob? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && (mr < 1 || mr > 2_000_000))
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–2 000 000" });

        if (!posterSourceSyncQueue.TryEnqueue(new PosterSourceSyncJob(maxRows, req?.TitleId)))
            return StatusCode(503, new { error = "queue_full", message = "Черга переповнена" });

        return Accepted(new { queued = true, source = "poster-source-sync" });
    }

    /// <summary>Watch-верифікація (10.11): coverage full|episode|unknown — жива перевірка лінків.</summary>
    [HttpPost("sync/watch-verify")]
    public IActionResult SyncWatchVerify([FromBody] WatchLinkVerificationJob? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var maxRows = req?.MaxRows;
        if (maxRows is { } mr && (mr < 1 || mr > 1_000_000))
            return BadRequest(new { error = "invalid_request", message = "maxRows має бути 1–1 000 000" });

        if (!watchLinkVerificationQueue.TryEnqueue(new WatchLinkVerificationJob(maxRows)))
            return StatusCode(503, new { error = "queue_full", message = "Черга переповнена" });

        return Accepted(new { queued = true, source = "watch-verify" });
    }

    /// <summary>
    /// FR-14: TMDB watch/providers (JustWatch через TMDB, region UA) → watch_links(tmdb-streaming).
    /// Без TMDB_API_KEY → 400 з підказкою зареєструвати безкоштовний ключ (dormant, нічого не ламається).
    /// З ключем → 202 {queued:true, source:"tmdb-providers"}; опційний body {limit} — скільки тайтлів обробити.
    /// </summary>
    [HttpPost("sync/tmdb-providers")]
    public IActionResult SyncTmdbProviders([FromBody] TmdbProvidersRequest? req)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        if (string.IsNullOrWhiteSpace(tmdbOptions.Value.ApiKey))
            return BadRequest(new
            {
                error = "tmdb_not_configured",
                message = "TMDB_API_KEY не задано: зареєструй безкоштовний ключ на themoviedb.org (Settings → API) і пропиши TMDB_API_KEY у .env",
            });

        var limit = req?.Limit ?? TmdbProvidersService.DefaultLimit;
        if (limit is < 1 or > 10_000)
            return BadRequest(new { error = "invalid_request", message = "limit має бути 1–10 000" });

        if (!watchSyncQueue.TryEnqueue(new WatchSyncJob("tmdb-providers", 0, limit)))
            return StatusCode(503, new { error = "queue_full", message = "Черга синхронізації переповнена" });

        return Accepted(new { queued = true, source = "tmdb-providers" });
    }

    [HttpGet("sync/status")]
    public async Task<IActionResult> SyncStatus(CancellationToken ct)
    {
        var denied = CheckAdmin();
        if (denied is not null) return denied;

        var lastRun = await db.SyncStates
            .Where(s => s.Source == "tmdb")
            .OrderByDescending(s => s.LastRunAt)
            .Select(s => s.LastRunAt)
            .FirstOrDefaultAsync(ct);

        var titlesCount = await db.Titles.CountAsync(ct);
        var embeddingsCount = await db.Embeddings.CountAsync(ct);
        var animeCount = await db.Titles.CountAsync(t => t.Type == "anime", ct);
        var mangaCount = await db.Titles.CountAsync(t => t.Type == "manga", ct);

        return Ok(new
        {
            lastRunAt = lastRun,
            titlesCount,
            embeddingsCount,
            queueSize = embeddingQueue.Count + syncQueue.Count + animeSyncQueue.Count,
            animeCount,
            mangaCount,
        });
    }

    public sealed class SyncPopularRequest
    {
        public int Pages { get; set; } = 5;
        public string Type { get; set; } = "all";
    }

    public sealed class SyncPagesRequest
    {
        public int Pages { get; set; } = 1;
    }

    public sealed class MalSeasonRequest
    {
        public int? Year { get; set; }     // null → поточний рік
        public string? Season { get; set; } // null → поточний сезон (winter|spring|summer|fall)
        public int Pages { get; set; } = 1;
    }

    public sealed class ImdbSyncRequest
    {
        public List<string>? MediaTypes { get; set; } // movie | series; порожньо → обидва
        public int? MaxRows { get; set; }             // smoke-ліміт рядків після фільтру типів
    }

    public sealed class MaxRowsRequest
    {
        public int? MaxRows { get; set; } // опційний smoke-ліміт (akas/crew)
    }

    public sealed class YoutubeSyncRequest
    {
        public int? Limit { get; set; } // скільки тайтлів збагатити (default 200)
    }

    public sealed class TmdbProvidersRequest
    {
        public int? Limit { get; set; } // скільки тайтлів з imdb_id обробити (default 500)
    }
}
