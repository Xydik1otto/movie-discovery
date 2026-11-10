using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>Результат sync sweet.tv.</summary>
public sealed record SweetTvSyncResult(int Cards, int TitlesCreated, int TitlesMatched, int Links);

/// <summary>
/// Парсинг каталогу та карток sweet.tv (FR-14, PRIMARY UA-провайдер):
/// каталог /ua-uk/movie (JSON-LD ItemList + href-фолбек) → картки фільмів (назва укр.,
/// рік, kind доступу) → upsert titles (type=movie, title_uk, source sweettv) +
/// watch_links (provider=sweettv, kind=free|subscription, region=UA).
/// Тротлінг 1 req/2 c; /search і /payment НЕ парсаться (robots).
/// </summary>
public sealed class SweetTvService(
    HttpClient http,
    MovieDiscoveryDbContext db,
    IOptions<SweetTvOptions> options,
    SweetTvThrottle throttle,
    ILogger<SweetTvService> log)
{
    public const string Source = "sweettv";
    public const string ProviderName = "sweettv";
    public const string Region = "UA";
    public const double PopularityConst = 5.0; // невелика константа: sweet.tv-тайтли нижче TMDB-популярних

    private readonly SweetTvOptions _opt = options.Value;

    public async Task<SweetTvSyncResult> SyncAsync(int pages, CancellationToken ct = default)
    {
        var result = new SweetTvSyncResult(0, 0, 0, 0);
        var seen = new HashSet<int>();

        for (var page = 1; page <= Math.Max(1, pages); page++)
        {
            ct.ThrowIfCancellationRequested();
            var html = await FetchAsync(CatalogUrl(page), ct);
            if (html is null) break;

            var items = SweetTvParser.ParseCatalog(html);
            if (items.Count == 0) break;

            foreach (var item in items)
                if (seen.Add(item.Id)) result = await ProcessCardAsync(item, result, ct);
            if (result.Cards >= _opt.MaxCards) break;
        }

        await UpdateSyncStateAsync(result, ct);
        log.LogInformation("sweet.tv sync: карток={Cards}, створено={Created}, матчів={Matched}, посилань={Links}",
            result.Cards, result.TitlesCreated, result.TitlesMatched, result.Links);
        return result;
    }

    /// <summary>Сторінка каталогу: 1 — /ua-uk/movie; N&gt;1 — /ua-uk/movie?page=N (без /search!).</summary>
    internal static string CatalogUrl(int page) =>
        page <= 1 ? "/ua-uk/movie" : $"/ua-uk/movie?page={page}";

    private async Task<SweetTvSyncResult> ProcessCardAsync(SweetCatalogItem item, SweetTvSyncResult result, CancellationToken ct)
    {
        var cardHtml = await FetchAsync($"/ua-uk/movie/{item.Id}-{item.Slug}", ct);
        var card = cardHtml is null ? null : SweetTvParser.ParseMovieCard(cardHtml);
        if (card is null)
        {
            log.LogWarning("sweet.tv: картка {Id} не розібрана — пропущено", item.Id);
            return result;
        }

        // матч за назвою + рік ±1 (кандидати: movie з річним вікном; також тайтли без року)
        var candidates = await LoadCandidatesAsync(card.Year, ct);
        var match = TitleMatcher.Find(candidates, card.NameUk, card.TitleEn, card.Year);

        Title title;
        bool created;
        if (match is null)
        {
            title = new Title
            {
                Type = "movie",
                TitleUk = card.NameUk,
                TitleEn = card.TitleEn ?? "",
                Year = card.Year,
                Genres = SweetTvParser.NormalizeGenres(item.GenresUk),
                Popularity = PopularityConst,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Titles.Add(title);
            created = true;
        }
        else
        {
            title = match;
            created = false;
            FillMissing(title, card);
        }
        await db.SaveChangesAsync(ct);

        // watch_link: UNIQUE(title_id, provider, kind, region, COALESCE(audio_langs,'')) —
        // повторний sync не дублює. UX-04: мови озвучки/субтитрів САМЕ ЦЬОГО тайтла
        // з блоку «Аудіодоріжки»/«Субтитри» view-сторінки (per-movie, не sitewide);
        // блок відсутній → NULL (невідома мова, не uk/ru за замовчуванням).
        var langs = SweetTvParser.ParseLanguages(cardHtml ?? "");
        var link = new WatchLink
        {
            TitleId = title.Id,
            Provider = ProviderName,
            Kind = card.Kind,
            Region = Region,
            Url = card.Url,
            IsAvailable = true,
            AudioLangs = langs.AudioLangs,
            SubLangs = langs.SubLangs,
            CheckedAt = DateTime.UtcNow,
        };
        var existingLinks = await db.WatchLinks
            .Where(w => w.TitleId == title.Id && w.Provider == ProviderName)
            .ToListAsync(ct);
        var (resolved, isNew) = WatchLinkUpsertCore.Resolve(existingLinks, link);
        if (isNew) db.WatchLinks.Add(resolved);
        else WatchLinkUpsertCore.Merge(resolved, link);
        await db.SaveChangesAsync(ct);

        return result with
        {
            Cards = result.Cards + 1,
            TitlesCreated = result.TitlesCreated + (created ? 1 : 0),
            TitlesMatched = result.TitlesMatched + (created ? 0 : 1),
            Links = result.Links + 1,
        };
    }

    /// <summary>Повторний sync: дозаповнюємо порожні поля, нічого не затираємо.</summary>
    private static void FillMissing(Title title, SweetMovieCard card)
    {
        if (string.IsNullOrWhiteSpace(title.TitleUk)) title.TitleUk = card.NameUk;
        if (string.IsNullOrWhiteSpace(title.TitleEn) && card.TitleEn is { } en) title.TitleEn = en;
        title.Year ??= card.Year;
        title.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Кандидати матчингу: movie у вікні року ±1 (індекс titles(year)) + тайтли без року.
    /// БЕЗ AsNoTracking — знайдений кандидат оновлюється (FillMissing) і має трекатись.</summary>
    private async Task<List<Title>> LoadCandidatesAsync(int? year, CancellationToken ct)
    {
        var query = db.Titles.Where(t => t.Type == "movie");
        query = year is { } y
            ? query.Where(t => t.Year == null || (t.Year >= y - 1 && t.Year <= y + 1))
            : query.Where(t => t.Year == null);
        return await query.ToListAsync(ct);
    }

    private async Task<string?> FetchAsync(string pathAndQuery, CancellationToken ct)
    {
        var url = _opt.BaseUrl.TrimEnd('/') + pathAndQuery;
        return await throttle.RunAsync(async () =>
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("sweet.tv {Status} на {Path} — пропущено", (int)resp.StatusCode, pathAndQuery);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }, ct);
    }

    private async Task UpdateSyncStateAsync(SweetTvSyncResult result, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == Source && s.Kind == "movie", ct);
        if (state is null)
        {
            state = new SyncState { Source = Source, Kind = "movie" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = result.Links;
        await db.SaveChangesAsync(ct);
    }
}
