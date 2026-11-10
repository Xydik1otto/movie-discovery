using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Wikidata; // internal CsvReader — той самий асемблі
using Npgsql;
using NpgsqlTypes;

namespace MovieDiscovery.Infrastructure.Wikipedia;

/// <summary>
/// Рядок SPARQL-відповіді: IMDb-ід (P345) → QID елемента + назви статей uk/ru/en вікі
/// (sitelink-триплети РОЗДІЛЕНІ OPTIONAL-блоками — ';'-ланцюг дає порожні sitelinks, перевірено живцем).
/// </summary>
public sealed record WikipediaSparqlRow(string ImdbId, string Qid, string? En, string? Uk, string? Ru);

/// <summary>Рядок Wikipedia-синку: imdb_id → готовий URL постера + мова вікі + QID (якір ідентичності).</summary>
public sealed record WikipediaPosterRow(string ImdbId, string? PosterUrl, string? Lang, string? Qid);

/// <summary>
/// Сторінка з відповіді MediaWiki prop=pageimages (formatversion=2): title — ФІНАЛЬНА назва
/// (після normalized/redirects), thumbnail/original — URL зображення, Missing — сторінки немає.
/// </summary>
public sealed record WikiPageImage(string Title, string? Thumbnail, string? Original, bool Missing);

/// <summary>
/// Staging wikipedia_posters_raw + батчевий fill-only UPDATE titles (poster_path).
/// SQL-константи — єдине джерело правди для міграції (DDL) і сервісу (COPY/UPDATE);
/// UNLOGGED ставить сервіс. Міграцію/DDL додає оркестратор (CREATE TABLE IF NOT EXISTS
/// imdb_id/poster_url/lang/qid — текст у WikipediaPosterStaging.CreateTableSql).
/// </summary>
public static class WikipediaPosterStaging
{
    public const string TableName = "wikipedia_posters_raw";

