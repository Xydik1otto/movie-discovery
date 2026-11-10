using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace MovieDiscovery.Infrastructure.Storage;

/// <summary>
/// Власне S3-сховище постерів (MinIO — «фото плакатів — окрема БД MinIO», наказ власника).
/// Контейнер moviediscovery-minio (compose), бакет за env MINIO_BUCKET (дефолт md-posters).
/// </summary>
public interface IPosterStorage
{
    /// <summary>Чесний fail-closed прапорець: false = сховище не налаштоване, жодних спроб мережі.</summary>
    bool Enabled { get; }

    /// <summary>Покласти об'єкт під ключ (напр. posters/00000001.jpg). Повертає ключ.</summary>
    Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Прочитати об'єкт (пошуковий стрім, MemoryStream з Position=0); null — нема/вимкнено.</summary>
    Task<Stream?> GetAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Реалізація IPosterStorage на офіційному MinIO SDK. Креденшели — виключно з env
/// (compose переказує MINIO_* у контейнер api). Fail-closed: Enabled=false, коли
/// POSTER_STORAGE_ENABLED != "true" АБО password/endpoint порожні — клієнт тоді
/// взагалі не будується, а PutAsync кидає чесну помилку (нічого «випадково» не пишемо).
/// </summary>
public sealed class MinioPosterStorage : IPosterStorage
{
    private readonly IMinioClient? _client;
    private readonly string _bucket;
    private bool _bucketEnsured;

    public bool Enabled { get; }

    public MinioPosterStorage(IConfiguration cfg)
    {
        var endpoint = cfg["MINIO_ENDPOINT"];
        var user = cfg["MINIO_ROOT_USER"];
        var password = cfg["MINIO_ROOT_PASSWORD"];
        var bucket = cfg["MINIO_BUCKET"];
        _bucket = string.IsNullOrWhiteSpace(bucket) ? "md-posters" : bucket!;

        Enabled = string.Equals(cfg["POSTER_STORAGE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase)
                  && !string.IsNullOrWhiteSpace(endpoint)
                  && !string.IsNullOrWhiteSpace(user)
                  && !string.IsNullOrWhiteSpace(password);

        if (!Enabled)
            return; // fail-closed: без клієнта і без жодних спроб мережі

        var uri = new Uri(endpoint!);
        _client = new MinioClient()
            .WithEndpoint(uri.Host, uri.Port == 0 ? (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : uri.Port)
            .WithCredentials(user, password)
            .WithSSL(string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            .Build();
    }

    /// <summary>Конструктор для тестів: готові значення замість env.</summary>
    internal MinioPosterStorage(IMinioClient client, string bucket, bool enabled)
    {
        _client = client;
        _bucket = bucket;
        Enabled = enabled;
    }

    public async Task<string> PutAsync(string key, Stream content, string contentType, CancellationToken ct = default)
    {
        if (!Enabled || _client is null)
            throw new InvalidOperationException(
                "poster-storage вимкнено (POSTER_STORAGE_ENABLED!=true або порожній пароль) — PUT неможливий (fail-closed).");

        // Буферизація: S4 PUT потребує довжину вмісту, а стрім може бути некерованим
        // (HTTP-відповідь). Постери — мегабайтна шкала, MemoryStream безпечний.
        await using var buffered = await BufferAsync(content, ct);
        await EnsureBucketAsync(ct);
        await _client.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithStreamData(buffered)
                .WithObjectSize(buffered.Length)
                .WithContentType(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType),
            ct);
        return key;
    }

    public async Task<Stream?> GetAsync(string key, CancellationToken ct = default)
    {
        if (!Enabled || _client is null)
            return null; // чесно: сховище вимкнено → контролер віддасть 404

        var sink = new MemoryStream();
        try
        {
            await _client.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithCallbackStream(async (stream, token) =>
                    {
                        await stream.CopyToAsync(sink, token);
                        await stream.DisposeAsync();
                    }),
                ct);
        }
        catch (ObjectNotFoundException)
        {
            return null; // ключа нема в бакеті → контролер 404
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        sink.Position = 0;
        return sink;
    }

    /// <summary>Бакет створюється ліниво один раз на час життя сервісу (Idempotent для MakeBucket).</summary>
    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketEnsured) return;
        var exists = await _client!.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), ct);
        if (!exists)
            await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket), ct);
        _bucketEnsured = true;
    }

    private static async Task<MemoryStream> BufferAsync(Stream content, CancellationToken ct)
    {
        var ms = new MemoryStream();
        if (content.CanSeek) content.Position = 0;
        await content.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }
}
