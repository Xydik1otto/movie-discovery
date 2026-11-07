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

/// <summary>Рядок title.akas.tsv (IMDb datasets) після фільтрів FR-19.</summary>
public sealed record ImdbAkasRow(string ImdbId, string Region);

/// <summary>
/// Потоковий парсер title.akas.tsv.gz (FR-19). Колонки: 0=titleId, 1=ordering, 2=title,
/// 3=region, 4=language, 5=types, 6=attributes, 7=isOriginalTitle, 8=comment.
/// ЛИШЕ isOriginalTitle=1; region — валідний ISO-2 (2 великі ASCII-літери);
/// «\N», XWW/XEU-подібні псевдокоди та порожні — SKIP.
/// </summary>
public static class ImdbAkasParser
{
    public const int ColTitleId = 0;
    public const int ColRegion = 3;
    public const int ColIsOriginalTitle = 7;

    /// <summary>Розібрати рядок akas; рядок не відповідає фільтрам → null.</summary>
    public static ImdbAkasRow? ParseRow(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var p = line.Split('\t');
        if (p.Length <= ColIsOriginalTitle) return null;

        var titleId = p[ColTitleId].Trim();
        if (titleId.Length == 0 || titleId == ImdbTsv.NullField) return null;
        if (p[ColIsOriginalTitle].Trim() != "1") return null; // лише регіон оригінальної назви

        var region = p[ColRegion].Trim();
        if (!IsValidRegion(region)) return null; // \N / XWW / XEU / порожній
        return new ImdbAkasRow(titleId, region);
    }

    /// <summary>Валідний ISO-3166 alpha-2 код: рівно 2 великі ASCII-літери.</summary>
    public static bool IsValidRegion(string? region) =>
        region is { Length: 2 }
        && region.All(c => c is >= 'A' and <= 'Z');
}

/// <summary>
/// Staging imdb_akas_raw + батчевий UPDATE titles.country (FR-19). SQL-константи —
/// єдине джерело правди для міграції (DDL) і сервісу (COPY/UPDATE); тестуються на clauses.
/// </summary>
public static class ImdbAkasStaging
{
    public const string TableName = "imdb_akas_raw";

    /// <summary>Порядок колонок для COPY — ЗБІГАЄТЬСЯ з DDL.</summary>
    public static readonly string[] Columns = ["imdb_id", "region"];

