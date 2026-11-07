using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Watchability;

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
