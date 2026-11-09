using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Watch;

/// <summary>Оффер JustWatch: тип монетизації + посилання + пакет (провайдер).</summary>
public sealed record JustWatchOffer(
    string? MonetizationType,
    string? StandardWebUrl,
    string? PackageClearName,
    string? PackageTechnicalName);

/// <summary>Тайтл JustWatch з відповіді popularTitles (id, тип, назва/рік, imdb/tmdb, оффери).</summary>
public sealed record JustWatchTitle(
    string JwId,
    string? ObjectType,
    string? Title,
    int? Year,
    string? ImdbId,
    string? TmdbId,
    IReadOnlyList<JustWatchOffer> Offers);

/// <summary>Сторінка popularTitles: тайтли + курсор пагінації.</summary>
public sealed record JustWatchPage(IReadOnlyList<JustWatchTitle> Titles, bool HasNextPage, string? EndCursor);

/// <summary>
/// Опції JustWatch-синку (розділ JustWatch конфігу). Значення за замовчуванням —
/// перевірені живцем 09.11.2026 з мережі UA: endpoint віддає 200 на ad-hoc GraphQL
/// (persisted queries НЕ потрібні), language "uk" валідний (віддає українські назви),
/// Country "UA" — оффери регіону. Ввічливість: пауза між запитами ≥800ms.
/// </summary>
public sealed class JustWatchSyncOptions
{
    /// <summary>GraphQL-endpoint (ad-hoc запити приймаються; persisted-хеші не потрібні).</summary>
    public string Endpoint { get; set; } = "https://apis.justwatch.com/graphql";

    /// <summary>User-Agent, який проходить bot-wall endpoint'а (перевірено живцем).</summary>
    public string UserAgent { get; set; } = "insomnia/10.0.0";

    /// <summary>Країна офферів (Country enum, великими літерами).</summary>
    public string Country { get; set; } = "UA";

    /// <summary>Мова контенту (Language enum; "uk" — українські назви, перевірено).</summary>
    public string Language { get; set; } = "uk";

    /// <summary>Тайтлів на сторінку popularTitles.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>Максимум сторінок popular-проходу (Phase A) — кап ввічливості.</summary>
    public int MaxPages { get; set; } = 10;

    /// <summary>Максимум точкових пошуків каталогу за прогін (Phase B).</summary>
    public int SearchLimit { get; set; } = 100;

    /// <summary>Пауза між GraphQL-запитами, ms (ввічливість до apis.justwatch.com).</summary>
    public int RequestDelayMs { get; set; } = 850;

    /// <summary>База ретрай-паузи (30s*спроба в проді; 0 у тестах зі стабом).</summary>
    public int RetryBaseDelayMs { get; set; } = 30_000;
}

/// <summary>Результат прогону JustWatch-синку.</summary>
public sealed record JustWatchSyncResult(int PopularSeen, int PopularMatched, int Searched, int TitlesWithOffers, int ProviderRows, int Pages);

/// <summary>
/// Чисті функції мапінгу JustWatch → Provider (без I/O) — покриті тестами.
/// Рішення власника 09.11: показувати УСІ типи монетизації (бейджі + перемикач
/// «платне» — робота фронтенду); сюди пишемо сирі kind: FREE/ADS→free,
/// FLATRATE→stream, RENT→rent, BUY→buy; невідомі (CINEMA тощо) — пропускаємо.
/// </summary>
public static class JustWatchMapper
{
    public const string Source = "justwatch";

    /// <summary>monetization_type → kind providers-таблиці (free|stream|rent|buy), інше → null.</summary>
    public static string? MapKind(string? monetizationType) => monetizationType?.Trim().ToUpperInvariant() switch
    {
        "FREE" or "ADS" => "free",
        "FLATRATE" => "stream",
        "RENT" => "rent",
        "BUY" => "buy",
        _ => null,
    };

