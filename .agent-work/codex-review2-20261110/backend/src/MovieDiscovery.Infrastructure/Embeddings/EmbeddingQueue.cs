using System.Threading.Channels;

namespace MovieDiscovery.Infrastructure.Embeddings;

/// <summary>Черга на перерахунок векторів (title_id). Bounded, DropOldest — синхронізація не блокує API.</summary>
public sealed class EmbeddingQueue
{
    private readonly Channel<int> _channel = Channel.CreateBounded<int>(new BoundedChannelOptions(20_000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(int titleId) => _channel.Writer.TryWrite(titleId);

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public bool TryPeek(out int value) => _channel.Reader.TryPeek(out value);
}
