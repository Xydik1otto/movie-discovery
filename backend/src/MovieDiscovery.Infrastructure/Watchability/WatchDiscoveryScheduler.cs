using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Sync;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>Feeds small unattended batches to the same workers used by the admin endpoints.</summary>
public sealed class WatchDiscoveryScheduler(
    WatchSyncQueue discovery,
    WatchLinkVerificationQueue verification,
    IOptions<TmdbOptions> tmdbOptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = tmdbOptions.Value;
        if (!options.AutomaticWatchDiscoveryEnabled) return;

        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(options.WatchSweepIntervalMinutes, 15, 1440)));
        do
        {
            if (discovery.Count == 0 && !string.IsNullOrWhiteSpace(options.ApiKey))
                discovery.TryEnqueue(new WatchSyncJob(TmdbProvidersService.Source, 0,
                    Math.Clamp(options.WatchSweepBatchSize, 1, 400), Sweep: true));

            if (verification.Count == 0)
                verification.TryEnqueue(new WatchLinkVerificationJob(50, TrustedOnly: true));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
