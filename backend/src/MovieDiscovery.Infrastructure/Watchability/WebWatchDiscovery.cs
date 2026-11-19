using System.Data;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Watchability;

public sealed class WebWatchQueue(IOptions<WatchWebOptions> options) : IDisposable
{
    internal readonly record struct Job(int TitleId, string Country);

    private readonly Channel<Job> _requests = Channel.CreateBounded<Job>(new BoundedChannelOptions(32)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly object _gate = new();
    private readonly HashSet<Job> _pending = [];
    private readonly MemoryCache _recent = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private DateTime _pausedUntil;

    public bool Enabled => options.Value.Enabled && !string.IsNullOrWhiteSpace(options.Value.ApiKey);

    public bool TryEnqueue(int titleId, string type, int? year, string country)
    {
        if (!Enabled || year is null || !WebWatchSearch.CanSearch(type, country)) return false;
        var job = new Job(titleId, country);
        lock (_gate)
        {
            if (_pending.Contains(job)) return true;
            if (DateTime.UtcNow < _pausedUntil || _recent.TryGetValue(job, out _) || !_requests.Writer.TryWrite(job))
                return false;
            _pending.Add(job);
            return true;
        }
    }

    internal void Complete(Job job, bool succeeded)
    {
        lock (_gate)
        {
            _pending.Remove(job);
            _recent.Set(job, true, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = succeeded ? TimeSpan.FromDays(7) : TimeSpan.FromMinutes(30),
            });
        }
    }

    internal void PauseUntil(DateTime until)
    {
        lock (_gate) _pausedUntil = until;
    }

    internal int Count => _requests.Reader.Count;
    internal bool IsPaused { get { lock (_gate) return DateTime.UtcNow < _pausedUntil; } }
    internal IAsyncEnumerable<Job> ReadAllAsync(CancellationToken ct) => _requests.Reader.ReadAllAsync(ct);

    public void Dispose() => _recent.Dispose();
}

public sealed class WebWatchDiscovery(
    WebWatchQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<WatchWebOptions> options,
    ILogger<WebWatchDiscovery> log) : BackgroundService
{
    private const string QuotaKind = "daily-quota";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => queue.Enabled
        ? Task.WhenAll(ConsumeAsync(stoppingToken), ScheduleAsync(stoppingToken))
        : Task.CompletedTask;

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var job in queue.ReadAllAsync(ct))
        {
            var succeeded = false;
            try
            {
                if (queue.IsPaused) continue;
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
                var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == job.TitleId, ct);
                if (title?.Year is null || !WebWatchSearch.CanSearch(title.Type, job.Country) ||
                    !await ReserveRequestAsync(db, ct)) continue;

                var search = scope.ServiceProvider.GetRequiredService<WebWatchSearch>();
                var candidates = await search.FindAsync(title, job.Country, ct);
                var stored = await db.Providers.Where(p => p.TitleId == title.Id && p.Region == job.Country &&
                    p.Source == WebWatchSearch.Source).ToListAsync(ct);
                var now = DateTime.UtcNow;
                foreach (var candidate in candidates)
                {
                    var existing = stored.Find(p => p.ProviderName == candidate.ProviderName);
                    if (existing is null)
                        db.Providers.Add(candidate);
                    else if (candidate.Kind == WebWatchSearch.CandidateKind || existing.Url == candidate.Url)
                    {
                        existing.Url = candidate.Url;
                        existing.UpdatedAt = now;
                    }
                }
                db.Providers.RemoveRange(stored.Where(p => p.UpdatedAt < now - WebWatchSearch.CandidateLifetime));
                await db.SaveChangesAsync(ct);
                succeeded = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                // Avoid placing the request URI, which contains the full query, in worker warnings.
                log.LogWarning("Viewing-link web search failed (status {Status})", ex.StatusCode);
                queue.PauseUntil(DateTime.UtcNow.AddMinutes(30));
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not complete viewing-link discovery for title {TitleId}", job.TitleId);
            }
            finally
            {
                queue.Complete(job, succeeded);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task<bool> ReserveRequestAsync(MovieDiscoveryDbContext db, CancellationToken ct)
    {
        // Commit the allowance before contacting Brave. Serializable isolation prevents two instances
        // from spending the same slot, and even a failed upstream request consumes an allowance.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;
        var now = DateTime.UtcNow;
        var state = await FindStateAsync(db, QuotaKind, ct);
        var used = state.LastRunAt?.Date == now.Date ? state.LastCount ?? 0 : 0;
        if (used >= Math.Clamp(options.Value.DailyRequests, 1, 500))
        {
            queue.PauseUntil(now.Date.AddDays(1));
            return false;
        }

        state.LastRunAt = now;
        state.LastCount = used + 1;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return true;
    }

    private async Task ScheduleAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(options.Value.SweepIntervalMinutes, 30, 1440)));
        var countries = WebWatchSearch.Countries.ToArray();
        var index = 0;
        do
        {
            if (queue.Count != 0 || queue.IsPaused) continue;
            var country = countries[index++ % countries.Length];
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
                var state = await FindStateAsync(db, $"sweep-{country}", ct);
                var cursor = state.LastPages ?? 0;
                var types = new[] { "movie", "series", "show", "anime" }.Where(type => WebWatchSearch.CanSearch(type, country)).ToArray();
                if (types.Length == 0) continue;

                var active = db.Titles.AsNoTracking().Where(t => types.Contains(t.Type) && t.Year != null)
                    .OrderByDescending(t => t.Popularity).ThenBy(t => t.Id).Take(1000);
                var title = await active.Where(t => t.Id > cursor).OrderBy(t => t.Id)
                    .Select(t => new { t.Id, t.Type, t.Year }).FirstOrDefaultAsync(ct);
                if (title is not null)
                    queue.TryEnqueue(title.Id, title.Type, title.Year, country);

                state.LastPages = title?.Id ?? 0;
                state.LastRunAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not queue viewing-link discovery for {Country}", country);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private static async Task<SyncState> FindStateAsync(MovieDiscoveryDbContext db, string kind, CancellationToken ct) =>
        await db.SyncStates.SingleOrDefaultAsync(s => s.Source == WebWatchSearch.Source && s.Kind == kind, ct) ??
        db.SyncStates.Add(new SyncState { Source = WebWatchSearch.Source, Kind = kind }).Entity;
}
