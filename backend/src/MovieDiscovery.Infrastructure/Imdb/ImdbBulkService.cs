using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace MovieDiscovery.Infrastructure.Imdb;

/// <summary>Опції IMDb bulk-імпорту (розділ Imdb конфігу).</summary>
public sealed class ImdbOptions
{
    /// <summary>Каталог для кешування .gz-файлів; порожньо → {TMPDIR}/movie-discovery/imdb.</summary>
    public string CacheDir { get; set; } = "";

    /// <summary>Скільки годин скачаний файл вважається свіжим (повторний sync не перекачує).</summary>
    public int CacheTtlHours { get; set; } = 24;

    /// <summary>Розмір батчу staging COPY → MERGE.</summary>
    public int BatchSize { get; set; } = 50_000;
}

/// <summary>Результат прогону imdb sync.</summary>
public sealed record ImdbSyncResult(int Movies, int Series, int Skipped);

/// <summary>
/// Масивний імпорт IMDb datasets (FR-15): потокове завантаження title.basics.tsv.gz +
/// title.ratings.tsv.gz (у temp-файл, НЕ в пам'ять цілком), парсинг TSV рядків,
/// PG binary COPY у staging imdb_raw → MERGE у titles за imdb_id (ідемпотентно),
/// staging чиститься. Для non-Npgsql провайдера (тестовий InMemory) — EF-шлях через ImdbUpsertCore.
/// </summary>
public sealed class ImdbBulkService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    ICacheStore cache,
    IOptions<ImdbOptions> options,
    ILogger<ImdbBulkService> log)
{
    public const string Source = "imdb";
    public const string BasicsUrl = "https://datasets.imdbws.com/title.basics.tsv.gz";
    public const string RatingsUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";

    private readonly ImdbOptions _opt = options.Value;

    /// <summary>Повний прогін: download → parse → stage/merge → sync_state.</summary>
    public async Task<ImdbSyncResult> RunAsync(
        IReadOnlyCollection<string> mediaTypes, int? maxRows, CancellationToken ct = default)
    {
        var types = NormalizeTypes(mediaTypes);
        var basicsPath = await DownloadCachedAsync("title.basics.tsv.gz", BasicsUrl, "file:basics", ct);
        var ratingsPath = await DownloadCachedAsync("title.ratings.tsv.gz", RatingsUrl, "file:ratings", ct);

        var ratings = await LoadRatingsAsync(ratingsPath, ct);
        log.LogInformation("IMDb ratings: {Count} записів", ratings.Count);

        var movies = 0;
        var series = 0;

        await foreach (var batch in StreamPatchesAsync(basicsPath, ratings, types, maxRows, ct))
        {
            foreach (var patch in batch)
                if (patch.Type == "movie") movies++; else series++;
            await ApplyPatchesAsync(batch, ct);
        }

        await UpdateSyncStateAsync(types, movies, series, ct);
        log.LogInformation("IMDb sync завершено: movies={Movies}, series={Series}", movies, series);
        return new ImdbSyncResult(movies, series, 0);
    }

    /// <summary>
    /// Злити патчі в titles (ідемпотентно). Npgsql → staging COPY + MERGE (швидкий шлях),
    /// інакше (тестовий InMemory) — EF-шлях через ImdbUpsertCore.
    /// </summary>
    public async Task<int> ApplyPatchesAsync(IEnumerable<ImdbTitlePatch> patches, CancellationToken ct = default)
    {
        var list = patches.ToList();
        if (list.Count == 0) return 0;

        if (db.Database.IsRelational() && db.Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            var batchSize = Math.Max(1000, _opt.BatchSize);
            var total = 0;
            foreach (var chunk in list.Chunk(batchSize))
                total += await StageAndMergeAsync(chunk.Select(ToStagingRow).ToList(), ct);
            return total;
        }

        // EF-шлях (тестовий провайдер): та сама логіка злиття через ImdbUpsertCore
        var patchList = list.ToList();
        var ids = patchList.Select(p => p.ImdbId).Distinct().ToList();
        var existing = await db.Titles
            .Where(t => t.ImdbId != null && ids.Contains(t.ImdbId))
            .ToListAsync(ct);

        var created = 0;
        foreach (var patch in patchList)
        {
            var (target, isNew) = ImdbUpsertCore.Resolve(existing, patch);
            if (isNew)
            {
                var entity = ImdbUpsertCore.ToEntity(patch);
                db.Titles.Add(entity);
                existing.Add(entity); // дублікати imdb_id у тому самому батчі зливаються в один рядок
                created++;
            }
        }
        await db.SaveChangesAsync(ct);
        return patchList.Count; // кожен патч або вставлений, або оновив наявний рядок
    }

    /// <summary>Рядок staging-запису з патча (жанри вже нормалізовані, popularity порахована).</summary>
    public static ImdbStagingRow ToStagingRow(ImdbTitlePatch p) => new(
        p.ImdbId, p.Type, p.TitleEn, p.TitleEn, p.IsAdult,
        p.Year, p.RuntimeMin, p.Genres, p.RatingImdb, p.VotesImdb, p.Popularity);

    // ─── Завантаження з кешем 24 год (source_cache + існування файлу) ───

    internal async Task<string> DownloadCachedAsync(string fileName, string url, string cacheKey, CancellationToken ct)
    {
        var dir = string.IsNullOrWhiteSpace(_opt.CacheDir)
            ? Path.Combine(Path.GetTempPath(), "movie-discovery", "imdb")
            : _opt.CacheDir;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);

        var freshMeta = await cache.GetAsync(Source, cacheKey, ct);
        if (File.Exists(path) && new FileInfo(path).Length > 0 && TryFetchedAt(freshMeta, out var at) &&
            DateTime.UtcNow - at < TimeSpan.FromHours(Math.Max(1, _opt.CacheTtlHours)))
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

        await cache.SetAsync(Source, cacheKey,
            JsonSerializer.Serialize(new ImdbCacheMeta(DateTime.UtcNow)), 86_400, ct);
        return path;
    }

    private sealed record ImdbCacheMeta([property: JsonPropertyName("fetchedAt")] DateTime FetchedAt);

    private static bool TryFetchedAt(string? json, out DateTime at)
    {
        at = default;
        if (json is null) return false;
        try
        {
            var meta = JsonSerializer.Deserialize<ImdbCacheMeta>(json);
            if (meta is null) return false;
            at = meta.FetchedAt;
            return true;
        }
        catch (JsonException) { return false; }
    }

    // ─── Потоковий парсинг ───

    internal async Task<Dictionary<string, (double Rating, int Votes)>> LoadRatingsAsync(string path, CancellationToken ct)
    {
        var dict = new Dictionary<string, (double, int)>(capacity: 2_000_000);
        await foreach (var row in TsvLinesAsync(path, ct))
        {
            var r = ImdbTsv.ParseRating(row);
            if (r is not null) dict[r.Tconst] = (r.AverageRating, r.NumVotes);
        }
        return dict;
    }

    /// <summary>Потоковий генератор патчів батчами (після фільтру типів; maxRows — глобальний ліміт).</summary>
    private async IAsyncEnumerable<List<ImdbTitlePatch>> StreamPatchesAsync(
        string basicsPath,
        Dictionary<string, (double Rating, int Votes)> ratings,
        IReadOnlySet<string> types,
        int? maxRows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var batch = new List<ImdbTitlePatch>(_opt.BatchSize);
        var emitted = 0;

        await foreach (var line in TsvLinesAsync(basicsPath, ct))
        {
            var basics = ImdbTsv.ParseBasics(line);
            if (basics is null) continue;
            var patch = ImdbMapper.ToPatch(basics, Map(ratings, basics.Tconst), types);
            if (patch is null) continue;

            batch.Add(patch);
            emitted++;
            if (batch.Count >= _opt.BatchSize || (maxRows is { } cap && emitted >= cap))
            {
                yield return batch;
                batch = new List<ImdbTitlePatch>(_opt.BatchSize);
                if (maxRows is { } c && emitted >= c) yield break;
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    private static ImdbRatingRow? Map(Dictionary<string, (double Rating, int Votes)> ratings, string tconst) =>
        ratings.TryGetValue(tconst, out var r) ? new ImdbRatingRow(tconst, r.Rating, r.Votes) : null;

    /// <summary>Розпаковка .gz → рядки TSV (стрім, без повного завантаження в пам'ять).</summary>
    private static async IAsyncEnumerable<string> TsvLinesAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        while (await reader.ReadLineAsync(ct) is { } line)
            yield return line;
    }

    // ─── Staging + MERGE (PostgreSQL) ───

    private async Task<int> StageAndMergeAsync(List<ImdbStagingRow> rows, CancellationToken ct)
    {
        await using var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen) await conn.OpenAsync(ct);
        try
        {
            await using (var truncate = new NpgsqlCommand(ImdbStaging.TruncateSql, conn))
                await truncate.ExecuteNonQueryAsync(ct);

            await using (var writer = await conn.BeginBinaryImportAsync(ImdbStaging.CopyFromStdinSql(), ct))
            {
                foreach (var r in rows)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(r.Tconst, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(r.TitleType, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(r.PrimaryTitle, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(r.OriginalTitle, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(r.IsAdult, NpgsqlDbType.Boolean, ct);
                    await WriteNullableInt(writer, r.StartYear, ct);
                    await WriteNullableInt(writer, r.RuntimeMinutes, ct);
                    await writer.WriteAsync(r.Genres, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                    await WriteNullableDouble(writer, r.AverageRating, ct);
                    await WriteNullableInt(writer, r.NumVotes, ct);
                    await writer.WriteAsync(r.Popularity, NpgsqlDbType.Double, ct);
                }
                await writer.CompleteAsync(ct);
            }

            await using var merge = new NpgsqlCommand(ImdbStaging.MergeSql + ";", conn);
            return await merge.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (!wasOpen) await conn.CloseAsync();
        }
    }

    private static async Task WriteNullableInt(NpgsqlBinaryImporter writer, int? value, CancellationToken ct) =>
        await writer.WriteAsync(value.HasValue ? (object)value.Value : DBNull.Value, NpgsqlDbType.Integer, ct);

    private static async Task WriteNullableDouble(NpgsqlBinaryImporter writer, double? value, CancellationToken ct) =>
        await writer.WriteAsync(value.HasValue ? (object)value.Value : DBNull.Value, NpgsqlDbType.Double, ct);

    // ─── sync_state ───

    private async Task UpdateSyncStateAsync(IReadOnlySet<string> types, int movies, int series, CancellationToken ct)
    {
        foreach (var kind in types)
        {
            var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == kind, ct);
            if (state is null)
            {
                state = new SyncState { Source = Source, Kind = kind };
                db.SyncStates.Add(state);
            }
            state.LastRunAt = DateTime.UtcNow;
            state.LastCount = kind == "movie" ? movies : series;
        }
        await db.SaveChangesAsync(ct);
    }

    internal static IReadOnlySet<string> NormalizeTypes(IReadOnlyCollection<string> mediaTypes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in mediaTypes)
        {
            var v = t.Trim().ToLowerInvariant();
            if (v is "movie" or "series") set.Add(v);
        }
        return set.Count == 0 ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "movie", "series" } : set;
    }
}
