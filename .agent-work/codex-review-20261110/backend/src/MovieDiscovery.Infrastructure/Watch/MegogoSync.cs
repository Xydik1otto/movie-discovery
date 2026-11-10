using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Infrastructure.Watch;

/// <summary>Картка безкоштовного каталогу Megogo (сторінка колекції /ua/collection/*).</summary>
public sealed record MegogoCatalogItem(
    int Id,
    string Slug,
    string TitleUk,
    int? Year,
    string? GenreUk,
    string Kind,
    string Url);

/// <summary>VideoObject JSON-LD зі сторінки тайтла /ua/view/*.html (назва uk, оригінал en, жанри).</summary>
public sealed record MegogoVideoLd(string? NameUk, string? TitleEn, string[] GenresUk);

/// <summary>Результат sync Megogo.</summary>
public sealed record MegogoSyncResult(int Cards, int Matched, int Unmatched, int Providers);

/// <summary>
/// Парсер Megogo за РЕАЛЬНОЮ структурою зразків (розвідка 09.11, збережені в scratch):
/// колекції free_films_megogo / free_series_megogo — server-render HTML БЕЗ JSON-LD,
/// картка = &lt;div class="card videoItem" data-obj-id data-title data-vod-rule="advod"&gt; з
/// &lt;a href="/ua/view/{id}-{slug}.html"&gt;, &lt;h3 class="video-title"&gt;, рік/жанр у
/// video-year («2021» або «1989 - 2013»)/video-country, маркер «Безкоштовно» у .free-label.
/// JSON-LD (schema.org VideoObject) є лише на картках тайтлів — name/alternativeHeadline/genre.
/// </summary>
public static partial class MegogoParser
{
    public const string KindFree = "free";

    /// <summary>Хост за замовчуванням (посилання на тайтл регіону UA).</summary>
    public const string SiteBase = "https://megogo.ua";

    /// <summary>Шлях view-сторінки тайтла (єдине джерело правди для клієнта і синку).</summary>
    public static string ViewPath(int id, string slug) => $"/ua/view/{id}-{slug}.html";

    // межі картки: кожна картка починається з <div class="card videoItem …" (з обрізаним класом у зразках)
    [GeneratedRegex("(?=<div class=\"card videoItem)")]
    private static partial Regex CardStart();

    [GeneratedRegex("data-obj-id=\"(?<id>\\d+)\"")]
    private static partial Regex ObjId();

    [GeneratedRegex("href=\"/ua/view/(?<id>\\d+)-(?<slug>[a-z0-9_-]+)\\.html\"")]
    private static partial Regex ViewHref();

    [GeneratedRegex("data-title=\"(?<t>[^\"]*)\"")]
    private static partial Regex DataTitle();

    [GeneratedRegex("class=\"video-year\"[^>]*>(?<y>[^<]*)<")]
    private static partial Regex YearSpan();

    [GeneratedRegex("class=\"video-country\"[^>]*>(?<g>[^<]*)<")]
    private static partial Regex GenreSpan();

    [GeneratedRegex("\\d{4}")]
    private static partial Regex Year4();

