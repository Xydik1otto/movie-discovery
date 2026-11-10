using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace MovieDiscovery.Infrastructure.Imdb;

// ─── TSV-рядки IMDb crew-датасетів ───

/// <summary>Рядок title.crew.tsv: 0=tconst, 1=directors, 2=writers (tconst-списки через кому).</summary>
public sealed record ImdbCrewRow(string Tconst, string[] Directors);

/// <summary>Рядок title.principals.tsv: 0=tconst, 1=ordering, 2=nconst, 3=category, 4=job, 5=characters.</summary>
public sealed record ImdbPrincipalRow(string Tconst, int Ordering, string Nconst, string Category);

/// <summary>Рядок name.basics.tsv: 0=nconst, 1=primaryName, ...</summary>
public sealed record ImdbNameRow(string Nconst, string PrimaryName);

/// <summary>
/// Потоковий парсер crew/principals/name.basics (IMDb datasets, розширення FR-19/20).
/// Чисті функції — без I/O, покриваються тестами. «\N» — відсутнє значення.
/// </summary>
public static class ImdbCrewParser
{
    /// <summary>Категорії principals, що рахуються акторським складом (контракт FR-20).</summary>
    public static bool IsCastCategory(string category) =>
        category is "actor" or "actress" or "self";

    /// <summary>Розібрати рядок crew; directors «nm1,nm2» | \N → масив nconst (дублікт прибрано).</summary>
    public static ImdbCrewRow? ParseCrew(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var p = line.Split('\t');
        if (p.Length < 2) return null;

        var tconst = p[0].Trim();
        if (tconst.Length == 0 || tconst == ImdbTsv.NullField) return null;

        return new ImdbCrewRow(tconst, ParseNconstList(p.Length > 1 ? p[1] : ImdbTsv.NullField));
    }

    /// <summary>Розібрати рядок principals; ordering/\N або кривий nconst → null (рядок пропускаємо).</summary>
    public static ImdbPrincipalRow? ParsePrincipal(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var p = line.Split('\t');
        if (p.Length < 4) return null;

        var tconst = p[0].Trim();
        if (tconst.Length == 0 || tconst == ImdbTsv.NullField) return null;
        if (!int.TryParse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var ordering))
            return null;
        var nconst = p[2].Trim();
        if (!IsValidNconst(nconst)) return null;
        var category = p[3].Trim();
        if (category.Length == 0 || category == ImdbTsv.NullField) return null;

        return new ImdbPrincipalRow(tconst, ordering, nconst, category);
    }

    /// <summary>Розібрати рядок name.basics (потрібні лише nconst + primaryName).</summary>
    public static ImdbNameRow? ParseName(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var p = line.Split('\t');
        if (p.Length < 2) return null;

        var nconst = p[0].Trim();
        if (!IsValidNconst(nconst)) return null;
        var name = p[1].Trim();
        if (name.Length == 0 || name == ImdbTsv.NullField) return null;
        return new ImdbNameRow(nconst, name);
    }

    /// <summary>«nm0000001»-форма.</summary>
    public static bool IsValidNconst(string? nconst) =>
        nconst is { Length: > 2 }
        && nconst.StartsWith("nm", StringComparison.Ordinal)
        && nconst[2..].All(char.IsAsciiDigit);

    /// <summary>
    /// Вибрати акторський склад групи principals одного тайтла: ЛИШЕ cast-категорії,
    /// перші {max} по ordering, дублікати nconst прибрано (перший оригінальний виграє).
    /// </summary>
    public static string[] SelectCast(IEnumerable<ImdbPrincipalRow> rows, int max) =>
        [.. rows
            .Where(r => IsCastCategory(r.Category))
            .OrderBy(r => r.Ordering)
            .Take(max)
            .Select(r => r.Nconst)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>«nm1,nm2,\N» → [«nm1», «nm2»] (валідні, без дублікатів).</summary>
    public static string[] ParseNconstList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == ImdbTsv.NullField) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsValidNconst(part)) continue;
            if (seen.Add(part)) result.Add(part);
        }
        return [.. result];
    }
}