    /// <summary>
    /// Нормалізація назви провайдера: clearName пріоритет (людська назва,
    /// локалізована), фолбек technicalName; обрізаємо пробіли; порожнє → null
    /// (колонка provider NOT NULL — рядок не створюємо).
    /// </summary>
    public static string? ProviderName(string? clearName, string? technicalName)
    {
        var name = (clearName ?? technicalName)?.Trim();
        if (string.IsNullOrEmpty(name) && technicalName is not null)
            name = technicalName.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Оффери одного тайтла → рядки Provider: дедуп за (провайдер, kind) —
    /// один пакет повторюється на кожен presentation-type (4K/HD/SD),
    /// нам потрібен лише факт доступності; невідомі kind і безіменні пакети — пропускаємо.
    /// </summary>
    public static List<Provider> BuildProviders(int titleId, IEnumerable<JustWatchOffer> offers, string region, DateTime now)
    {
        var rows = new List<Provider>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in offers)
        {
            var kind = MapKind(o.MonetizationType);
            if (kind is null) continue; // невідома монетизація (CINEMA тощо)

            var name = ProviderName(o.PackageClearName, o.PackageTechnicalName);
            if (name is null) continue; // провайдер не ідентифікований

            var key = $"{name}|{kind}";
            if (!seen.Add(key)) continue; // дублікат presentation-type

            rows.Add(new Provider
            {
                TitleId = titleId,
                ProviderName = name,
                Kind = kind,
                Url = string.IsNullOrWhiteSpace(o.StandardWebUrl) ? null : o.StandardWebUrl,
                Source = Source,
                Region = region,
                UpdatedAt = now,
            });
        }
        return rows;
    }