    private static readonly Regex LdJson = new(
        "<script[^>]*type=\"application/ld\\+json\"[^>]*>(?<json>.*?)</script>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Картки колекції: id/slug/uk-назва/рік/жанр/kind. Дублікати якорів (2-3 на картку) злиті.</summary>
    public static List<MegogoCatalogItem> ParseCatalogPage(string html)
    {
        var items = new List<MegogoCatalogItem>();
        var seen = new HashSet<int>();
        var chunks = CardStart().Split(html ?? "");
        foreach (var chunk in chunks)
        {
            var idMatch = ObjId().Match(chunk);
            if (!idMatch.Success) continue; // прелюдія сторінки / хвіст без картки
            var id = int.Parse(idMatch.Groups["id"].Value);

            // якорів на картку кілька (thumb/overlay/content) — беремо той, що збігається з data-obj-id
            string? slug = null;
            foreach (Match m in ViewHref().Matches(chunk))
            {
                if (m.Groups["id"].Value != idMatch.Groups["id"].Value) continue;
                slug = m.Groups["slug"].Value;
                break;
            }
            if (slug is null) continue;

            var title = Unescape(DataTitle().Match(chunk).Groups["t"].Value).Trim();
            if (title.Length == 0) continue;

            var year = FirstYear(YearSpan().Match(chunk).Groups["y"].Value);
            var genre = Unescape(GenreSpan().Match(chunk).Groups["g"].Value).Trim();
            if (!seen.Add(id)) continue;

            items.Add(new MegogoCatalogItem(
                id,
                slug,
                title,
                year,
                genre.Length > 0 ? genre : null,
                DetectCardKind(chunk) ?? KindFree, // сама колекція — безкоштовний розділ
                $"{SiteBase}/ua/view/{id}-{slug}.html"));
        }
        return items;
    }

    /// <summary>Саніті-гейт: це справді сторінка колекції {slug} (canonical /ua/collection/{slug})?
    /// Захист від підміни контенту при авто-редіректі 302 → /ua (на головній свої картки, які
    /// не належать безкоштовному розділу).</summary>
    public static bool IsCollectionPage(string html, string slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        var m = CanonicalHref().Match(html ?? "");
        return m.Success
            && m.Groups["h"].Value.EndsWith($"/ua/collection/{slug}", StringComparison.Ordinal);
    }

    [GeneratedRegex("<link[^>]*rel=\"canonical\"[^>]*href=\"(?<h>[^\"]*)\"")]
    private static partial Regex CanonicalHref();

    /// <summary>kind за маркерами картки: advod (ad-VOD) або плашка «Безкоштовно»; інакше null — невідомо.</summary>
    public static string? DetectCardKind(string chunk)
    {
        if (chunk.Contains("data-vod-rule=\"advod\"", StringComparison.Ordinal)) return KindFree;
        if (chunk.Contains("free-label", StringComparison.Ordinal)) return KindFree;
        return null;
    }

    /// <summary>«1989 - 2013» (серіал) → 1989; «2021» → 2021; порожньо → null.</summary>
    public static int? FirstYear(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Year4().Match(raw);
        return m.Success && int.TryParse(m.Value, out var y) ? y : null;
    }

    /// <summary>Мови «Переклад»/«Субтитри» view-сторінки тайтла Megogo (UX-04): csv ISO-639-1 або null.</summary>
    public sealed record MegogoViewLanguages(string? AudioLangs, string? SubLangs);

    /// <summary>
    /// Блок «Переклад» (div.video-block.video-audio) і «Субтитри» (div.video-block.video-cc)
    /// на view-сторінці /ua/view/{id}-{slug}.html: &lt;h4&gt;Переклад&lt;/h4&gt; + &lt;span&gt;Українська&lt;/span&gt;,…
    /// СЕМАНТИКА per-movie: мови САМЕ ЦЬОГО тайтла (контрольна перевірка хвилі-2 — сторінка
    /// без рос. перекладу не містить «Російська» в блоці). Лейбли нормалізує WatchLanguages
    /// (Українська→uk, Російська→ru…); невідомі лейбли відкидаємо (не вигадуємо), дублі злиті.
    /// </summary>
    public static MegogoViewLanguages ParseViewLanguages(string html)
    {
        var audio = Labels(VideoAudioBlock().Match(html ?? "").Groups["body"].Value);
        var subs = Labels(VideoCcBlock().Match(html ?? "").Groups["body"].Value);
        return new MegogoViewLanguages(
            WatchLanguages.NormalizeSet(audio),
            WatchLanguages.NormalizeSet(subs));
    }

    /// <summary>Всі &lt;span&gt;-лейбли в тілі блоку (без h4-заголовка).</summary>
    private static List<string> Labels(string body)
    {
        var labels = new List<string>();
        foreach (Match m in BlockSpan().Matches(body ?? ""))
        {
            var label = Unescape(m.Groups["l"].Value).Trim();
            if (label.Length > 0) labels.Add(label);
        }
        return labels;
    }

    // межі блоку «Переклад»: від <div class="video-block video-audio"> до першого </div>
    [GeneratedRegex("<div class=\"video-block video-audio\"[^>]*>(?<body>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex VideoAudioBlock();

    // межі блоку «Субтитри»: <div class="video-block video-cc">
    [GeneratedRegex("<div class=\"video-block video-cc\"[^>]*>(?<body>.*?)</div>", RegexOptions.Singleline)]
    private static partial Regex VideoCcBlock();

    [GeneratedRegex("<span>(?<l>[^<]+)</span>")]
    private static partial Regex BlockSpan();

    /// <summary>VideoObject JSON-LD зі сторінки тайтла: name (uk), alternativeHeadline (оригінал), genre.</summary>
    public static MegogoVideoLd? ParseVideoObject(string html)
    {
        foreach (Match m in LdJson.Matches(html ?? ""))
        {
            JsonDocument? doc;
            try { doc = JsonDocument.Parse(m.Groups["json"].Value); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!IsType(root, "VideoObject")) continue;
                return new MegogoVideoLd(
                    Str(root, "name"),
                    Str(root, "alternativeHeadline"),
                    StrList(root, "genre"));
            }
        }
        return null;
    }

    /// <summary>Мінімальний декодер HTML-сутностей у значеннях атрибутів (&amp; — останнім).</summary>
    public static string Unescape(string raw) => raw
        .Replace("&lt;", "<").Replace("&gt;", ">")
        .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
        .Replace("&nbsp;", " ")
        .Replace("&amp;", "&");

    private static bool IsType(JsonElement el, string type)
    {
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("@type", out var t)) return false;
        return t.ValueKind == JsonValueKind.String
            ? string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase)
            : t.ValueKind == JsonValueKind.Array && t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String
                && string.Equals(x.GetString(), type, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[] StrList(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return [];
        return v.ValueKind switch
        {
            JsonValueKind.Array => v.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => (x.GetString() ?? "").Trim())
                .Where(s => s.Length > 0).ToArray(),
            JsonValueKind.String when v.GetString() is { Length: > 0 } s => s.Split(',')
                .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray(),
            _ => [],
        };
    }
}

/// <summary>
/// Синк безкоштовного каталогу Megogo (UA): колекції free_films_megogo і free_series_megogo
/// (пагінація ?page=N; кінець видачі — 302 на /ua або порожня сторінка) → картки (id, uk-назва,
/// рік, kind) → матч у titles за нормалізованою uk-назвою + рік ±1 (TitleMatcher) →
/// providers (Source=megogo, Region=UA, Kind=free). Тайтли НЕ створюються і не перезаписуються —
/// лише впевнені збіги. Ідемпотентність: прогін ЗАМІНЮЄ рядки source=megogo на свіжі.
/// </summary>
public sealed class MegogoSyncService(
    MegogoClient client,
    MovieDiscoveryDbContext db,
    IOptions<MegogoOptions> options,
    ILogger<MegogoSyncService> log)
{
    public const string Source = "megogo";
    public const string ProviderName = "Megogo";
    public const string Region = "UA";
    public const string KindFree = MegogoParser.KindFree;
    public const int DefaultMaxPages = 50;

    private readonly MegogoOptions _opt = options.Value;

    public async Task<MegogoSyncResult> SyncAsync(int? maxPages, CancellationToken ct = default)
    {
        var effectivePages = Math.Clamp(maxPages ?? _opt.MaxPages, 1, 500);

        var films = await CrawlCollectionAsync(_opt.FilmsCollectionPath, effectivePages, ct);
        var series = await CrawlCollectionAsync(_opt.SeriesCollectionPath, effectivePages, ct);

        // глобальний дедуп: один Megogo-id → одна картка (колекції не перетинаються, але не важаємо)
        var seenIds = new HashSet<int>();
        var cards = new List<MegogoCatalogItem>();
        foreach (var card in films.Concat(series))
            if (seenIds.Add(card.Id)) cards.Add(card);

        log.LogInformation("megogo: зібрано карток={Films}+{Series}={Cards} (maxPages={MaxPages})",
            films.Count, series.Count, cards.Count, effectivePages);

        var rows = new List<Provider>();
        var matched = 0;
        var unmatched = 0;
        var duplicates = 0;
        var unmatchedSamples = new List<string>();
        if (cards.Count > 0)
        {
            var index = IndexCandidates(await LoadCandidatesAsync(cards, ct));
            var seenTitles = new HashSet<int>();
            foreach (var card in cards)
            {
                if (card.Year is null)
                {
                    // без року впевненого матчу нема (±1) — як в archive-синку
                    unmatched++;
                    continue;
                }

                var found = TitleMatcher.Find(
                    index.Lookup(TitleNormalizer.Normalize(card.TitleUk)).ToList(),
                    card.TitleUk, null, card.Year);
                if (found is null)
                {
                    unmatched++;
                    if (unmatchedSamples.Count < 10) unmatchedSamples.Add($"{card.TitleUk} ({card.Year})");
                    continue;
                }
                if (!seenTitles.Add(found.Id))
                {
                    // дві картки megogo вказали на один тайтл — UNIQUE(title, provider, kind, source, region)
                    duplicates++;
                    continue;
                }

                rows.Add(BuildProviderRow(found.Id, card));
                matched++;
            }
        }

        // заміна рядків джерела: свіжі — замість усіх source=megogo (застарілі знімаються теж)
        var existing = await db.Providers.Where(p => p.Source == Source).ToListAsync(ct);
        db.Providers.RemoveRange(existing);
        db.Providers.AddRange(rows);
        await db.SaveChangesAsync(ct);

        await UpdateSyncStateAsync(rows.Count, ct);
        if (unmatchedSamples.Count > 0)
            log.LogDebug("megogo: приклади без матчу: {Samples}", string.Join("; ", unmatchedSamples));
        log.LogInformation(
            "megogo sync: карток={Cards}, матчів={Matched}, без матчу={Unmatched}, дублів-карток={Duplicates}, рядків providers={Providers}",
            cards.Count, matched, unmatched, duplicates, rows.Count);
        return new MegogoSyncResult(cards.Count, matched, unmatched, rows.Count);
    }

    /// <summary>URL сторінки колекції: 1 — без query, N&gt;1 — ?page=N (без /search — robots).</summary>
    internal static string CatalogPageUrl(string collectionSlug, int page) =>
        page <= 1 ? $"/ua/collection/{collectionSlug}" : $"/ua/collection/{collectionSlug}?page={page}";

    /// <summary>Рядок providers для підтвердженого матчу (чиста функція — тести).</summary>
    internal static Provider BuildProviderRow(int titleId, MegogoCatalogItem card) => new()
    {
        TitleId = titleId,
        ProviderName = ProviderName,
        Kind = KindFree,
        Url = card.Url,
        Source = Source,
        Region = Region,
        UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>Обхід колекції до maxPages; стоп — null (302/помилка), порожня сторінка або 0 нових id.</summary>
    private async Task<List<MegogoCatalogItem>> CrawlCollectionAsync(string slug, int maxPages, CancellationToken ct)
    {
        var items = new List<MegogoCatalogItem>();
        var seen = new HashSet<int>();
        for (var page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var html = await client.GetHtmlAsync(CatalogPageUrl(slug, page), ct);
            if (html is null) break; // 302 → /ua = кінець видачі (або помилка)
            if (!MegogoParser.IsCollectionPage(html, slug))
            {
                // авто-редірект віддав іншу сторінку (напр. головну) — не змішувати чужі картки
                log.LogWarning("megogo: {Path} віддав не колекцію — обхід зупинено", CatalogPageUrl(slug, page));
                break;
            }

            var cards = MegogoParser.ParseCatalogPage(html);
            var fresh = 0;
            foreach (var card in cards)
                if (seen.Add(card.Id))
                {
                    items.Add(card);
                    fresh++;
                }
            if (fresh == 0) break; // редірект на головну (дублікати) або порожня сторінка
        }
        return items;
    }

    /// <summary>Кандидати: movie+series з роком у вікні карток ±1 (індекс titles(year)), одним запитом на чанк.</summary>
    private async Task<List<Title>> LoadCandidatesAsync(List<MegogoCatalogItem> cards, CancellationToken ct)
    {
        var years = new HashSet<int>();
        foreach (var card in cards)
            if (card.Year is { } y)
            {
                years.Add(y - 1);
                years.Add(y);
                years.Add(y + 1);
            }

        var candidates = new List<Title>();
        foreach (var chunk in years.Chunk(500))
        {
            var set = chunk.ToHashSet();
            candidates.AddRange(await db.Titles.AsNoTracking()
                .Where(t => (t.Type == "movie" || t.Type == "series") && t.Year != null && set.Contains(t.Year.Value))
                .ToListAsync(ct));
        }
        return candidates;
    }

    private static CandidateIndex IndexCandidates(List<Title> candidates)
    {
        var index = new CandidateIndex();
        foreach (var c in candidates)
        {
            var en = TitleNormalizer.Normalize(c.TitleEn);
            if (en.Length > 0) index.Add(en, c);
            var uk = TitleNormalizer.Normalize(c.TitleUk);
            if (uk.Length > 0 && uk != en) index.Add(uk, c);
        }
        return index;
    }

    /// <summary>Індекс кандидатів за нормалізованою назвою (одна назва → кілька тайтлів).</summary>
    private sealed class CandidateIndex
    {
        private readonly Dictionary<string, List<Title>> _map = new(StringComparer.Ordinal);

        public void Add(string normalized, Title title)
        {
            if (!_map.TryGetValue(normalized, out var list)) _map[normalized] = list = [];
            list.Add(title);
        }

        public IEnumerable<Title> Lookup(string normalized) =>
            _map.TryGetValue(normalized, out var list) ? list : [];
    }

    private async Task UpdateSyncStateAsync(int providers, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "free_catalog", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "free_catalog" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = providers;
        await db.SaveChangesAsync(ct);
    }
}

// ─── Черга + фонова джоба (патерн VodSyncQueue) ───

/// <summary>Джоба megogo-синка: безкоштовний каталог UA → providers.</summary>
public sealed record MegogoSyncJob(int? MaxPages);

/// <summary>Черга megogo-синхронізації (202 → фонова джоба).</summary>
public sealed class MegogoSyncQueue
{
    private readonly System.Threading.Channels.Channel<MegogoSyncJob> _channel =
        System.Threading.Channels.Channel.CreateBounded<MegogoSyncJob>(
            new System.Threading.Channels.BoundedChannelOptions(10)
            {
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });

    public int Count => _channel.Reader.Count;

    public bool TryEnqueue(MegogoSyncJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<MegogoSyncJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Фонова обробка megogo-джоб (обхід колекцій → матчинг → providers).</summary>
public sealed class MegogoSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<MegogoSyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = scopeFactory.CreateScope().ServiceProvider.GetRequiredService<MegogoSyncQueue>();
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<MegogoSyncService>();
                var result = await service.SyncAsync(job.MaxPages, stoppingToken);
                log.LogInformation("Megogo джоба завершена: карток={Cards}, матчів={Matched}, рядків={Providers}",
                    result.Cards, result.Matched, result.Providers);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка megogo-синхронізації (maxPages={MaxPages})", job.MaxPages);
            }
        }
    }
}
