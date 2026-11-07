using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>Джоба синку аніме/манга-каталогу (kind: anime | manga).</summary>
public sealed record AnimeSyncJob(int Pages, string Kind);

/// <summary>Черга синку каталогу аніме/манги (POST /api/admin/sync/anime|manga → 202 + фонова обробка).</summary>
public sealed class AnimeSyncQueue
{
    private readonly Channel<AnimeSyncJob> _channel =
        Channel.CreateBounded<AnimeSyncJob>(new BoundedChannelOptions(10)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(AnimeSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<AnimeSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фоновий обробник синку аніме/манга-каталогу (за патерном SyncBackgroundService).</summary>
public sealed class AnimeSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AnimeSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<AnimeSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var catalog = scope.ServiceProvider.GetRequiredService<AnimeCatalogService>();
                var count = job.Kind == "manga"
                    ? await catalog.SyncMangaAsync(job.Pages, stoppingToken)
                    : await catalog.SyncAnimeAsync(job.Pages, stoppingToken);
                log.LogInformation("Синк {Kind} завершено: {Count} тайтлів", job.Kind, count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка синку аніме/манга-каталогу (kind={Kind}, pages={Pages})", job.Kind, job.Pages);
            }
        }
    }
}