    /// <summary>Порядок колонок для COPY — ЗБІГАЄТЬСЯ з DDL. poster_url — ГОТОВИЙ URL (thumbnail 500px або original).</summary>
    public static readonly string[] Columns = ["imdb_id", "poster_url", "lang", "qid"];

    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS wikipedia_posters_raw (
            imdb_id text NOT NULL,
            poster_url text NULL,
            lang text NULL,
            qid text NULL
        );
        """;

    public const string TruncateSql = "TRUNCATE TABLE wikipedia_posters_raw;";

    /// <summary>
    /// Fill-only UPDATE titles за imdb_id (ідентичність через P345→QID→sitelink — побудована самим
    /// ланцюгом): poster_path ЛИШЕ у порожні (NULL/'' — після чистки фейків і для тих, де його ніколи
    /// не було); наявні постери TMDB/sweet.tv/shikimori НЕ перебиваємо. Ідемпотентність: повторний
    /// прогін не знаходить порожніх серед уже заповнених → 0 рядків.
    /// </summary>
    public const string UpdateSql = """
        UPDATE titles t SET
            poster_path = COALESCE(t.poster_path, s.poster_url),
            updated_at = now()
        FROM wikipedia_posters_raw s
        WHERE t.imdb_id = s.imdb_id
          AND s.poster_url IS NOT NULL AND s.poster_url <> ''
          AND (t.poster_path IS NULL OR t.poster_path = '')
        """;

    /// <summary>COPY-заголовок (binary): колонки в порядку Columns.</summary>
    public static string CopyFromStdinSql() =>
        $"COPY {TableName} ({string.Join(", ", Columns)}) FROM STDIN (FORMAT BINARY)";
}

/// <summary>Опції wikipedia-posters синку (розділ Wikipedia конфігу).</summary>
public sealed class WikipediaPosterOptions
{
    /// <summary>SPARQL-endpoint Wikidata Query Service (P345 → QID + sitelinks).</summary>
    public string Endpoint { get; set; } = "https://query.wikidata.org/sparql";

    /// <summary>Шаблон URL MediaWiki action API ({0} — мова вікі: uk/ru/en).</summary>
    public string ApiUrlTemplate { get; set; } = "https://{0}.wikipedia.org/w/api.php";

    /// <summary>User-Agent (політика Wikimedia: контактний рядок обов'язковий і для QS, і для api.php).</summary>
    public string UserAgent { get; set; } = "KinoNavigator/1.0 (movie-discovery; contact via repo issues)";

    /// <summary>Скільки imdb_id каталогу запитувати за один SPARQL VALUES-запит (POST form).</summary>
    public int SparqlBatchSize { get; set; } = 500;

    /// <summary>MediaWiki prop=pageimages: жорсткий ліміт titles на запит (API дозволяє ≤50).</summary>
    public int MediaWikiBatchSize { get; set; } = 50;

    /// <summary>Пауза між запитами до Wikimedia (QS і api.php) — ввічливість, ≥700мс.</summary>
    public int RequestDelayMs { get; set; } = 700;
}

/// <summary>Результат прогону wikipedia-posters sync.</summary>
public sealed record WikipediaPosterSyncResult(int Staged, int Updated, int Batches);

/// <summary>
/// Справжні постери з ВІЛЬНОГО джерела — Wikipedia через Wikidata (без ключів), QID-first:
/// SPARQL VALUES по imdb_id (P345 → QID + РОЗДІЛЕНІ sitelink-триплети uk/ru/en) →
/// MediaWiki action API prop=pageimages&amp;piprop=thumbnail|original&amp;pithumbsize=500&amp;redirects=1
/// батчами ≤50 titles → staging wikipedia_posters_raw → fill-only UPDATE titles.
/// Ідентичність побудована ланцюгом (imdb_id→QID→стаття саме цього елемента), не пошуком за назвою —
/// однойменні тайтли різних років отримують свої пости. Постер із першої мови-пріоритету uk → ru → en,
/// у staging пишеться lang і qid (перевірність джерела). Запускається АДМІН-джобою
/// (POST /api/admin/sync/wikipedia-posters — код в отчёті оркестратора), НІКОЛИ з GET-шляхів каталогу.
/// </summary>
public sealed class WikipediaPosterService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    IOptions<WikipediaPosterOptions> options,
    ILogger<WikipediaPosterService> log)
{
    public const string Source = "wikipedia-posters";

    /// <summary>Пріоритет мов вікі для постера (укр → рос → англ; сама картинка мовно нейтральна).</summary>
    public static readonly string[] LangPriority = ["uk", "ru", "en"];

    private readonly WikipediaPosterOptions _opt = options.Value;

    /// <summary>
    /// SPARQL з VALUES-батчем imdb_id. Sitelinks — три РОЗДІЛЕНІ OPTIONAL-блоки з окремими
    /// триплетами (';'-ланцюг між schema:about і schema:isPartOf повертає порожні sitelinks —
    /// перевірено живцем на QS). schema: — дефолтний префікс QS, PREFIX не потрібні.
    /// $$-префікс: { } літеральні.
    /// </summary>
    public static string SparqlValues(IReadOnlyCollection<string> ids)
    {
        var sb = new StringBuilder(ids.Count * 14 + 700);
        sb.Append("SELECT ?imdb ?qid ?en ?uk ?ru WHERE {\n  VALUES ?imdb { ");
        foreach (var id in ids)
            sb.Append('"').Append(id.Replace("\"", "")).Append("\" ");
        sb.Append("}\n  ?item wdt:P345 ?imdb .\n");
        sb.Append("  BIND(STRAFTER(STR(?item), \"/entity/\") AS ?qid)\n");
        foreach (var (lang, varName) in new[] { ("en", "en"), ("uk", "uk"), ("ru", "ru") })
        {
            sb.Append($"  OPTIONAL {{\n    ?{varName}page schema:about ?item .\n");
            sb.Append($"    ?{varName}page schema:isPartOf <https://{lang}.wikipedia.org/> .\n");
            sb.Append($"    ?{varName}page schema:name ?{varName} .\n  }}\n");
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Повний прогін: imdb-каталог без постерів → SPARQL → MediaWiki → COPY → fill-only UPDATE → sync_state.</summary>
    public async Task<WikipediaPosterSyncResult> RunAsync(int? maxRows, CancellationToken ct = default)
    {
        if (!WikidataNamesService.IsNpgsql(db))
            throw new InvalidOperationException("wikipedia-posters sync потребує Npgsql-провайдера (staging COPY).");

        await SetUnloggedAsync(ct);

        var staged = 0;
        var updated = 0;
        var batches = 0;
        var skipped = 0;

        // Джерело ідентичності — imdb_id; ціль fill-only — тайтли БЕЗ постера
        // (після чистки фейків i.ytimg.com / missing_original.jpg вони теж тут).
        var ids = db.Titles.AsNoTracking()
            .Where(t => t.ImdbId != null && t.ImdbId != "" && (t.PosterPath == null || t.PosterPath == ""))
            .OrderBy(t => t.Id)
            .Select(t => t.ImdbId!);

        foreach (var batch in Batch(ids, _opt.SparqlBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var remaining = maxRows is { } cap ? cap - staged : (int?)null;
            if (remaining is <= 0) break; // smoke-ліміт вичерпано (ЗАГАЛЬНИЙ по всіх батчах)

            List<WikipediaSparqlRow> sparqlRows;
            try
            {
                sparqlRows = await FetchSparqlBatchAsync(batch, remaining, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Батч не взявся навіть після ретраїв — пропускаємо і йдемо далі:
                // sync ідемпотентний, повторний прогін добере пропущене.
                log.LogWarning(ex, "wikipedia-posters: SPARQL-батч #{N} пропущено після ретраїв", batches + 1);
                batches++;
                skipped += batch.Count;
                continue;
            }
            batches++;

            var rows = await ResolvePostersAsync(sparqlRows, ct);
            staged += rows.Count;
            if (rows.Count > 0)
                updated += await StageAndUpdateAsync(rows, ct);
            log.LogInformation("wikipedia-posters: батч #{N}: запитано={Asked}, з QS={Rows}, постерів знайдено={Found} (staged={Staged}, updated={Updated})",
                batches, batch.Count, sparqlRows.Count, rows.Count, staged, updated);

            if (maxRows is { } c && staged >= c) break;
            await Task.Delay(_opt.RequestDelayMs, ct);
        }

        await ExecuteWithRetryAsync(WikipediaPosterStaging.TruncateSql, ct);

        await UpdateSyncStateAsync(staged, updated, ct);
        log.LogInformation("Wikipedia posters sync завершено: staged={Staged}, updated={Updated}, batches={Batches}, skipped={Skipped}",
            staged, updated, batches, skipped);
        return new WikipediaPosterSyncResult(staged, updated, batches);
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

    /// <summary>SPARQL VALUES-батч → CSV (imdb,qid,en,uk,ru). Ретраї 429/503/502/504/403 — як у wikidata-names.</summary>
    private async Task<List<WikipediaSparqlRow>> FetchSparqlBatchAsync(List<string> ids, int? maxRows, CancellationToken ct)
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
            if ((int)resp.StatusCode is 429 or 503 or 502 or 504 or 403)
            {
                if (attempt >= maxAttempts)
                    throw new HttpRequestException($"Wikidata QS {resp.StatusCode} після {attempt} спроб");
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30 * attempt);
                log.LogWarning("wikipedia-posters: QS {Status} — пауза {Retry}s (спроба {Attempt}/{Max})",
                    (int)resp.StatusCode, retryAfter.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(retryAfter, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return ParseSparqlCsv(stream, maxRows);
        }
    }

    /// <summary>Мінімальний RFC4180-парсер SPARQL CSV (imdb,qid,en,uk,ru) — через спільний CsvReader.</summary>
    public static List<WikipediaSparqlRow> ParseSparqlCsv(Stream stream, int? maxRows)
    {
        var rows = new List<WikipediaSparqlRow>();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var header = CsvReader.ReadCsvLine(reader);
        if (header is null || header.Count == 0) return rows;

        var imdbIdx = header.FindIndex(h => h.Trim() == "imdb");
        var qidIdx = header.FindIndex(h => h.Trim() == "qid");
        var enIdx = header.FindIndex(h => h.Trim() == "en");
        var ukIdx = header.FindIndex(h => h.Trim() == "uk");
        var ruIdx = header.FindIndex(h => h.Trim() == "ru");
        if (imdbIdx < 0) return rows;

        List<string>? fields;
        while ((fields = CsvReader.ReadCsvLine(reader)) is not null)
        {
            var imdb = Field(fields, imdbIdx).Trim();
            var qid = Field(fields, qidIdx).Trim();
            if (imdb.Length == 0 || qid.Length == 0) continue; // без QID немає перевірної ідентичності

            var en = Field(fields, enIdx).Trim();
            var uk = Field(fields, ukIdx).Trim();
            var ru = Field(fields, ruIdx).Trim();
            if (en.Length == 0 && uk.Length == 0 && ru.Length == 0) continue; // жодного sitelink — постера не буде

            rows.Add(new WikipediaSparqlRow(imdb, qid,
                en.Length > 0 ? en : null, uk.Length > 0 ? uk : null, ru.Length > 0 ? ru : null));
            if (maxRows is { } cap && rows.Count >= cap) break;
        }
        return rows;
    }

    private static string Field(List<string> f, int i) => i >= 0 && i < f.Count ? f[i] : "";

    /// <summary>Sitelink назва статті для мови (null — статті немає).</summary>
    public static string? Sitelink(WikipediaSparqlRow row, string lang) => lang switch
    {
        "en" => row.En,
        "uk" => row.Uk,
        "ru" => row.Ru,
        _ => null,
    };

    /// <summary>
    /// MediaWiki-шар: для кожної мови за пріоритетом uk → ru → en — батчі ≤50 titles у
    /// prop=pageimages (thumbnail 500px | original), пауза ≥700мс між запитами, ретраї
    /// 429/503 backoff 30–60с. Постер бере ПЕРША мова, де стаття має pageimage.
    /// </summary>
    internal async Task<List<WikipediaPosterRow>> ResolvePostersAsync(List<WikipediaSparqlRow> sparqlRows, CancellationToken ct)
    {
        var found = new Dictionary<string, WikipediaPosterRow>(); // imdb → перший (найпріоритетніший) постер

        foreach (var lang in LangPriority)
        {
            // пари (imdb, назва статті) тільки для тих, у кого постера ще нема
            var pending = sparqlRows
                .Where(r => !found.ContainsKey(r.ImdbId))
                .Select(r => (Imdb: r.ImdbId, Qid: r.Qid, Title: Sitelink(r, lang)))
                .Where(p => !string.IsNullOrWhiteSpace(p.Title))
                .ToList();
            if (pending.Count == 0) continue;

            for (var offset = 0; offset < pending.Count; offset += _opt.MediaWikiBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunkPairs = pending.Skip(offset).Take(_opt.MediaWikiBatchSize).ToList();
                var titles = chunkPairs.Select(p => p.Title!).Distinct(StringComparer.Ordinal).ToList();
                if (titles.Count == 0) continue;

                Dictionary<string, string> urlByPage;
                try
                {
                    urlByPage = await FetchPageImagesAsync(lang, titles, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Чанк не взявся навіть після ретраїв — пропускаємо чанк (не весь батч):
                    // sync ідемпотентний, повторний прогін добере пропущене.
                    log.LogWarning(ex, "wikipedia-posters: MediaWiki {Lang}-чанк ({Count} titles) пропущено після ретраїв",
                        lang, titles.Count);
                    continue;
                }
                finally
                {
                    // пауза ≥700мс між БУДЬ-якими запитами до MediaWiki (429-безпечно)
                    await Task.Delay(_opt.RequestDelayMs, ct);
                }

                foreach (var p in chunkPairs)
                {
                    if (p.Title is null || !urlByPage.TryGetValue(p.Title, out var url)) continue;
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    found[p.Imdb] = new WikipediaPosterRow(p.Imdb, url, lang, p.Qid);
                }
            }
        }
        return found.Values.ToList();
    }

    /// <summary>URL запиту MediaWiki action API: prop=pageimages, thumbnail|original, 500px, redirects=1, titles ≤50 через %7C.</summary>
    public static string BuildPageImagesUrl(string apiUrlTemplate, string lang, IReadOnlyCollection<string> titles) =>
        string.Format(apiUrlTemplate, lang) +
        "?action=query&format=json&formatversion=2&prop=pageimages" +
        "&piprop=thumbnail%7Coriginal&pithumbsize=500&redirects=1&titles=" +
        string.Join("%7C", titles.Select(Uri.EscapeDataString));

    /// <summary>Запитана назва → URL: (normalized →) redirects → фінальна назва сторінки → URL (пропускає відсутні).</summary>
    public static Dictionary<string, string> MapRequestedToUrls(
        IEnumerable<string> requested,
        IReadOnlyDictionary<string, string> renames,
        Dictionary<string, string> urlByPage)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var title in requested)
        {
            var final = ResolveTitle(title, renames);
            if (urlByPage.TryGetValue(final, out var url))
                result[title] = url;
        }
        return result;
    }

    /// <summary>
    /// Один запит MediaWiki action API (≤50 titles): GET action=query&amp;format=json&amp;formatversion=2
    /// &amp;prop=pageimages&amp;piprop=thumbnail|original&amp;pithumbsize=500&amp;redirects=1&amp;titles=…
    /// Повертає map ЗАПИТАНА назва → URL постера (redirects/normalized розв'язуються всередині).
    /// </summary>
    internal async Task<Dictionary<string, string>> FetchPageImagesAsync(string lang, List<string> titles, CancellationToken ct)
    {
        var url = BuildPageImagesUrl(_opt.ApiUrlTemplate, lang, titles);

        var maxAttempts = 4;
        string json;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(_opt.UserAgent);
            using var resp = await http.SendAsync(req, ct);
            // MediaWiki 429/503 — backoff 30–60с (політика Wikimedia); 502/504 — теж транзитні
            if ((int)resp.StatusCode is 429 or 503 or 502 or 504)
            {
                if (attempt >= maxAttempts)
                    throw new HttpRequestException($"MediaWiki {lang} {resp.StatusCode} після {attempt} спроб");
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30 * attempt, 60));
                log.LogWarning("wikipedia-posters: MediaWiki {Lang} {Status} — пауза {Retry}s (спроба {Attempt}/{Max})",
                    lang, (int)resp.StatusCode, retryAfter.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(retryAfter, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            json = await resp.Content.ReadAsStringAsync(ct);
            break;
        }

        var urlByPage = ParsePageImagesJson(json)
            .Where(p => !p.Missing && PickPoster(p) is not null)
            .ToDictionary(p => p.Title, p => PickPoster(p)!, StringComparer.Ordinal);

        return MapRequestedToUrls(titles, ParseRenamesJson(json), urlByPage);
    }

    /// <summary>Постер сторінки: thumbnail (500px) у пріоритеті, фолбек — original.</summary>
    public static string? PickPoster(WikiPageImage page) =>
        !string.IsNullOrWhiteSpace(page.Thumbnail) ? page.Thumbnail
        : !string.IsNullOrWhiteSpace(page.Original) ? page.Original
        : null;

    /// <summary>Ланцюжок перейменувань normalized+redirects: requested → фінальна назва сторінки.</summary>
    public static string ResolveTitle(string requested, IReadOnlyDictionary<string, string> renames)
    {
        var current = requested;
        var guard = 0;
        while (renames.TryGetValue(current, out var next) && next != current && guard++ < 8)
            current = next;
        return current;
    }

    /// <summary>Парсер відповіді MediaWiki (formatversion=2): pages із thumbnail/original, missing відфільтровано прапорцем.</summary>
    public static List<WikiPageImage> ParsePageImagesJson(string json)
    {
        var pages = new List<WikiPageImage>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return pages;

        foreach (var page in arr.EnumerateArray())
        {
            var title = page.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(title)) continue;
            var missing = page.TryGetProperty("missing", out _); // formatversion=2: missing=""/true
            string? thumb = null, original = null;
            if (page.TryGetProperty("thumbnail", out var th) && th.TryGetProperty("source", out var ts))
                thumb = ts.GetString();
            if (page.TryGetProperty("original", out var og) && og.TryGetProperty("source", out var os))
                original = os.GetString();
            pages.Add(new WikiPageImage(title, thumb, original, missing));
        }
        return pages;
    }

    /// <summary>normalized+redirects з відповіді MediaWiki → map from→to (для повернення до запитаної назви).</summary>
    public static Dictionary<string, string> ParseRenamesJson(string json)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var query)) return renames;
        foreach (var key in new[] { "normalized", "redirects" })
        {
            if (!query.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in arr.EnumerateArray())
            {
                var from = item.TryGetProperty("from", out var f) ? f.GetString() : null;
                var to = item.TryGetProperty("to", out var t2) ? t2.GetString() : null;
                if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                    renames.TryAdd(from, to); // normalized записано першим; колізії немає за природою ланцюга
            }
        }
        return renames;
    }

    /// <summary>COPY батчу в staging + fill-only UPDATE titles (ідемпотентно; ретрай транзитних збоїв PG).</summary>
    public async Task<int> StageAndUpdateAsync(List<WikipediaPosterRow> rows, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
                await conn.OpenAsync(ct);

                await using (var sc = new NpgsqlCommand("SET synchronous_commit TO OFF", conn))
                    await sc.ExecuteNonQueryAsync(ct);

                await using (var truncate = new NpgsqlCommand(WikipediaPosterStaging.TruncateSql, conn))
                    await truncate.ExecuteNonQueryAsync(ct);

                await using (var writer = await conn.BeginBinaryImportAsync(WikipediaPosterStaging.CopyFromStdinSql(), ct))
                {
                    foreach (var r in rows)
                    {
                        await writer.StartRowAsync(ct);
                        await writer.WriteAsync(r.ImdbId, NpgsqlDbType.Text, ct);
                        await WriteNullable(writer, r.PosterUrl, ct);
                        await WriteNullable(writer, r.Lang, ct);
                        await WriteNullable(writer, r.Qid, ct);
                    }
                    await writer.CompleteAsync(ct);
                }

                await using var update = new NpgsqlCommand(WikipediaPosterStaging.UpdateSql + ";", conn);
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
        await ExecuteWithRetryAsync("ALTER TABLE IF EXISTS wikipedia_posters_raw SET UNLOGGED", ct);

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
}

/// <summary>Джоба wikipedia-posters sync: заповнення порожніх poster_path з Wikipedia (QID-first).</summary>
public sealed record WikipediaPosterSyncJob(int? MaxRows);

/// <summary>Черга wikipedia-posters-синхронізації.</summary>
public sealed class WikipediaPosterSyncQueue
{
    private readonly System.Threading.Channels.Channel<WikipediaPosterSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<WikipediaPosterSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(WikipediaPosterSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<WikipediaPosterSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка wikipedia-posters-джоб (SPARQL → MediaWiki → staging → fill-only UPDATE titles).</summary>
public sealed class WikipediaPosterSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<WikipediaPosterSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<WikipediaPosterSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<WikipediaPosterService>();
                var result = await service.RunAsync(job.MaxRows, stoppingToken);
                log.LogInformation("Wikipedia-posters джоба завершена: staged={Staged}, updated={Updated}, batches={Batches}",
                    result.Staged, result.Updated, result.Batches);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка wikipedia-posters-синхронізації (maxRows={MaxRows})", job.MaxRows);
            }
        }
    }
}