    /// <summary>Матч по imdb-ід результатів пошуку (точний, не залежить від мови назв).</summary>
    public static JustWatchTitle? MatchByImdb(IReadOnlyList<JustWatchTitle> results, string imdbId) =>
        results.FirstOrDefault(t => t.ImdbId is not null && t.ImdbId.Equals(imdbId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Фолбек-матч: рік точний + назва (не чутливо до регістру).</summary>
    public static JustWatchTitle? MatchByTitleYear(IReadOnlyList<JustWatchTitle> results, string titleEn, int? year) =>
        results.FirstOrDefault(t => t.Year == year && t.Title is not null &&
            string.Equals(t.Title.Trim(), titleEn.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Тіло POST-запиту GraphQL: query + variables (ад-хок, без persisted-хешів).</summary>
    public static string RequestBody(string query, IReadOnlyDictionary<string, object?> variables) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["query"] = query,
            ["variables"] = variables,
        });

    /// <summary>Фільтр popular-проходу (objectTypes MOVIE/SHOW; excludeIrrelevantTitles ОБОВ'ЯЗКОВО false — true вимагає авторизації, перевірено 422-помилкою).</summary>
    public static IReadOnlyDictionary<string, object?> PopularFilter() => new Dictionary<string, object?>
    {
        ["objectTypes"] = new[] { "MOVIE", "SHOW" },
        ["excludeIrrelevantTitles"] = false,
        ["includeTitlesWithoutUrl"] = true,
        ["releaseYear"] = new Dictionary<string, object?>(),
        ["searchQuery"] = "",
    };

    /// <summary>Variables популяр-сторінки.</summary>
    public static IReadOnlyDictionary<string, object?> PopularVariables(string country, string language, int first, string? after) =>
        new Dictionary<string, object?>
        {
            ["searchTitlesSortBy"] = "POPULAR",
            ["searchTitlesFilter"] = PopularFilter(),
            ["language"] = language,
            ["country"] = country,
            ["first"] = first,
            ["after"] = after,
        };

    /// <summary>Variables точкового пошуку: назва (+ рік, якщо відомий) — матчинг далі по imdb-ід відповіді.</summary>
    public static IReadOnlyDictionary<string, object?> SearchVariables(string country, string language, string query, int? year)
    {
        var filter = new Dictionary<string, object?>
        {
            ["objectTypes"] = new[] { "MOVIE", "SHOW" },
            ["excludeIrrelevantTitles"] = false,
            ["includeTitlesWithoutUrl"] = true,
            ["releaseYear"] = year is { } y
                ? new Dictionary<string, object?> { ["min"] = y, ["max"] = y }
                : new Dictionary<string, object?>(),
            ["searchQuery"] = query,
        };
        return new Dictionary<string, object?>
        {
            ["searchTitlesSortBy"] = "POPULAR",
            ["searchTitlesFilter"] = filter,
            ["language"] = language,
            ["country"] = country,
            ["first"] = 5,
        };
    }

    /// <summary>
    /// Парсинг відповіді popularTitles (System.Text.Json, толерантно до відсутніх
    /// полів). Помилки GraphQL (data:null) віддаємо як ValidationException —
    /// батч пропускається, повторний прогін добирає (ідемпотентність).
    /// </summary>
    public static JustWatchPage ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // GraphQL-помилки: {"errors":[...],"data":null} — кидаємо з першим повідомленням.
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
        {
            var msg = errs[0].TryGetProperty("message", out var m) ? m.GetString() : "GraphQL error";
            throw new InvalidOperationException($"JustWatch GraphQL: {msg}");
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("popularTitles", out var conn) || conn.ValueKind == JsonValueKind.Null)
            return new JustWatchPage([], false, null);

        var titles = new List<JustWatchTitle>();
        if (conn.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
            foreach (var edge in edges.EnumerateArray())
            {
                if (!edge.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.Object) continue;
                titles.Add(ParseNode(node));
            }

        var hasNext = false;
        string? cursor = null;
        if (conn.TryGetProperty("pageInfo", out var pi) && pi.ValueKind == JsonValueKind.Object)
        {
            if (pi.TryGetProperty("hasNextPage", out var hnp) && hnp.ValueKind == JsonValueKind.True) hasNext = true;
            if (pi.TryGetProperty("endCursor", out var ec) && ec.ValueKind == JsonValueKind.String) cursor = ec.GetString();
        }
        return new JustWatchPage(titles, hasNext, cursor);
    }

    /// <summary>Node popularTitles → JustWatchTitle (offers без пакетів зберігаємо — фільтрує BuildProviders).</summary>
    public static JustWatchTitle ParseNode(JsonElement node)
    {
        var id = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
        var objectType = node.TryGetProperty("objectType", out var otEl) && otEl.ValueKind == JsonValueKind.String ? otEl.GetString() : null;

        string? title = null;
        int? year = null;
        string? imdb = null;
        string? tmdb = null;
        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String) title = tEl.GetString();
            if (content.TryGetProperty("originalReleaseYear", out var yEl) && yEl.ValueKind == JsonValueKind.Number && yEl.TryGetInt32(out var y)) year = y;
            if (content.TryGetProperty("externalIds", out var ext) && ext.ValueKind == JsonValueKind.Object)
            {
                if (ext.TryGetProperty("imdbId", out var imEl) && imEl.ValueKind == JsonValueKind.String) imdb = imEl.GetString();
                if (ext.TryGetProperty("tmdbId", out var tmEl) && tmEl.ValueKind == JsonValueKind.String) tmdb = tmEl.GetString();
            }
        }

        var offers = new List<JustWatchOffer>();
        if (node.TryGetProperty("offers", out var os) && os.ValueKind == JsonValueKind.Array)
            foreach (var o in os.EnumerateArray())
            {
                var mt = o.TryGetProperty("monetizationType", out var mtEl) && mtEl.ValueKind == JsonValueKind.String ? mtEl.GetString() : null;
                var url = o.TryGetProperty("standardWebURL", out var uEl) && uEl.ValueKind == JsonValueKind.String ? uEl.GetString() : null;
                string? clear = null, tech = null;
                if (o.TryGetProperty("package", out var pkg) && pkg.ValueKind == JsonValueKind.Object)
                {
                    if (pkg.TryGetProperty("clearName", out var cn) && cn.ValueKind == JsonValueKind.String) clear = cn.GetString();
                    if (pkg.TryGetProperty("technicalName", out var tn) && tn.ValueKind == JsonValueKind.String) tech = tn.GetString();
                }
                offers.Add(new JustWatchOffer(mt, url, clear, tech));
            }

        return new JustWatchTitle(id, objectType, title, year, imdb, tmdb, offers);
    }
}

