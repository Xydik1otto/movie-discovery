using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace MovieDiscovery.Infrastructure.Series;

/// <summary>Рядок синку серій: IMDb-ід (P345) → серія (P179 QID + назви) + prev/next (P155/P156).</summary>
public sealed record TitleSeriesRow(
    string ImdbId,
    string SeriesQid,
    string? LabelUk,
    string? LabelRu,
    string? LabelEn,
    string? PrevImdb,
    string? NextImdb);

/// <summary>
/// Staging title_series_raw + upsert title_series (INSERT ... ON CONFLICT (imdb_id) DO UPDATE).
/// SQL-константи — єдине джерело правди для міграції (DDL) і сервісу (COPY/UPSERT);
/// UNLOGGED ставить сервіс. Перезапис колонок серії ПОВНИЙ — дані Wikidata авторитетні.
/// </summary>
public static class TitleSeriesStaging
{
    public const string TableName = "title_series_raw";

    /// <summary>Порядок колонок для COPY — ЗБІГАЄТЬСЯ з DDL.</summary>
    public static readonly string[] Columns =
        ["imdb_id", "series_qid", "label_uk", "label_ru", "label_en", "prev_imdb", "next_imdb"];

    /// <summary>DDL staging-таблиці (у міграції; тут — для довідки і smoke-перевірок).</summary>
    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS title_series_raw (
            imdb_id text NOT NULL,
            series_qid text NOT NULL,
            label_uk text NULL,
            label_ru text NULL,
            label_en text NULL,
            prev_imdb text NULL,
            next_imdb text NULL
        );
        """;

    public const string TruncateSql = "TRUNCATE TABLE title_series_raw;";

    /// <summary>
    /// Upsert у title_series за imdb_id (PK): перезапис УСІХ колонок серії кожним прогоном
    /// (Wikidata — авторитетне джерело; P179 може зникнути → NULL-и теж перезаписуються).
    /// </summary>
    public const string UpsertSql = """
        INSERT INTO title_series (imdb_id, series_qid, label_uk, label_ru, label_en, prev_imdb, next_imdb, updated_at)
        SELECT s.imdb_id, s.series_qid, s.label_uk, s.label_ru, s.label_en, s.prev_imdb, s.next_imdb, now()
        FROM title_series_raw s
        ON CONFLICT (imdb_id) DO UPDATE SET
            series_qid = EXCLUDED.series_qid,
            label_uk = EXCLUDED.label_uk,
            label_ru = EXCLUDED.label_ru,
            label_en = EXCLUDED.label_en,
            prev_imdb = EXCLUDED.prev_imdb,
            next_imdb = EXCLUDED.next_imdb,
            updated_at = now()
        """;

    /// <summary>COPY-заголовок (binary): колонки в порядку Columns.</summary>
    public static string CopyFromStdinSql() =>
        $"COPY {TableName} ({string.Join(", ", Columns)}) FROM STDIN (FORMAT BINARY)";
}

/// <summary>Опції синку серій (розділ TitleSeries конфігу).</summary>
public sealed class TitleSeriesSyncOptions
{
    /// <summary>SPARQL-endpoint Query Service.</summary>
    public string Endpoint { get; set; } = "https://query.wikidata.org/sparql";

    /// <summary>User-Agent (політика Wikimedia: контактний рядок обов'язковий).</summary>
    public string UserAgent { get; set; } = "KinoNavigator/1.0 (movie-discovery; contact via repo issues)";

    /// <summary>Скільки imdb_id каталогу запитувати за один SPARQL VALUES-запит (POST form).</summary>
    public int BatchSize { get; set; } = 4000;

    /// <summary>Пауза між SPARQL-запитами (ввічливість до Query Service).</summary>
    public int RequestDelayMs { get; set; } = 700;
}

/// <summary>Результат прогону title-series sync.</summary>
public sealed record TitleSeriesSyncResult(int Staged, int Upserted, int Batches);

/// <summary>
/// Групування тайтлів за франшизами («фільми з тієї ж лінійки») з ВІЛЬНОГО джерела —
/// Wikidata Query Service (без ключів). Батчевий SPARQL по imdb_id каталогу (P345):
/// P179 (part of the series) + назви серії uk/ru/en + P155/P156 (previous/next part
/// → їхні imdb_id). FILTER(BOUND(?series)) — лише члени франшиз. Staging COPY →
/// upsert title_series. Патерн — WikidataNamesService (незалежний синк).
/// </summary>
public sealed class TitleSeriesSyncService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    IOptions<TitleSeriesSyncOptions> options,
    ILogger<TitleSeriesSyncService> log)
{
    public const string Source = "title-series";

    private readonly TitleSeriesSyncOptions _opt = options.Value;

    /// <summary>
    /// SPARQL з VALUES-батчем наших imdb_id: серія (P179, OPTIONAL + BOUND-фільтр),
    /// назви серії uk/ru/en, prev/next частини (P155/P156 → їхні P345 imdb_id).
    /// $$-префікс: { } літеральні.
    /// </summary>
    public static string SparqlValues(IReadOnlyCollection<string> ids)
    {
        var sb = new StringBuilder(ids.Count * 14 + 480);
        sb.Append("SELECT ?imdb ?series ?luk ?lru ?len ?prev ?next WHERE {\n  VALUES ?imdb { ");
        foreach (var id in ids)
            sb.Append('"').Append(id.Replace("\"", "")).Append("\" ");
        sb.Append("}\n  ?item wdt:P345 ?imdb .\n");
        sb.Append("  OPTIONAL { ?item wdt:P179 ?series . }\n");
        sb.Append("  OPTIONAL { ?series rdfs:label ?luk . FILTER(LANG(?luk) = \"uk\") }\n");
        sb.Append("  OPTIONAL { ?series rdfs:label ?lru . FILTER(LANG(?lru) = \"ru\") }\n");
        sb.Append("  OPTIONAL { ?series rdfs:label ?len . FILTER(LANG(?len) = \"en\") }\n");
        sb.Append("  OPTIONAL { ?item wdt:P155 ?prevItem . ?prevItem wdt:P345 ?prev . }\n");
        sb.Append("  OPTIONAL { ?item wdt:P156 ?nextItem . ?nextItem wdt:P345 ?next . }\n");
        sb.Append("  FILTER(BOUND(?series))\n}");
        return sb.ToString();
    }

    /// <summary>Повний прогін: батчі imdb_id каталогу → SPARQL VALUES → COPY → upsert → sync_state.</summary>
    public async Task<TitleSeriesSyncResult> RunAsync(int? maxRows, CancellationToken ct = default)
    {
        if (!IsNpgsql(db))
            throw new InvalidOperationException("title-series sync потребує Npgsql-провайдера (staging COPY).");

        await SetUnloggedAsync(ct);

        var staged = 0;
        var upserted = 0;
        var batches = 0;
        var skipped = 0;

        var ids = db.Titles.AsNoTracking()
            .Where(t => t.ImdbId != null && t.ImdbId != "")
            .OrderBy(t => t.Id)
            .Select(t => t.ImdbId!);
        foreach (var batch in Batch(ids, _opt.BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var remaining = maxRows is { } cap ? cap - staged : (int?)null;
            if (remaining is <= 0) break; // smoke-ліміт вичерпано (ЗАГАЛЬНИЙ по всіх батчах)

            List<TitleSeriesRow> rows;
            try
            {
                rows = await FetchBatchAsync(batch, remaining, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Батч не взявся навіть після ретраїв — пропускаємо і йдемо далі:
                // sync ідемпотентний, повторний прогін добере пропущене.
                log.LogWarning(ex, "title-series: батч #{N} пропущено після ретраїв", batches + 1);
                batches++;
                skipped += batch.Count;
                continue;
            }
            batches++;
            staged += rows.Count;
            if (rows.Count > 0)
                upserted += await StageAndUpsertAsync(rows, ct);
            log.LogInformation("title-series: батч #{N}: запитано={Asked}, з Wikidata={Rows} (staged={Staged}, upserted={Upserted})",
                batches, batch.Count, rows.Count, staged, upserted);
            if (maxRows is { } c && staged >= c) break;
            await Task.Delay(_opt.RequestDelayMs, ct);
        }

        await ExecuteWithRetryAsync(TitleSeriesStaging.TruncateSql, ct);

        await UpdateSyncStateAsync(staged, upserted, ct);
        log.LogInformation("Title-series sync завершено: staged={Staged}, upserted={Upserted}, batches={Batches}, skipped={Skipped}",
            staged, upserted, batches, skipped);
        return new TitleSeriesSyncResult(staged, upserted, batches);
    }

    /// <summary>Батчі ідентифікаторів (materialize кожен батч — один SPARQL-запит на батч).</summary>
    public static IEnumerable<List<string>> Batch(IEnumerable<string> source, int size)
    {
        var batch = new List<string>(size);
        foreach (var id in source)
        {
            batch.Add(id);
            if (batch.Count >= size)
            {
                yield return batch;
                batch = new List<string>(size);
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    private async Task<List<TitleSeriesRow>> FetchBatchAsync(List<string> ids, int? maxRows, CancellationToken ct)
    {
        var query = SparqlValues(ids);
        var maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _opt.Endpoint);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
            req.Headers.UserAgent.ParseAdd(_opt.UserAgent);
            req.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("query", query)]);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            // Wikimedia edge буває блік некрупними 403/502 навіть для валідних запитів
            // (перевірено живцем на wikidata-names: той самий запит 403 у сервісі,
            // 200 curl-ом за хвилину).
            if ((int)resp.StatusCode is 429 or 503 or 502 or 504 or 403)
            {
                if (attempt >= maxAttempts)
                    throw new HttpRequestException($"Wikidata QS {resp.StatusCode} після {attempt} спроб");
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30 * attempt);
                log.LogWarning("title-series: QS {Status} — пауза {Retry}s (спроба {Attempt}/{Max})",
                    (int)resp.StatusCode, retryAfter.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(retryAfter, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return ParseCsv(stream, maxRows);
        }
    }

    /// <summary>
    /// Мінімальний RFC4180-парсер SPARQL CSV (quoted-поля з комами/лапками/переносами).
    /// Дублі imdb_id (тайтл у кількох серіях / кілька prev-next) — ПЕРШИЙ рядок виграє:
    /// в one-row-per-imdb моделі дві однакові ссылке в одному INSERT дають 21000.
    /// </summary>
    public static List<TitleSeriesRow> ParseCsv(Stream stream, int? maxRows)
    {
        var rows = new List<TitleSeriesRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var header = CsvReader.ReadCsvLine(reader);
        if (header is null || header.Count == 0) return rows;

        var imdbIdx = header.FindIndex(h => h.Trim() == "imdb");
        var seriesIdx = header.FindIndex(h => h.Trim() == "series");
        var lukIdx = header.FindIndex(h => h.Trim() == "luk");
        var lruIdx = header.FindIndex(h => h.Trim() == "lru");
        var lenIdx = header.FindIndex(h => h.Trim() == "len");
        var prevIdx = header.FindIndex(h => h.Trim() == "prev");
        var nextIdx = header.FindIndex(h => h.Trim() == "next");
        if (imdbIdx < 0 || seriesIdx < 0) return rows;

        List<string>? fields;
        while ((fields = CsvReader.ReadCsvLine(reader)) is not null)
        {
            var imdb = Field(fields, imdbIdx).Trim();
            var qid = ExtractQid(Field(fields, seriesIdx).Trim());
            if (imdb.Length == 0 || qid is null) continue;   // без серії — не член франшизи
            if (!seen.Add(imdb)) continue;                    // дубль imdb — перший рядок виграє

            var luk = Field(fields, lukIdx).Trim();
            var lru = Field(fields, lruIdx).Trim();
            var len = Field(fields, lenIdx).Trim();
            var prev = Field(fields, prevIdx).Trim();
            var next = Field(fields, nextIdx).Trim();
            rows.Add(new TitleSeriesRow(
                imdb, qid,
                luk.Length > 0 ? luk : null,
                lru.Length > 0 ? lru : null,
                len.Length > 0 ? len : null,
                prev.Length > 0 ? prev : null,
                next.Length > 0 ? next : null));
            if (maxRows is { } cap && rows.Count >= cap) break;
        }
        return rows;
    }

    private static string Field(List<string> f, int i) => i >= 0 && i < f.Count ? f[i] : "";

    /// <summary>IRI серії виду http://www.wikidata.org/entity/Q123456 → QID «Q123456».</summary>
    public static string? ExtractQid(string iri)
    {
        if (string.IsNullOrWhiteSpace(iri)) return null;
        const string marker = "/entity/";
        var idx = iri.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var qid = iri[(idx + marker.Length)..].Trim();
        // валідний QID: Q + цифри (інакше сміття/неповний IRI)
        return qid.Length > 1 && qid[0] == 'Q' && qid[1..].All(char.IsAsciiDigit) ? qid : null;
    }

    /// <summary>COPY батчу в staging + upsert title_series (ідемпотентно; ретрай транзитних збоїв PG).</summary>
    public async Task<int> StageAndUpsertAsync(List<TitleSeriesRow> rows, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Окреме з'єднання (НЕ спільне EF): synchronous_commit OFF + COPY.
                await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
                await conn.OpenAsync(ct);

                await using (var sc = new NpgsqlCommand("SET synchronous_commit TO OFF", conn))
                    await sc.ExecuteNonQueryAsync(ct);

                await using (var truncate = new NpgsqlCommand(TitleSeriesStaging.TruncateSql, conn))
                    await truncate.ExecuteNonQueryAsync(ct);

                await using (var writer = await conn.BeginBinaryImportAsync(TitleSeriesStaging.CopyFromStdinSql(), ct))
                {
                    foreach (var r in rows)
                    {
                        await writer.StartRowAsync(ct);
                        await writer.WriteAsync(r.ImdbId, NpgsqlDbType.Text, ct);
                        await writer.WriteAsync(r.SeriesQid, NpgsqlDbType.Text, ct);
                        WriteNullable(writer, r.LabelUk, ct);
                        WriteNullable(writer, r.LabelRu, ct);
                        WriteNullable(writer, r.LabelEn, ct);
                        WriteNullable(writer, r.PrevImdb, ct);
                        WriteNullable(writer, r.NextImdb, ct);
                    }
                    await writer.CompleteAsync(ct);
                }

                await using var upsert = new NpgsqlCommand(TitleSeriesStaging.UpsertSql + ";", conn);
                return await upsert.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (attempt < 3 && PgRetry.IsTransient(ex))
            {
                log.LogWarning("Транзитна помилка PG (спроба {Attempt}/3, {SqlState}): {Msg} — ретрай",
                    attempt, ex.SqlState, ex.MessageText);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), ct);
            }
        }
    }

    private static async Task WriteNullable(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value is null) await writer.WriteAsync(DBNull.Value, NpgsqlDbType.Text, ct);
        else await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
    }

    private async Task SetUnloggedAsync(CancellationToken ct) =>
        await ExecuteWithRetryAsync("ALTER TABLE IF EXISTS title_series_raw SET UNLOGGED", ct);

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

    private async Task UpdateSyncStateAsync(int staged, int upserted, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "original", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "original" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = upserted;
        state.LastPages = staged;
        await db.SaveChangesAsync(ct);
    }

    internal static bool IsNpgsql(MovieDiscoveryDbContext db) =>
        db.Database.IsRelational() && db.Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Читач CSV: одне логічне «рядок» за виклик; quoted-поля можуть містити переноси рядка.</summary>
internal static class CsvReader
{
    public static List<string>? ReadCsvLine(StreamReader reader)
    {
        var line = reader.ReadLine();
        if (line is null) return null;

        var (values, openEnded, raw) = SplitCsv(line);
        // незакрита лапка → поле триває на наступних рядках (SPARQL CSV дозволяє переноси всередині)
        while (openEnded)
        {
            var next = reader.ReadLine();
            if (next is null) break;
            (values, openEnded, raw) = SplitCsv(raw + "\n" + next);
        }
        return values;
    }

    private static (List<string> Values, bool OpenEnded, string Raw) SplitCsv(string line)
    {
        var values = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                sb.Append(c); i++; continue;
            }
            switch (c)
            {
                case '"':
                    inQuotes = true; i++; break;
                case ',':
                    values.Add(sb.ToString()); sb.Clear(); i++; break;
                default:
                    sb.Append(c); i++; break;
            }
        }
        values.Add(sb.ToString());
        return (values, inQuotes, line);
    }
}

// ─── Черга + фонова обробка (патерн SyncQueue/VodSyncQueue) ───

/// <summary>Джоба title-series sync: P179-серії + prev/next → title_series.</summary>
public sealed record TitleSeriesSyncJob(int? MaxRows);

/// <summary>Черга title-series-синхронізації (202 → фонова джоба).</summary>
public sealed class TitleSeriesSyncQueue
{
    private readonly System.Threading.Channels.Channel<TitleSeriesSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<TitleSeriesSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(TitleSeriesSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<TitleSeriesSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка title-series-джоб (SPARQL-батчі → staging → upsert).</summary>
public sealed class TitleSeriesSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TitleSeriesSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<TitleSeriesSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<TitleSeriesSyncService>();
                var result = await service.RunAsync(job.MaxRows, stoppingToken);
                log.LogInformation("Title-series джоба завершена: staged={Staged}, upserted={Upserted}, batches={Batches}",
                    result.Staged, result.Upserted, result.Batches);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка title-series-синхронізації (maxRows={MaxRows})", job.MaxRows);
            }
        }
    }
}
