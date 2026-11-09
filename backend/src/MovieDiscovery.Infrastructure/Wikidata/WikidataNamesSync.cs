using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace MovieDiscovery.Infrastructure.Wikidata;

/// <summary>Рядок Wikidata-синку: IMDb-ід (P345) → українська/російська назва + зображення (P18).</summary>
public sealed record WikidataNameRow(string ImdbId, string? Uk, string? Ru, string? Image);

/// <summary>
/// Staging wikidata_names_raw + батчевий UPDATE titles.title_uk/title_ru/poster_path.
/// SQL-константи — єдине джерело правди для міграції (DDL) і сервісу (COPY/UPDATE);
/// UNLOGGED ставить сервіс. Рішення власника 09.11: російська назва — ДОЗВОЛЕНИЙ
/// фолбек, коли офіційної української немає (пріоритет відображення uk → ru → en).
/// </summary>
public static class WikidataNamesStaging
{
    public const string TableName = "wikidata_names_raw";

    /// <summary>Порядок колонок для COPY — ЗБІГАЄТЬСЯ з DDL. poster_url — ГОТОВИЙ Commons-URL (thumbnail 500px).</summary>
    public static readonly string[] Columns = ["imdb_id", "uk", "ru", "poster_url"];

    /// <summary>DDL staging-таблиці (у міграції; тут — для smoke/тестів і довідки).</summary>
    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS wikidata_names_raw (
            imdb_id text NOT NULL,
            uk text NULL,
            ru text NULL,
            poster_url text NULL
        );
        """;

    public const string TruncateSql = "TRUNCATE TABLE wikidata_names_raw;";

    /// <summary>
    /// UPDATE titles за imdb_id (ідемпотентно): title_uk/title_ru перезаписуються Wikidata-значеннями
    /// (канонічні інтернет-назви, рішення власника), poster_path — ЛИШЕ якщо порожній (P18 не гірший
    /// за наявний постер TMDB/sweet.tv). JOIN за imdb_id — індекс ix_titles_imdb_id.
    /// </summary>
    public const string UpdateSql = """
        UPDATE titles t SET
            title_uk = COALESCE(NULLIF(s.uk, ''), t.title_uk),
            title_ru = COALESCE(NULLIF(s.ru, ''), t.title_ru),
            poster_path = COALESCE(t.poster_path, s.poster_url),
            updated_at = now()
        FROM wikidata_names_raw s
        WHERE t.imdb_id = s.imdb_id
          AND (   (s.uk IS NOT NULL AND (t.title_uk IS NULL OR t.title_uk <> s.uk))
               OR (s.ru IS NOT NULL AND (t.title_ru IS NULL OR t.title_ru <> s.ru))
               OR (s.poster_url IS NOT NULL AND t.poster_path IS NULL))
        """;

    /// <summary>URL зображення Commons з імені файлу (thumbnail ?width=500 — під розмір карток).</summary>
    public static string? PosterUrl(string? image) =>
        string.IsNullOrWhiteSpace(image)
            ? null
            : $"https://commons.wikimedia.org/wiki/Special:FilePath/{image.Replace(' ', '_')}?width=500";

    /// <summary>COPY-заголовок (binary): колонки в порядку Columns.</summary>
    public static string CopyFromStdinSql() =>
        $"COPY {TableName} ({string.Join(", ", Columns)}) FROM STDIN (FORMAT BINARY)";
}

/// <summary>Опції wikidata-синку (розділ Wikidata конфігу).</summary>
public sealed class WikidataNamesOptions
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

/// <summary>Результат прогону wikidata-names sync.</summary>
public sealed record WikidataNamesSyncResult(int Staged, int Updated, int Buckets);

/// <summary>
/// Назви і постери локального каталогу з ВІЛЬНОГО офіційного джерела — Wikidata Query
/// Service (без ключів). Пакетний SPARQL по префіксах tt00..tt39: елементи з P345 (IMDb id),
/// uk label, ru label (фолбек за рішенням власника), P18 (зображення) → staging COPY →
/// UPDATE titles (ідемпотентно, батчами; дроблення бакету при переповненні сторінки).
/// </summary>
public sealed class WikidataNamesService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    IOptions<WikidataNamesOptions> options,
    ILogger<WikidataNamesService> log)
{
    public const string Source = "wikidata-names";

    private readonly WikidataNamesOptions _opt = options.Value;

    /// <summary>
    /// SPARQL з VALUES-батчем наших imdb_id (точкові пошуки по P345 — швидкі й надійні;
    /// префіксні скани TT QS відкидає 403/504 — перевірено живцем). $$-префікс: { } літеральні.
    /// </summary>
    public static string SparqlValues(IReadOnlyCollection<string> ids)
    {
        var sb = new StringBuilder(ids.Count * 14 + 240);
        sb.Append("SELECT ?imdb ?uk ?ru ?img WHERE {\n  VALUES ?imdb { ");
        foreach (var id in ids)
            sb.Append('"').Append(id.Replace("\"", "")).Append("\" ");
        sb.Append("}\n  ?item wdt:P345 ?imdb .\n");
        sb.Append("  OPTIONAL { ?item rdfs:label ?uk . FILTER(LANG(?uk) = \"uk\") }\n");
        sb.Append("  OPTIONAL { ?item rdfs:label ?ru . FILTER(LANG(?ru) = \"ru\") }\n");
        sb.Append("  OPTIONAL { ?item wdt:P18 ?img . }\n");
        sb.Append("  FILTER(BOUND(?uk) || BOUND(?ru))\n}");
        return sb.ToString();
    }

    /// <summary>Повний прогін: батчі imdb_id каталогу → SPARQL VALUES → COPY → UPDATE → sync_state.</summary>
    public async Task<WikidataNamesSyncResult> RunAsync(int? maxRows, CancellationToken ct = default)
    {
        if (!IsNpgsql(db))
            throw new InvalidOperationException("wikidata-names sync потребує Npgsql-провайдера (staging COPY).");

        await SetUnloggedAsync(ct);

        var staged = 0;
        var updated = 0;
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

            List<WikidataNameRow> rows;
            try
            {
                rows = await FetchBatchAsync(batch, remaining, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Батч не взявся навіть після ретраїв — пропускаємо і йдемо далі:
                // sync ідемпотентний, повторний прогін добере пропущене.
                log.LogWarning(ex, "wikidata-names: батч #{N} пропущено після ретраїв", batches + 1);
                batches++;
                skipped += batch.Count;
                continue;
            }
            batches++;
            staged += rows.Count;
            if (rows.Count > 0)
                updated += await StageAndUpdateAsync(rows, ct);
            log.LogInformation("wikidata-names: батч #{N}: запитано={Asked}, з Wikidata={Rows} (staged={Staged}, updated={Updated})",
                batches, batch.Count, rows.Count, staged, updated);
            if (maxRows is { } c && staged >= c) break;
            await Task.Delay(_opt.RequestDelayMs, ct);
        }

        await ExecuteWithRetryAsync(WikidataNamesStaging.TruncateSql, ct);

        await UpdateSyncStateAsync(staged, updated, ct);
        log.LogInformation("Wikidata names sync завершено: staged={Staged}, updated={Updated}, batches={Batches}, skipped={Skipped}",
            staged, updated, batches, skipped);
        return new WikidataNamesSyncResult(staged, updated, batches);
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

    private async Task<List<WikidataNameRow>> FetchBatchAsync(List<string> ids, int? maxRows, CancellationToken ct)
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
            // (перевірено живцем: той самий запит 403 у сервісі, 200 curl-ом за хвилину)
            if ((int)resp.StatusCode is 429 or 503 or 502 or 504 or 403)
            {
                if (attempt >= maxAttempts)
                    throw new HttpRequestException($"Wikidata QS {resp.StatusCode} після {attempt} спроб");
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30 * attempt);
                log.LogWarning("wikidata-names: QS {Status} — пауза {Retry}s (спроба {Attempt}/{Max})",
                    (int)resp.StatusCode, retryAfter.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(retryAfter, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return ParseCsv(stream, maxRows);
        }
    }

    /// <summary>Мінімальний RFC4180-парсер SPARQL CSV (quoted-поля з комами/лапками/переносами).</summary>
    public static List<WikidataNameRow> ParseCsv(Stream stream, int? maxRows)
    {
        var rows = new List<WikidataNameRow>();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var header = CsvReader.ReadCsvLine(reader);
        if (header is null || header.Count == 0) return rows;

        var imdbIdx = header.FindIndex(h => h.Trim() == "imdb");
        var ukIdx = header.FindIndex(h => h.Trim() == "uk");
        var ruIdx = header.FindIndex(h => h.Trim() == "ru");
        var imgIdx = header.FindIndex(h => h.Trim() == "img");
        if (imdbIdx < 0) return rows;

        List<string>? fields;
        while ((fields = CsvReader.ReadCsvLine(reader)) is not null)
        {
            var imdb = Field(fields, imdbIdx).Trim();
            if (imdb.Length == 0) continue;

            var uk = Field(fields, ukIdx).Trim();
            var ru = Field(fields, ruIdx).Trim();
            if (uk.Length == 0 && ru.Length == 0) continue; // немає жодної потрібної назви

            var file = ExtractCommonsFile(Field(fields, imgIdx).Trim());
            rows.Add(new WikidataNameRow(imdb, uk.Length > 0 ? uk : null, ru.Length > 0 ? ru : null, file));
            if (maxRows is { } cap && rows.Count >= cap) break;
        }
        return rows;
    }

    private static string Field(List<string> f, int i) => i >= 0 && i < f.Count ? f[i] : "";

    /// <summary>P18 IRI виду http://commons.wikimedia.org/wiki/Special:FilePath/&lt;file&gt; → ім'я файлу.</summary>
    public static string? ExtractCommonsFile(string iri)
    {
        if (string.IsNullOrWhiteSpace(iri)) return null;
        const string marker = "/wiki/Special:FilePath/";
        var idx = iri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var file = Uri.UnescapeDataString(iri[(idx + marker.Length)..]);
        var cut = file.IndexOfAny(['?', '#']); // хвостові query/фрагменти дзеркал
        if (cut >= 0) file = file[..cut];
        file = file.Replace('_', ' ').Trim();
        if (file.Length == 0) return null;

        // Commons зберігає і PDF/DJVU/городні формати — беремо лише растрові зображення.
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".tif" or ".tiff" ? file : null;
    }

    /// <summary>COPY батчу в staging + UPDATE titles (ідемпотентно; ретрай транзитних збоїв PG).</summary>
    public async Task<int> StageAndUpdateAsync(List<WikidataNameRow> rows, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
                await conn.OpenAsync(ct);

                await using (var sc = new NpgsqlCommand("SET synchronous_commit TO OFF", conn))
                    await sc.ExecuteNonQueryAsync(ct);

                await using (var truncate = new NpgsqlCommand(WikidataNamesStaging.TruncateSql, conn))
                    await truncate.ExecuteNonQueryAsync(ct);

                await using (var writer = await conn.BeginBinaryImportAsync(WikidataNamesStaging.CopyFromStdinSql(), ct))
                {
                    foreach (var r in rows)
                    {
                        await writer.StartRowAsync(ct);
                        await writer.WriteAsync(r.ImdbId, NpgsqlDbType.Text, ct);
                        WriteNullable(writer, r.Uk, ct);
                        WriteNullable(writer, r.Ru, ct);
                        WriteNullable(writer, WikidataNamesStaging.PosterUrl(r.Image), ct);
                    }
                    await writer.CompleteAsync(ct);
                }

                await using var update = new NpgsqlCommand(WikidataNamesStaging.UpdateSql + ";", conn);
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

    private static async Task WriteNullable(NpgsqlBinaryImporter writer, string? value, CancellationToken ct)
    {
        if (value is null) await writer.WriteAsync(DBNull.Value, NpgsqlDbType.Text, ct);
        else await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
    }

    private async Task SetUnloggedAsync(CancellationToken ct) =>
        await ExecuteWithRetryAsync("ALTER TABLE IF EXISTS wikidata_names_raw SET UNLOGGED", ct);

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