// ─── Злиття у titles (чисте ядро — ідемпотентне, тестопридатне) ───

/// <summary>Готовий crew-запис одного тайтла: імена ВЖЕ розрізшені з nconst (EF-шлях/тести).</summary>
public sealed record ImdbCrewRecord(string ImdbId, string Type, string[] Directors, string[] Actors);

/// <summary>
/// Ідемпотентне застосування crew-запису до titles (лише UPDATE існуючих, БЕЗ створення).
/// Обмеження контракту: type movie|series; anime/manga НЕ чіпаються; тайтли з uk-джерел
/// (title_uk заповнено — sweet.tv/TMDB-укр) і sweettv-рядки (title_en='') НЕ чіпаються.
/// Повторний sync присвоює ті самі масиви (не конкатенує) → стабільний.
/// </summary>
public static class ImdbCrewMergeCore
{
    public static bool CanApply(Title target, ImdbCrewRecord record) =>
        (target.Type is "movie" or "series") &&
        target.Type == record.Type &&
        !string.IsNullOrEmpty(target.TitleEn) &&
        string.IsNullOrWhiteSpace(target.TitleUk);

    /// <summary>Застосувати; false — тайтл під guard-ами (не чіпаємо).</summary>
    public static bool Apply(Title target, ImdbCrewRecord record)
    {
        if (!CanApply(target, record)) return false;
        if (record.Directors is { Length: > 0 }) target.Directors = record.Directors;
        if (record.Actors is { Length: > 0 }) target.Actors = record.Actors;
        target.UpdatedAt = DateTime.UtcNow;
        return true;
    }
}

// ─── Staging SQL (єдине джерело правди для міграції і сервісу) ───

/// <summary>
/// Staging-таблиці imdb_crew_raw / imdb_principals_raw / imdb_names_raw (FR-20 розширення):
/// nconst-масиви стейджаться COPY-ом, імена резольвляться SQL-джоїном з imdb_names_raw
/// (без тримання словника 13 млн назв у пам'яті). Типова згортка — UPDATE titles за imdb_id
/// з guard-ами (movie|series, title_en ≠ '', title_uk порожній).
/// </summary>
public static class ImdbCrewStaging
{
    public const string CrewTable = "imdb_crew_raw";
    public const string PrincipalsTable = "imdb_principals_raw";
    public const string NamesTable = "imdb_names_raw";

    public static readonly string[] CrewColumns = ["imdb_id", "directors"];
    public static readonly string[] PrincipalsColumns = ["imdb_id", "actors"];
    public static readonly string[] NamesColumns = ["nconst", "primary_name"];