/// <summary>
/// «Де дивитись» з JustWatch (інформаційний канал UA, БЕЗ ключів): ad-hoc GraphQL
/// на apis.justwatch.com/graphql. Дві фази:
///   A) популяр-прохід country=UA сторінками (Page×PageSize) — матчинг нашого
///      каталогу по imdb_id (індекс ix_titles_imdb_id);
///   B) точковий пошук наших топ-популярних тайтлів БЕЗ justwatch-провайдерів
///      (назва + рік; матч по imdb_id відповіді — надійніший за назву).
/// Запис providers: delete-stale(source=justwatch) + insert за тайтл,
/// атомарно (транзакція на реляційному провайдері), ідемпотентно.
/// Рішення власника: усі типи монетизації (free|stream|rent|buy), бейджі/перемикач — фронт.
/// </summary>
public sealed class JustWatchSyncService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    IOptions<JustWatchSyncOptions> options,
    ILogger<JustWatchSyncService> log)
{
    public const string Source = JustWatchMapper.Source;
    public const string StateKind = "providers";

    private readonly JustWatchSyncOptions _opt = options.Value;

    /// <summary>Ad-hoc GraphQL-операція GetSearchTitles (persisted-хеші не потрібні — перевірено живцем).</summary>
    public const string SearchTitlesQuery = """
        query GetSearchTitles(
          $country: Country!
          $language: Language!
          $searchTitlesFilter: TitleFilter
          $searchTitlesSortBy: PopularTitlesSorting!
          $first: Int!
          $after: String
        ) {
          popularTitles(
            first: $first
            after: $after
            country: $country
            filter: $searchTitlesFilter
            sortBy: $searchTitlesSortBy
          ) {
            edges {
              node {
                id
                objectType
                content(country: $country, language: $language) {
                  title
                  originalReleaseYear
                  fullPath
                  externalIds { imdbId tmdbId }
                }
                offers(country: $country, platform: WEB) {
                  monetizationType
                  standardWebURL
                  package { clearName technicalName }
                }
              }
            }
            pageInfo { hasNextPage endCursor }
          }
        }
        """;

    /// <summary>Повний прогін: Phase A (popular) + Phase B (точковий пошук) + sync_state.</summary>
    public async Task<JustWatchSyncResult> RunAsync(int? maxPages, int? limit, CancellationToken ct = default)
    {
        var pageCap = Math.Clamp(maxPages ?? _opt.MaxPages, 0, _opt.MaxPages * 4);
        var searchCap = Math.Clamp(limit ?? _opt.SearchLimit, 0, _opt.SearchLimit * 10);

        // Фаза A не валить фазу B: popular-прохід пропускається (лог), повторний прогін добере.
        var popularMatched = 0;
        var pages = 0;
        var popularSeen = 0;
        try
        {
            (popularMatched, pages, popularSeen) = await RunPopularAsync(pageCap, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "justwatch: популяр-прохід пропущено після ретраїв");
        }

        var (searched, searchedMatched) = await RunSearchAsync(searchCap, ct);

        var rows = await db.Providers.CountAsync(p => p.Source == Source, ct);
        await UpdateSyncStateAsync(rows, ct);

        var result = new JustWatchSyncResult(popularSeen, popularMatched, searched, searchedMatched, rows, pages);
        log.LogInformation(
            "JustWatch sync завершено: popular_seen={PopularSeen}, popular_matched={PopularMatched}, searched={Searched}, with_offers={WithOffers}, provider_rows={Rows}, pages={Pages}",
            result.PopularSeen, result.PopularMatched, result.Searched, result.TitlesWithOffers, result.ProviderRows, result.Pages);
        return result;
    }

    /// <summary>Phase A: popular-сторінки UA → imdb_id → наш каталог → providers.</summary>
    public async Task<(int Matched, int Pages, int Seen)> RunPopularAsync(int pageCap, CancellationToken ct)
    {
        var seen = 0;
        var pages = 0;
        string? after = null;
        var offersByImdb = new Dictionary<string, IReadOnlyList<JustWatchOffer>>(StringComparer.OrdinalIgnoreCase);

        while (pages < pageCap)
        {
            ct.ThrowIfCancellationRequested();
            var page = await FetchAsync(JustWatchMapper.PopularVariables(_opt.Country, _opt.Language, _opt.PageSize, after), ct);
            pages++;
            foreach (var t in page.Titles)
            {
                seen++;
                if (t.Offers.Count == 0 || string.IsNullOrWhiteSpace(t.ImdbId)) continue;
                offersByImdb[t.ImdbId] = t.Offers; // останній виграє — дублікатів imdb у потоці практично немає
            }
            if (!page.HasNextPage || page.EndCursor is null) break;
            after = page.EndCursor;
            await Task.Delay(_opt.RequestDelayMs, ct);
        }

        var matched = await WriteMatchedAsync(offersByImdb, ct);
        return (matched, pages, seen);
    }

    /// <summary>Phase B: наші топ-популярні тайтли без justwatch-провайдерів → пошук за назвою+ріком → матч по imdb.</summary>
    public async Task<(int Searched, int Matched)> RunSearchAsync(int cap, CancellationToken ct)
    {
        if (cap <= 0) return (0, 0);

        var candidates = await db.Titles.AsNoTracking()
            .Where(t => (t.Type == "movie" || t.Type == "series")
                        && t.ImdbId != null && t.ImdbId != ""
                        && t.TitleEn != ""
                        && !db.Providers.Any(p => p.TitleId == t.Id && p.Source == Source))
            .OrderByDescending(t => t.Popularity)
            .ThenBy(t => t.Id)
            .Take(cap)
            .Select(t => new { t.Id, t.TitleEn, t.Year, t.ImdbId })
            .ToListAsync(ct);

        var matched = 0;
        foreach (var t in candidates)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<JustWatchTitle> results;
            try
            {
                var page = await FetchAsync(JustWatchMapper.SearchVariables(_opt.Country, _opt.Language, t.TitleEn, t.Year), ct);
                results = page.Titles;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Пошук не взявся навіть після ретраїв — тайтл пропускаємо, повторний прогін добере.
                log.LogWarning(ex, "justwatch: пошук «{Title}» пропущено після ретраїв", t.TitleEn);
                continue;
            }

            var hit = JustWatchMapper.MatchByImdb(results, t.ImdbId!) ?? JustWatchMapper.MatchByTitleYear(results, t.TitleEn, t.Year);
            if (hit is not { Offers.Count: > 0 }) continue;

            var providers = JustWatchMapper.BuildProviders(t.Id, hit.Offers, _opt.Country, DateTime.UtcNow);
            if (providers.Count == 0) continue; // усі оффери невідомих kind/без пакетів — нічого не пишемо

            await ReplaceProvidersAsync(db, t.Id, providers, ct);
            matched++;
            await Task.Delay(_opt.RequestDelayMs, ct);
        }
        return (candidates.Count, matched);
    }

    /// <summary>Один GraphQL-запит із ретраями 403/429/502/503/504 (≤4, Retry-After/30s*спроба); 422 — валідація, одразу фейл.</summary>
    public async Task<JustWatchPage> FetchAsync(IReadOnlyDictionary<string, object?> variables, CancellationToken ct)
    {
        var body = JustWatchMapper.RequestBody(SearchTitlesQuery, variables);
        var maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _opt.Endpoint);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.UserAgent.ParseAdd(_opt.UserAgent);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;
            if (status is 403 or 429 or 502 or 503 or 504)
            {
                if (attempt >= maxAttempts)
                    throw new HttpRequestException($"JustWatch {status} після {attempt} спроб");
                var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(_opt.RetryBaseDelayMs * attempt);
                log.LogWarning("justwatch: {Status} — пауза {Retry}s (спроба {Attempt}/{Max})",
                    status, retryAfter.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(retryAfter, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode(); // 4xx валідації (422 тощо) — не ретраїмо

            var json = await resp.Content.ReadAsStringAsync(ct);
            return JustWatchMapper.ParseSearch(json);
        }
    }

    /// <summary>Phase A запис: наші imdb_id з офферами → ReplaceProvidersAsync (батчами по 500 — Contains).</summary>
    public async Task<int> WriteMatchedAsync(Dictionary<string, IReadOnlyList<JustWatchOffer>> offersByImdb, CancellationToken ct)
    {
        var matched = 0;
        var imdbIds = offersByImdb.Keys.ToList();
        for (var i = 0; i < imdbIds.Count; i += 500)
        {
            var chunk = imdbIds.Skip(i).Take(500).ToList();
            var rows = await db.Titles.AsNoTracking()
                .Where(t => t.ImdbId != null && chunk.Contains(t.ImdbId))
                .Select(t => new { t.Id, t.ImdbId })
                .ToListAsync(ct);

            foreach (var t in rows)
            {
                var providers = JustWatchMapper.BuildProviders(t.Id, offersByImdb[t.ImdbId!], _opt.Country, DateTime.UtcNow);
                if (providers.Count == 0) continue;
                await ReplaceProvidersAsync(db, t.Id, providers, ct);
                matched++;
            }
        }
        return matched;
    }

    /// <summary>
    /// Upsert провайдерів тайтла: DELETE source=justwatch (усі регіони) + INSERT свіжі
    /// — двома SaveChanges всередині транзакції (реляційний провайдер) / послідовно
    /// (InMemory). Ідемпотентно: повторний прогін дає той самий набір рядків.
    /// </summary>
    public static async Task ReplaceProvidersAsync(MovieDiscoveryDbContext db, int titleId, List<Provider> rows, CancellationToken ct)
    {
        await using var tx = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var stale = await db.Providers
            .Where(p => p.TitleId == titleId && p.Source == Source)
            .ToListAsync(ct);
        db.Providers.RemoveRange(stale);
        await db.SaveChangesAsync(ct); // DELETE перед INSERT — не тригерить UNIQUE (title, provider, kind, source, region)

        db.Providers.AddRange(rows);
        await db.SaveChangesAsync(ct);

        if (tx is not null) await tx.CommitAsync(ct);
    }

    private async Task UpdateSyncStateAsync(int rows, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == StateKind, ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = StateKind };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = rows;
        await db.SaveChangesAsync(ct);
    }
}

// ─── Черга + фонова обробка (патерн VodSyncQueue: bounded channel, DropOldest) ───

/// <summary>Джоба justwatch-синку: maxPages — кап популяр-проходу, limit — кап точкових пошуків.</summary>
public sealed record JustWatchSyncJob(int? MaxPages, int? Limit);

/// <summary>Черга justwatch-синхронізації.</summary>
public sealed class JustWatchSyncQueue
{
    private readonly System.Threading.Channels.Channel<JustWatchSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<JustWatchSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(JustWatchSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<JustWatchSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка justwatch-джоб (popular-прохід + точковий пошук → providers).</summary>
public sealed class JustWatchSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<JustWatchSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<JustWatchSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<JustWatchSyncService>();
                var result = await service.RunAsync(job.MaxPages, job.Limit, stoppingToken);
                log.LogInformation("JustWatch джоба завершена: popular_matched={Matched}, searched={Searched}, provider_rows={Rows}",
                    result.PopularMatched, result.Searched, result.ProviderRows);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка justwatch-синхронізації (maxPages={MaxPages}, limit={Limit})", job.MaxPages, job.Limit);
            }
        }
    }
}