    /// <summary>DDL staging-таблиці (у міграції; тут — для smoke/тестів і довідки). UNLOGGED ставить сервіс.</summary>
    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS imdb_akas_raw (
            imdb_id text NOT NULL,
            region text NOT NULL
        );
        """;

    public const string DropTableSql = "DROP TABLE IF EXISTS imdb_akas_raw;";

    public const string TruncateSql = "TRUNCATE TABLE imdb_akas_raw;";

    /// <summary>
    /// UPDATE titles.country за imdb_id батчами (ідемпотентно: лише фактичні зміни;
    /// JOIN за imdb_id — індекс ix_titles_imdb_id; title_uk/type/назви НЕ чіпаються).
    /// </summary>
    public const string UpdateSql = """
        UPDATE titles t SET
            country = s.region,
            updated_at = now()
        FROM imdb_akas_raw s
        WHERE t.imdb_id = s.imdb_id
          AND (t.country IS NULL OR t.country <> s.region)
        """;

    /// <summary>COPY-заголовок (binary): колонки в порядку Columns.</summary>
    public static string CopyFromStdinSql() =>
        $"COPY {TableName} ({string.Join(", ", Columns)}) FROM STDIN (FORMAT BINARY)";
}

/// <summary>Опції imdb-akas sync (розділ Imdb конфігу).</summary>
public sealed class ImdbAkasOptions
{
    /// <summary>Каталог для кешування .gz-файлів; порожньо → {TMPDIR}/movie-discovery/imdb.</summary>
    public string CacheDir { get; set; } = "";

    /// <summary>Скільки годин скачаний файл вважається свіжим.</summary>
    public int CacheTtlHours { get; set; } = 24;

    /// <summary>Розмір батчу staging COPY → UPDATE.</summary>
    public int BatchSize { get; set; } = 50_000;
}

/// <summary>Результат прогону imdb-akas sync.</summary>
public sealed record ImdbAkasSyncResult(int Staged, int Updated);

/// <summary>
/// FR-19: країна виробника з IMDb title.akas (isOriginalTitle=1) → titles.country.
/// Патерн ImdbBulkService: потокове завантаження .gz (24-год кеш файлу), батчі 50k →
/// PG COPY у UNLOGGED staging → UPDATE titles за imdb_id (ідемпотентно) → TRUNCATE.
/// Харденінг: ретрай транзитних збоїв PG, synchronous_commit OFF. Для non-Npgsql
/// провайдера (тестовий InMemory) — EF-шлях.
/// </summary>
public sealed class ImdbAkasService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    ICacheStore cache,
    IOptions<ImdbAkasOptions> options,
    ILogger<ImdbAkasService> log)
{
    public const string Source = "imdb-akas";
    public const string AkasUrl = "https://datasets.imdbws.com/title.akas.tsv.gz";

    private readonly ImdbAkasOptions _opt = options.Value;
    private readonly ImdbFileCache _files = new(http, cache, log);

    /// <summary>Повний прогін: download → parse → stage/update батчами → sync_state.</summary>
    public async Task<ImdbAkasSyncResult> RunAsync(int? maxRows, CancellationToken ct = default)
    {
        var path = await _files.DownloadAsync(
            Source, "title.akas.tsv.gz", AkasUrl, "file:akas", _opt.CacheDir, _opt.CacheTtlHours, ct);

        var isNpgsql = IsNpgsql(db);
        if (isNpgsql)
            await SetUnloggedAsync(ct);

        var staged = 0;
        var updated = 0;
        await foreach (var batch in StreamBatchesAsync(path, maxRows, ct))
        {
            staged += batch.Count;
            updated += await StageAndUpdateAsync(batch, isNpgsql, ct);
        }

        // фінальне чищення staging (кожен батч уже TRUNCATE-иться на вході наступного)
        if (isNpgsql)
            await ExecuteWithRetryAsync(ImdbAkasStaging.TruncateSql, ct);

        await UpdateSyncStateAsync(staged, updated, ct);
        log.LogInformation("IMDb akas sync завершено: staged={Staged}, updated={Updated}", staged, updated);
        return new ImdbAkasSyncResult(staged, updated);
    }

    /// <summary>Потоковий генератор батчів (isOriginalTitle=1, валідний region; maxRows — ліміт).</summary>
    internal async IAsyncEnumerable<List<ImdbAkasRow>> StreamBatchesAsync(
        string path, int? maxRows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var batch = new List<ImdbAkasRow>(_opt.BatchSize);
        var emitted = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal); // дублікати tconst усередині батчу
        var first = true;

        await foreach (var line in ImdbGzip.TsvLinesAsync(path, ct))
        {
            if (first) { first = false; continue; } // header

            var row = ImdbAkasParser.ParseRow(line);
            if (row is null) continue;
            if (!seen.Add(row.ImdbId)) continue; // перший оригінальний рядок tconst виграє

            batch.Add(row);
            emitted++;
            if (batch.Count >= _opt.BatchSize || (maxRows is { } cap && emitted >= cap))
            {
                yield return batch;
                batch = new List<ImdbAkasRow>(_opt.BatchSize);
                seen.Clear();
                if (maxRows is { } c && emitted >= c) yield break;
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>
    /// Злити батч у titles.country (ідемпотентно). Npgsql → staging COPY + UPDATE
    /// (швидкий шлях), інакше (тестовий InMemory) — EF-шлях.
    /// </summary>
    public async Task<int> StageAndUpdateAsync(List<ImdbAkasRow> rows, bool isNpgsql, CancellationToken ct = default)
    {
        if (rows.Count == 0) return 0;

        if (!isNpgsql)
        {
            var ids = rows.Select(r => r.ImdbId).Distinct().ToList();
            var titles = await db.Titles
                .Where(t => t.ImdbId != null && ids.Contains(t.ImdbId))
                .ToListAsync(ct);
            var byId = titles.GroupBy(t => t.ImdbId!).ToDictionary(g => g.Key, g => g.First());

            var updated = 0;
            foreach (var row in rows)
            {
                if (!byId.TryGetValue(row.ImdbId, out var title)) continue;
                if (title.Country == row.Region) continue; // ідемпотентність
                title.Country = row.Region;
                title.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
            await db.SaveChangesAsync(ct);
            return updated;
        }

        // Ретрай транзитних збоїв PG; весь StageAndUpdate ідемпотентний:
        // TRUNCATE на вході чистить сміття попередньої спроби.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Окреме з'єднання з пулу: спільне EF-з'єднання контексту не чіпаємо.
                await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
                await conn.OpenAsync(ct);

                // Масове завантаження відновлюваних даних: синхронний fsync не потрібен.
                await using (var sc = new NpgsqlCommand("SET synchronous_commit TO OFF", conn))
                    await sc.ExecuteNonQueryAsync(ct);

                await using (var truncate = new NpgsqlCommand(ImdbAkasStaging.TruncateSql, conn))
                    await truncate.ExecuteNonQueryAsync(ct);

                await using (var writer = await conn.BeginBinaryImportAsync(ImdbAkasStaging.CopyFromStdinSql(), ct))
                {
                    foreach (var r in rows)
                    {
                        await writer.StartRowAsync(ct);
                        await writer.WriteAsync(r.ImdbId, NpgsqlDbType.Text, ct);
                        await writer.WriteAsync(r.Region, NpgsqlDbType.Text, ct);
                    }
                    await writer.CompleteAsync(ct);
                }

                await using var update = new NpgsqlCommand(ImdbAkasStaging.UpdateSql + ";", conn);
                return await update.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (attempt < 3 && PgRetry.IsTransient(ex))
            {
                log.LogWarning("Транзитна помилка PG (спроба {Attempt}/3, {SqlState}): {Msg} — ретрай",
                    attempt, ex.SqlState, ex.MessageText);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), ct);
            }
        }
    }

    /// <summary>Staging — відновлювані проміжні дані: UNLOGGED різко зменшує тиск на WAL під час COPY.</summary>
    private async Task SetUnloggedAsync(CancellationToken ct) =>
        await ExecuteWithRetryAsync("ALTER TABLE IF EXISTS imdb_akas_raw SET UNLOGGED", ct);

    private async Task ExecuteWithRetryAsync(string sql, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (PostgresException ex) when (attempt < 3 && PgRetry.IsTransient(ex))
            {
                log.LogWarning("Транзитна помилка PG (спроба {Attempt}/3, {SqlState}): {Msg} — ретрай",
                    attempt, ex.SqlState, ex.MessageText);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), ct);
            }
        }
    }

    private async Task UpdateSyncStateAsync(int staged, int updated, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "original", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "original" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = updated;
        state.LastPages = staged;
        await db.SaveChangesAsync(ct);
    }

    internal static bool IsNpgsql(MovieDiscoveryDbContext db) =>
        db.Database.IsRelational() && db.Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase);
}
