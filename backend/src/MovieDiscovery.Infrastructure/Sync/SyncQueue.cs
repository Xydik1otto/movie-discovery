namespace MovieDiscovery.Infrastructure.Sync;

/// <summary>Черга фонової синхронізації (POST /api/admin/sync/popular → 202 + фонова обробка).</summary>
public sealed record SyncJob(int Pages, string Type, bool Automatic = false); // type: movie | series | all

public sealed class SyncQueue
{
    private readonly System.Threading.Channels.Channel<SyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<SyncJob>(new System.Threading.Channels.BoundedChannelOptions(100)
        {
            SingleReader = true,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
        });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(SyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<SyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
