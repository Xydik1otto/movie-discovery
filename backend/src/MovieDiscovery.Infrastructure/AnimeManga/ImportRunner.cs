using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>Посилання на джобу імпорту в черзі.</summary>
public sealed record ImportQueueJob(int JobId);

/// <summary>Черга фонових імпортів (POST /api/import/jobs → 202 + фонова обробка).</summary>
public sealed class ImportQueue
{
    private readonly Channel<ImportQueueJob> _channel =
        Channel.CreateBounded<ImportQueueJob>(new BoundedChannelOptions(20)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(ImportQueueJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<ImportQueueJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фоновий обробник імпортів: джоба з БД → ImportService.RunAsync → статус/статистика в import_jobs.</summary>
public sealed class ImportBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<ImportBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<ImportQueue>();
        await foreach (var queued in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var importer = scope.ServiceProvider.GetRequiredService<ImportService>();
                await importer.RunAsync(queued.JobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка обробки імпорту (jobId={JobId})", queued.JobId);
            }
        }
    }
}
