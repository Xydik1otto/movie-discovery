using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Watchability;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Infrastructure.Sync;

/// <summary>Джоба imdb bulk sync (FR-15).</summary>
public sealed record ImdbSyncJob(IReadOnlyList<string> MediaTypes, int? MaxRows);

/// <summary>Черга imdb-синхронізації (202 → фонова джоба).</summary>
public sealed class ImdbSyncQueue
{
    private readonly System.Threading.Channels.Channel<ImdbSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<ImdbSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(ImdbSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<ImdbSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Джоба watchability-синків (FR-14): archive-films | sweettv | tmdb-providers.</summary>
public sealed record WatchSyncJob(string Source, int Pages, int Limit);

/// <summary>Черга watchability-синхронізацій.</summary>
public sealed class WatchSyncQueue
{
    private readonly System.Threading.Channels.Channel<WatchSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<WatchSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(100)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(WatchSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<WatchSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка imdb bulk-джоб (потокове завантаження + staging MERGE).</summary>
public sealed class ImdbSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<ImdbSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ImdbSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ImdbBulkService>();
                var result = await service.RunAsync(job.MediaTypes, job.MaxRows, stoppingToken);
                log.LogInformation("IMDb джоба завершена: movies={Movies}, series={Series}",
                    result.Movies, result.Series);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка imdb-синхронізації (maxRows={MaxRows})", job.MaxRows);
            }
        }
    }
}

/// <summary>Фонова обробка watchability-джоб: archive-films / sweettv / tmdb-providers.</summary>
public sealed class WatchSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<WatchSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<WatchSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                switch (job.Source)
                {
                    case "archive-films":
                        await scope.ServiceProvider.GetRequiredService<ArchiveWatchService>()
                            .SyncAsync(job.Pages, stoppingToken);
                        break;
                    case "sweettv":
                        await scope.ServiceProvider.GetRequiredService<SweetTvService>()
                            .SyncAsync(job.Pages, stoppingToken);
                        break;
                    case "tmdb-providers":
                        await scope.ServiceProvider.GetRequiredService<TmdbProvidersService>()
                            .SyncAsync(job.Limit, stoppingToken);
                        break;
                    default:
                        log.LogWarning("Невідоме джерело watch-sync: {Source}", job.Source);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SyncSkipException ex)
            {
                log.LogWarning("Watch-sync {Source} пропущено: {Reason}", job.Source, ex.UserMessage);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка watch-синхронізації (source={Source})", job.Source);
            }
        }
    }
}

// ─── FR-19: imdb-akas (країна виробника) ───

/// <summary>Джоба imdb-akas sync (FR-19): title.akas → titles.country.</summary>
public sealed record ImdbAkasSyncJob(int? MaxRows);

/// <summary>Черга imdb-akas-синхронізації.</summary>
public sealed class ImdbAkasSyncQueue
{
    private readonly System.Threading.Channels.Channel<ImdbAkasSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<ImdbAkasSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(ImdbAkasSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<ImdbAkasSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка imdb-akas-джоб (стікм akas.tsv → staging → UPDATE titles.country).</summary>
public sealed class ImdbAkasSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<ImdbAkasSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ImdbAkasSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ImdbAkasService>();
                var result = await service.RunAsync(job.MaxRows, stoppingToken);
                log.LogInformation("IMDb akas джоба завершена: staged={Staged}, updated={Updated}",
                    result.Staged, result.Updated);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка imdb-akas-синхронізації (maxRows={MaxRows})", job.MaxRows);
            }
        }
    }
}

// ─── Розширення FR-19/20: imdb-crew (directors/actors) ───

/// <summary>Джоба imdb-crew sync: crew + principals + name.basics → titles.directors/actors.</summary>
public sealed record ImdbCrewSyncJob(int? MaxRows);

/// <summary>Черга imdb-crew-синхронізації.</summary>
public sealed class ImdbCrewSyncQueue
{
    private readonly System.Threading.Channels.Channel<ImdbCrewSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<ImdbCrewSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(ImdbCrewSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<ImdbCrewSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка imdb-crew-джоб (3 стріми → staging → MERGE у titles).</summary>
public sealed class ImdbCrewSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<ImdbCrewSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ImdbCrewSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ImdbCrewService>();
                var result = await service.RunAsync(job.MaxRows, stoppingToken);
                log.LogInformation("IMDb crew джоба завершена: movies={Movies}, series={Series}, updated={Updated}",
                    result.MoviesStaged, result.SeriesStaged, result.TitlesUpdated);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка imdb-crew-синхронізації (maxRows={MaxRows})", job.MaxRows);
            }
        }
    }
}

// ─── FR-20: YouTube як джерело перегляду ───

/// <summary>Джоба youtube-синхронізації: топ-N тайтлів без youtube-лінку → watch_links.</summary>
public sealed record YoutubeSyncJob(int Limit);

/// <summary>Черга youtube-синхронізації.</summary>
public sealed class YoutubeSyncQueue
{
    private readonly System.Threading.Channels.Channel<YoutubeSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<YoutubeSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(YoutubeSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<YoutubeSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка youtube-джоб (пошук+матч → watch_links, прогрес sync_state kind=enrich).</summary>
public sealed class YoutubeSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<YoutubeSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<YoutubeSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<YouTubeEnrichService>();
                if (!service.IsConfigured)
                {
                    log.LogWarning("YOUTUBE_API_KEY не задано — youtube-синхронізація пропущена (dormant)");
                    continue;
                }
                var added = await service.SyncAsync(job.Limit, stoppingToken);
                log.LogInformation("YouTube джоба завершена: додано {Added} лінків", added);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка youtube-синхронізації (limit={Limit})", job.Limit);
            }
        }
    }
}
