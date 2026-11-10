using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Abstractions;

namespace MovieDiscovery.Infrastructure.Imdb;

/// <summary>
/// Спільний стрім-читач IMDb .gz-датасетів: розпаковка GZip → рядки TSV
/// (без повного завантаження в пам'ять). Використовується bulk/akas/crew-синками.
/// </summary>
public static class ImdbGzip
{
    public static async IAsyncEnumerable<string> TsvLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        while (await reader.ReadLineAsync(ct) is { } line)
            yield return line;
    }
}

/// <summary>
/// Спільний кеш-завантажувач IMDb-датасетів (той самий патерн, що в ImdbBulkService):
/// .gz у temp-каталозі, свіжість 24 год за source_cache-метою; повторний sync не перекачує.
/// </summary>
internal sealed class ImdbFileCache(
    HttpClient http,
    ICacheStore cache,
    ILogger log)
{
    public async Task<string> DownloadAsync(
        string source, string fileName, string url, string cacheKey,
        string? cacheDir, int ttlHours, CancellationToken ct)
    {
        var dir = string.IsNullOrWhiteSpace(cacheDir)
            ? Path.Combine(Path.GetTempPath(), "movie-discovery", "imdb")
            : cacheDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);

        var freshMeta = await cache.GetAsync(source, cacheKey, ct);
        if (File.Exists(path) && new FileInfo(path).Length > 0 && TryFetchedAt(freshMeta, out var at) &&
            DateTime.UtcNow - at < TimeSpan.FromHours(Math.Max(1, ttlHours)))
        {
            log.LogInformation("IMDb {File}: свіжий кеш ({Hours:0.0} год) — пропускаю завантаження",
                fileName, (DateTime.UtcNow - at).TotalHours);
            return path;
        }

        log.LogInformation("IMDb: завантажую {Url} → {Path}", url, path);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        await src.CopyToAsync(dst, ct);

        await cache.SetAsync(source, cacheKey,
            JsonSerializer.Serialize(new ImdbFileCacheMeta(DateTime.UtcNow)), 86_400, ct);
        return path;
    }

    private sealed record ImdbFileCacheMeta([property: JsonPropertyName("fetchedAt")] DateTime FetchedAt);

    private static bool TryFetchedAt(string? json, out DateTime at)
    {
        at = default;
        if (json is null) return false;
        try
        {
            var meta = JsonSerializer.Deserialize<ImdbFileCacheMeta>(json);
            if (meta is null) return false;
            at = meta.FetchedAt;
            return true;
        }
        catch (JsonException) { return false; }
    }
}