    /// <summary>DDL staging-таблиць (у міграції; тут — для smoke/тестів і довідки).</summary>
    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS imdb_crew_raw (
            imdb_id text NOT NULL,
            directors text[]
        );
        CREATE TABLE IF NOT EXISTS imdb_principals_raw (
            imdb_id text NOT NULL,
            actors text[]
        );
        CREATE TABLE IF NOT EXISTS imdb_names_raw (
            nconst text NOT NULL,
            primary_name text NOT NULL
        );
        """;

    public const string DropTableSql = """
        DROP TABLE IF EXISTS imdb_crew_raw;
        DROP TABLE IF EXISTS imdb_principals_raw;
        DROP TABLE IF EXISTS imdb_names_raw;
        """;

    public const string TruncateAllSql = """
        TRUNCATE TABLE imdb_crew_raw;
        TRUNCATE TABLE imdb_principals_raw;
        TRUNCATE TABLE imdb_names_raw;
        """;

    public const string CreateNamesIndexSql =
        "CREATE INDEX IF NOT EXISTS ix_imdb_names_raw_nconst ON imdb_names_raw (nconst);";

    /// <summary>COPY-заголовки (binary): колонки в порядку Columns.</summary>
    public static string CopyCrewSql() =>
        $"COPY {CrewTable} ({string.Join(", ", CrewColumns)}) FROM STDIN (FORMAT BINARY)";

    public static string CopyPrincipalsSql() =>
        $"COPY {PrincipalsTable} ({string.Join(", ", PrincipalsColumns)}) FROM STDIN (FORMAT BINARY)";

    public static string CopyNamesSql() =>
        $"COPY {NamesTable} ({string.Join(", ", NamesColumns)}) FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// Фінальне злиття: nconst → primary_name через imdb_names_raw (індекс за nconst),
    /// дублікати staging прибрано DISTINCT ON, тайтли лише з actors/directors-даними.
    /// Guard-и: type movie|series; sweettv (title_en='') і uk-джерела (title_uk) не чіпаються;
    /// COALESCE — відсутність резолвнутих імен НЕ затирає наявні дані; ідемпотентність —
    /// лише фактичні зміни (IS DISTINCT FROM).
    /// </summary>
    public const string MergeSql = """
        WITH crew_resolved AS (
            SELECT DISTINCT ON (c.imdb_id)
                   c.imdb_id,
                   (SELECT array_agg(n.primary_name ORDER BY u.ord)
                      FROM unnest(c.directors) WITH ORDINALITY AS u(nc, ord)
                      JOIN imdb_names_raw n ON n.nconst = u.nc) AS directors
            FROM imdb_crew_raw c
        ),
        princ_resolved AS (
            SELECT DISTINCT ON (p.imdb_id)
                   p.imdb_id,
                   (SELECT array_agg(n.primary_name ORDER BY u.ord)
                      FROM unnest(p.actors) WITH ORDINALITY AS u(nc, ord)
                      JOIN imdb_names_raw n ON n.nconst = u.nc) AS actors
            FROM imdb_principals_raw p
        ),
        merged AS (
            SELECT c.imdb_id, c.directors, p.actors
              FROM crew_resolved c
              LEFT JOIN princ_resolved p USING (imdb_id)
            UNION ALL
            SELECT p.imdb_id, NULL::text[], p.actors
              FROM princ_resolved p
             WHERE NOT EXISTS (SELECT 1 FROM imdb_crew_raw c WHERE c.imdb_id = p.imdb_id)
        )
        UPDATE titles t SET
            directors = COALESCE(m.directors, t.directors),
            actors = COALESCE(m.actors, t.actors),
            updated_at = now()
        FROM merged m
        WHERE t.imdb_id = m.imdb_id
          AND t.type IN ('movie', 'series')
          AND t.title_en <> ''
          AND (t.title_uk IS NULL OR t.title_uk = '')
          AND (COALESCE(m.directors, t.directors) IS DISTINCT FROM t.directors
               OR COALESCE(m.actors, t.actors) IS DISTINCT FROM t.actors)
        """;
}

/// <summary>Опції imdb-crew sync (розділ Imdb конфігу).</summary>
public sealed class ImdbCrewOptions
{
    public string CacheDir { get; set; } = "";
    public int CacheTtlHours { get; set; } = 24;
    public int BatchSize { get; set; } = 50_000;

    /// <summary>Скільки cast-принципалів брати на тайтл (перші N по ordering).</summary>
    public int MaxCastPerTitle { get; set; } = 8;
}

/// <summary>Результат прогону imdb-crew sync.</summary>
public sealed record ImdbCrewSyncResult(int MoviesStaged, int SeriesStaged, int TitlesUpdated);

/// <summary>
/// Розширення FR-19/20: IMDb crew (directors) + principals (actors: actor|actress|self,
/// перші ~8 по ordering) + name.basics (nconst → ім'я) → titles.directors/actors.
/// Лише UPDATE існуючих тайтлів за imdb_id (movie|series; anime/manga/sweettv не чіпаються).
/// Патерн ImdbBulkService: .gz у 24-год кеші, батчі 50k → PG COPY у UNLOGGED staging →
/// один MERGE-SQL (імена резольвляться в БД, словник 13 млн назв у пам'яті не тримається),
/// ретрай транзитних, synchronous_commit OFF, TRUNCATE на вході/виході.
/// </summary>
public sealed class ImdbCrewService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    ICacheStore cache,
    IOptions<ImdbCrewOptions> options,
    ILogger<ImdbCrewService> log)
{
    public const string Source = "imdb-crew";
    public const string CrewUrl = "https://datasets.imdbws.com/title.crew.tsv.gz";
    public const string PrincipalsUrl = "https://datasets.imdbws.com/title.principals.tsv.gz";
    public const string NamesUrl = "https://datasets.imdbws.com/name.basics.tsv.gz";

    private readonly ImdbCrewOptions _opt = options.Value;
    private readonly ImdbFileCache _files = new(http, cache, log);

    /// <summary>Повний прогін: download → basics-мапа → 3 streaming-паси → staging → MERGE → sync_state.</summary>
    public async Task<ImdbCrewSyncResult> RunAsync(int? maxRows, CancellationToken ct = default)
    {
        // title.basics кешується тим самим записом, що і в ImdbBulkService (source=imdb/file:basics)
        var basicsPath = await _files.DownloadAsync(
            ImdbBulkService.Source, "title.basics.tsv.gz", ImdbBulkService.BasicsUrl, "file:basics",
            _opt.CacheDir, _opt.CacheTtlHours, ct);
        var crewPath = await _files.DownloadAsync(
            Source, "title.crew.tsv.gz", CrewUrl, "file:crew", _opt.CacheDir, _opt.CacheTtlHours, ct);
        var principalsPath = await _files.DownloadAsync(
            Source, "title.principals.tsv.gz", PrincipalsUrl, "file:principals", _opt.CacheDir, _opt.CacheTtlHours, ct);
        var namesPath = await _files.DownloadAsync(
            Source, "name.basics.tsv.gz", NamesUrl, "file:names", _opt.CacheDir, _opt.CacheTtlHours, ct);

        var typesMap = await LoadBasicsMapAsync(basicsPath, ct);
        log.LogInformation("IMDb crew: basics-мапа {Count} movie/series тайтлів", typesMap.Count);

        var isNpgsql = ImdbAkasService.IsNpgsql(db);
        var moviesStaged = 0;
        var seriesStaged = 0;
        var updated = 0;

        if (isNpgsql)
        {
            // Стейджинг — відновлювані проміжні дані: UNLOGGED зменшує тиск на WAL.
            await ExecuteWithRetryAsync("ALTER TABLE IF EXISTS imdb_crew_raw SET UNLOGGED", ct);
            await ExecuteWithRetryAsync("ALTER TABLE IF EXISTS imdb_principals_raw SET UNLOGGED", ct);
            await ExecuteWithRetryAsync("ALTER TABLE IF EXISTS imdb_names_raw SET UNLOGGED", ct);
            await ExecuteWithRetryAsync(ImdbCrewStaging.TruncateAllSql, ct);

            // Пас 1: crew → directors (nconst)
            await foreach (var batch in StreamDirectorBatchesAsync(crewPath, typesMap, maxRows, ct))
            {
                foreach (var (_, type, _) in batch)
                    if (type == "movie") moviesStaged++; else seriesStaged++;
                await CopyCrewBatchAsync(batch, ct);
            }

            // Пас 2: principals → actors (nconst; actor|actress|self, перші ~8 по ordering)
            await foreach (var batch in StreamActorGroupBatchesAsync(principalsPath, typesMap, maxRows, ct))
                await CopyPrincipalsBatchAsync(batch, ct);

            // Пас 3: name.basics → словник назв у staging (резолв у SQL, не в пам'яті)
            await foreach (var batch in StreamNameBatchesAsync(namesPath, ct))
                await CopyNamesBatchAsync(batch, ct);

            await ExecuteWithRetryAsync(ImdbCrewStaging.CreateNamesIndexSql, ct);
            updated = await ExecuteNonQueryWithRetryAsync(ImdbCrewStaging.MergeSql + ";", ct);
            await ExecuteWithRetryAsync(ImdbCrewStaging.TruncateAllSql, ct);
        }
        else
        {
            // EF-шлях (тестовий InMemory): та сама логіка злиття через ImdbCrewMergeCore.
            var names = await LoadNamesAsync(namesPath, ct);
            var directors = new Dictionary<string, (string Type, string[] Nconsts)>(StringComparer.Ordinal);
            await foreach (var batch in StreamDirectorBatchesAsync(crewPath, typesMap, maxRows, ct))
                foreach (var (imdbId, type, ncs) in batch)
                    directors.TryAdd(imdbId, (type, ncs));

            var actors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            await foreach (var batch in StreamActorGroupBatchesAsync(principalsPath, typesMap, maxRows, ct))
                foreach (var (imdbId, _, ncs) in batch)
                    actors.TryAdd(imdbId, ncs);

            var ids = directors.Keys.Union(actors.Keys).ToList();
            var titles = await db.Titles
                .Where(t => t.ImdbId != null && ids.Contains(t.ImdbId))
                .ToListAsync(ct);
            var byId = titles.GroupBy(t => t.ImdbId!).ToDictionary(g => g.Key, g => g.First());

            foreach (var imdbId in ids)
            {
                if (!byId.TryGetValue(imdbId, out var title)) continue; // лише існуючі, БЕЗ створення
                var type = directors.TryGetValue(imdbId, out var d) ? d.Type
                    : actors.TryGetValue(imdbId, out _) ? typesMap.GetValueOrDefault(imdbId, "movie")
                    : "movie";
                var record = new ImdbCrewRecord(
                    imdbId, type,
                    ResolveNames(directors.TryGetValue(imdbId, out var dv) ? dv.Nconsts : [], names),
                    ResolveNames(actors.GetValueOrDefault(imdbId) ?? [], names));
                if (ImdbCrewMergeCore.Apply(title, record))
                {
                    updated++;
                    if (title.Type == "movie") moviesStaged++; else seriesStaged++;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        await UpdateSyncStateAsync(moviesStaged, seriesStaged, ct);
        log.LogInformation("IMDb crew sync завершено: movies={Movies}, series={Series}, updated={Updated}",
            moviesStaged, seriesStaged, updated);
        return new ImdbCrewSyncResult(moviesStaged, seriesStaged, updated);
    }

    // ── Потокові паси (спільні для Npgsql/EF-гілок) ──

    /// <summary>Стрім crew.tsv → батчі (imdbId, type, directors-nconst) лише для movie/series з directors.</summary>
    public async IAsyncEnumerable<List<(string ImdbId, string Type, string[] Nconsts)>> StreamDirectorBatchesAsync(
        string crewPath,
        IReadOnlyDictionary<string, string> typesMap,
        int? maxRows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var batch = new List<(string, string, string[])>(_opt.BatchSize);
        var emitted = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var first = true;

        await foreach (var line in ImdbGzip.TsvLinesAsync(crewPath, ct))
        {
            if (first) { first = false; continue; } // header
            var row = ImdbCrewParser.ParseCrew(line);
            if (row is null) continue;
            if (!typesMap.TryGetValue(row.Tconst, out var type)) continue; // лише movie/series
            if (row.Directors.Length == 0) continue;
            if (!seen.Add(row.Tconst)) continue;

            batch.Add((row.Tconst, type, row.Directors));
            emitted++;
            if (batch.Count >= _opt.BatchSize || (maxRows is { } cap && emitted >= cap))
            {
                yield return batch;
                batch = new List<(string, string, string[])>(_opt.BatchSize);
                seen.Clear();
                if (maxRows is { } c && emitted >= c) yield break;
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>
    /// Стрім principals.tsv → батчі (imdbId, actors-nconst): рядки одного tconst ідуть
    /// поспіль (файл згенерований відсортованим за tconst) → групування у потоці.
    /// </summary>
    public async IAsyncEnumerable<List<(string ImdbId, string Type, string[] Nconsts)>> StreamActorGroupBatchesAsync(
        string principalsPath,
        IReadOnlyDictionary<string, string> typesMap,
        int? maxRows,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var batch = new List<(string, string, string[])>(_opt.BatchSize);
        var emitted = 0;
        var first = true;
        string? current = null;
        var group = new List<ImdbPrincipalRow>();

        await foreach (var line in ImdbGzip.TsvLinesAsync(principalsPath, ct))
        {
            if (first) { first = false; continue; } // header
            var row = ImdbCrewParser.ParsePrincipal(line);
            if (row is null) continue;
            if (current is null || row.Tconst != current)
            {
                // флаш попередньої групи
                if (current is not null && typesMap.TryGetValue(current, out var prevType))
                {
                    var cast = ImdbCrewParser.SelectCast(group, _opt.MaxCastPerTitle);
                    if (cast.Length > 0)
                    {
                        batch.Add((current, prevType, cast));
                        emitted++;
                        if (batch.Count >= _opt.BatchSize || (maxRows is { } cap && emitted >= cap))
                        {
                            yield return batch;
                            batch = new List<(string, string, string[])>(_opt.BatchSize);
                            if (maxRows is { } c && emitted >= c) yield break;
                        }
                    }
                }
                current = row.Tconst;
                group.Clear();
            }
            if (ImdbCrewParser.IsCastCategory(row.Category)) group.Add(row);
        }

        // флаш останньої групи
        if (current is not null && typesMap.TryGetValue(current, out var lastType))
        {
            var lastCast = ImdbCrewParser.SelectCast(group, _opt.MaxCastPerTitle);
            if (lastCast.Length > 0) batch.Add((current, lastType, lastCast));
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Стрім name.basics → батчі (nconst, primaryName).</summary>
    public async IAsyncEnumerable<List<ImdbNameRow>> StreamNameBatchesAsync(
        string namesPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var batch = new List<ImdbNameRow>(_opt.BatchSize);
        var first = true;
        await foreach (var line in ImdbGzip.TsvLinesAsync(namesPath, ct))
        {
            if (first) { first = false; continue; } // header
            var row = ImdbCrewParser.ParseName(line);
            if (row is null) continue;
            batch.Add(row);
            if (batch.Count >= _opt.BatchSize)
            {
                yield return batch;
                batch = new List<ImdbNameRow>(_opt.BatchSize);
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    /// <summary>Мапа tconst → type (movie|series) з title.basics (лише маппабельні типи).</summary>
    public async Task<Dictionary<string, string>> LoadBasicsMapAsync(string basicsPath, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(capacity: 1_000_000);
        var first = true;
        await foreach (var line in ImdbGzip.TsvLinesAsync(basicsPath, ct))
        {
            if (first) { first = false; continue; } // header
            var row = ImdbTsv.ParseBasics(line);
            if (row is null) continue;
            var type = ImdbMapper.MapType(row.TitleType);
            if (type is null) continue;
            map[row.Tconst] = type;
        }
        return map;
    }

    /// <summary>Словник nconst → primaryName (EF-шлях; продакшн резольвить імена в БД).</summary>
    public async Task<Dictionary<string, string>> LoadNamesAsync(string namesPath, CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var row in StreamNamesAsync(namesPath, ct))
            dict.TryAdd(row.Nconst, row.PrimaryName);
        return dict;
    }

    private async IAsyncEnumerable<ImdbNameRow> StreamNamesAsync(
        string namesPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var first = true;
        await foreach (var line in ImdbGzip.TsvLinesAsync(namesPath, ct))
        {
            if (first) { first = false; continue; }
            if (ImdbCrewParser.ParseName(line) is { } row) yield return row;
        }
    }

    /// <summary>nconst → імена (в порядку масиву; невідомі nconst — пропущено).</summary>
    internal static string[] ResolveNames(string[] nconsts, IReadOnlyDictionary<string, string> names)
    {
        var result = new List<string>(nconsts.Length);
        foreach (var nc in nconsts)
            if (names.TryGetValue(nc, out var name)) result.Add(name);
        return [.. result];
    }

    // ── PG COPY (батчі; ретрай транзитних) ──

    private Task CopyCrewBatchAsync(List<(string ImdbId, string Type, string[] Nconsts)> batch, CancellationToken ct) =>
        CopyBatchAsync(ImdbCrewStaging.CopyCrewSql(), batch, ct, async (w, r) =>
        {
            await w.WriteAsync(r.ImdbId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(r.Nconsts, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
        });

    private Task CopyPrincipalsBatchAsync(List<(string ImdbId, string Type, string[] Nconsts)> batch, CancellationToken ct) =>
        CopyBatchAsync(ImdbCrewStaging.CopyPrincipalsSql(), batch, ct, async (w, r) =>
        {
            await w.WriteAsync(r.ImdbId, NpgsqlDbType.Text, ct);
            await w.WriteAsync(r.Nconsts, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
        });

    private Task CopyNamesBatchAsync(List<ImdbNameRow> batch, CancellationToken ct) =>
        CopyBatchAsync(ImdbCrewStaging.CopyNamesSql(), batch, ct, async (w, r) =>
        {
            await w.WriteAsync(r.Nconst, NpgsqlDbType.Text, ct);
            await w.WriteAsync(r.PrimaryName, NpgsqlDbType.Text, ct);
        });

    private async Task CopyBatchAsync<T>(
        string copySql, List<T> rows, CancellationToken ct, Func<NpgsqlBinaryImporter, T, Task> write)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
                await conn.OpenAsync(ct);
                await using (var sc = new NpgsqlCommand("SET synchronous_commit TO OFF", conn))
                    await sc.ExecuteNonQueryAsync(ct);
                await using (var writer = await conn.BeginBinaryImportAsync(copySql, ct))
                {
                    foreach (var r in rows)
                    {
                        await writer.StartRowAsync(ct);
                        await write(writer, r);
                    }
                    await writer.CompleteAsync(ct);
                }
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

    private async Task ExecuteWithRetryAsync(string sql, CancellationToken ct) =>
        await ExecuteNonQueryWithRetryAsync(sql, ct);

    private async Task<int> ExecuteNonQueryWithRetryAsync(string sql, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(db.Database.GetConnectionString());
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(sql, conn);
                // Дефолтні 30 с недостатні для merge-UPDATE по 2.8 млн рядків під
                // I/O-нагрузкою інших синків — 15 хв з запасом; ретрай нижче ловить
                // лише PostgresException, тому таймаут лікуємо тут, а не ретраєм.
                cmd.CommandTimeout = 900;
                return await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException ex) when (attempt < 3 && PgRetry.IsTransient(ex))
            {
                log.LogWarning("Транзитна помилка PG (спроба {Attempt}/3, {SqlState}): {Msg} — ретрай",
                    attempt, ex.SqlState, ex.MessageText);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), ct);
            }
        }
    }

    /// <summary>sync_state source=imdb-crew, kind=movie|series (окремі лічильники прогресу).</summary>
    private async Task UpdateSyncStateAsync(int movies, int series, CancellationToken ct)
    {
        foreach (var (kind, count) in new[] { ("movie", movies), ("series", series) })
        {
            var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == kind, ct);
            if (state is null)
            {
                state = new SyncState { Source = Source, Kind = kind };
                db.SyncStates.Add(state);
            }
            state.LastRunAt = DateTime.UtcNow;
            state.LastCount = count;
        }
        await db.SaveChangesAsync(ct);
    }
}
