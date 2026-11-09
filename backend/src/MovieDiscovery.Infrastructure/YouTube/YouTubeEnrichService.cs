using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Infrastructure.YouTube;

/// <summary>Шаблон пошукового запиту YouTube за типом тайтла (контракт FR-20).</summary>
public static class YoutubeQueryBuilder
{
    /// <summary>Повнометражне видання: movie або аніме-фільм (movie/ova/ona/special).</summary>
    public static bool IsMovieLike(Title t) =>
        t.Type is "movie"
        || (t.Type is "anime" or "manga" && t.Format is "movie" or "ova" or "ona" or "special");

    /// <summary>
    /// movie/anime-movie → «{назва} {рік} full movie»; series/anime-tv → «{назва} {рік} full episode»;
    /// show (телешоу) → «{назва} season 1 full episode». Назва — en (fallback uk).
    /// </summary>
    public static string BuildQuery(Title t)
    {
        var name = (!string.IsNullOrWhiteSpace(t.TitleEn) ? t.TitleEn : t.TitleUk ?? "").Trim();
        if (name.Length == 0) return "";

        var tail = t.Type switch
        {
            "show" => "season 1 full episode",
            "series" => "full episode",
            "anime" or "manga" => IsMovieLike(t) ? "full movie" : "full episode",
            _ => "full movie",
        };
        var year = t.Year is { } y && t.Type != "show" ? $" {y}" : "";
        return $"{name}{year} {tail}".Trim();
    }
}

/// <summary>Фільтр трейлерів/тизерів та валідація тривалості повного видання.</summary>
public static partial class YoutubeFilters
{
    [GeneratedRegex("trailer|teaser|official clip", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailerRx();

    /// <summary>Трейлер/тизер/кліп у назві або описі → НЕ повне видання.</summary>
    public static bool IsTrailer(string title, string description) =>
        TrailerRx().IsMatch(title ?? "") || TrailerRx().IsMatch(description ?? "");

    /// <summary>Повне видання: movie/anime-movie ≥ 45 хв; серіал-епізод ≥ 18 хв.</summary>
    public static bool DurationOk(bool movieLike, TimeSpan? duration) =>
        duration is { } d && d >= TimeSpan.FromMinutes(movieLike ? 45 : 18);
}

/// <summary>
/// Матчинг відео до тайтла за назвою (укр/англ; нормалізовані токени — суцільна
/// послідовність у назві відео) + рік ±1, якщо рік видний у назві/описі відео.
/// </summary>
public static partial class YoutubeMatcher
{
    [GeneratedRegex("(?<!\\d)(19\\d{2}|20\\d{2})(?!\\d)", RegexOptions.CultureInvariant)]
    private static partial Regex YearRx();

    public static int? ExtractYear(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = YearRx().Match(text);
        return m.Success ? int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public static bool Matches(Title t, string videoTitle, string videoDescription = "")
    {
        var videoTokens = TitleNormalizer.Normalize(videoTitle)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (videoTokens.Length == 0) return false;

        var matched =
            ContainsContiguous(videoTokens, Tokens(t.TitleEn)) ||
            ContainsContiguous(videoTokens, Tokens(t.TitleUk));
        if (!matched) return false;

        // рік у відео суперечить року тайтла (> ±1) → не той реліз (сиквел/ремейк)
        var videoYear = ExtractYear(videoTitle) ?? ExtractYear(videoDescription);
        if (videoYear is { } vy && t.Year is { } ty && Math.Abs(vy - ty) > 1) return false;
        return true;
    }

    private static string[] Tokens(string? name) =>
        TitleNormalizer.Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>needle — суцільна підпослідовність haystack (word-safe: «it» не збігається з «italian»).</summary>
    public static bool ContainsContiguous(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }
}

/// <summary>
/// Розпізнавання мови аудіо/озвучки з назви/опису/каналу відео (audio_lang watch_links, FR-19).
/// uk-пріоритет; нічого не розпізнано → NULL (порожнє значення — легальне).
/// </summary>
public static class YoutubeLangDetector
{
    private static readonly (string Code, string[] Keys)[] Map =
    [
        ("multi", ["multi", "багатомовн", "багатоязич", "двомовн"]),
        ("ukr", ["ukrainian", "україн", "укр"]),
        ("russ", ["russian", "рус", "рос"]),
        ("jpn", ["japanese", "япон"]),
        ("kor", ["korean", "корей"]),
        ("zho", ["chinese", "mandarin", "китайс"]),
        ("eng", ["english", "англ", "eng"]),
    ];

    public static string? Detect(string? title, string? description, string? channelTitle)
    {
        var text = $"{title} {description} {channelTitle}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var (code, keys) in Map)
            if (keys.Any(text.Contains))
                return code;
        return null;
    }
}

/// <summary>
/// FR-20: YouTube як джерело перегляду. Search+матч → watch_links (provider=youtube,
/// kind=free, region=*, dub=original, audio_lang за мовою якщо розпізнано, quality за definition).
/// Постер-фолбек: порожній poster_path → прев'ю відео (повний URL i.ytimg.com).
/// Квота-дисципліна: search-відповіді кешуються в source_cache 24 год; on-demand — 1 search/запит.
/// </summary>
public sealed class YouTubeEnrichService(
    YoutubeClient client,
    MovieDiscoveryDbContext db,
    ICacheStore cache,
    IOptions<YoutubeOptions> options,
    ILogger<YouTubeEnrichService> log)
{
    public const string Provider = "youtube";

    private readonly YoutubeOptions _opt = options.Value;

    public bool IsConfigured => client.IsConfigured;

    /// <summary>
    /// On-demand збагачення ОДНОГО тайтла (GET /api/titles/{id}/watch?enrich=youtube):
    /// якщо youtube-лінку нема → 1 search (кеш 24 год) + матч + upsert. false → нічого не додано
    /// (нема ключа / тайтл не знайдено / лінк вже є / матч не знайдено / API-помилка).
    /// </summary>
    public async Task<bool> EnsureYoutubeLinkAsync(int titleId, CancellationToken ct = default)
    {
        if (!IsConfigured) return false;

        var title = await db.Titles.FirstOrDefaultAsync(t => t.Id == titleId, ct);
        if (title is null) return false;

        // youtube-лінк уже є — on-demand повторно не шукаємо
        if (await db.WatchLinks.AsNoTracking()
                .AnyAsync(w => w.TitleId == titleId && w.Provider == Provider, ct))
            return false;

        try
        {
            return await FindAndUpsertAsync(title, ct) is not null;
        }
        catch (YoutubeApiException ex)
        {
            log.LogWarning("YouTube enrich недоступний (title {Id}): {Msg}", titleId, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Батч-синк: топ-N тайтлів БЕЗ youtube-лінку (movie/series/anime/show; манга — не відео-контент)
    /// за popularity → search+матч → watch_links. API-помилка (квота) — м'яко зупиняє прогін.
    /// </summary>
    public async Task<int> SyncAsync(int limit, CancellationToken ct = default)
    {
        if (!IsConfigured) return 0;

        var withYoutube = db.WatchLinks
            .Where(w => w.Provider == Provider)
            .Select(w => w.TitleId)
            .Distinct();
        var ids = await db.Titles.AsNoTracking()
            .Where(t => t.Type != "manga" && !withYoutube.Contains(t.Id))
            .OrderByDescending(t => t.Popularity)
            .ThenByDescending(t => t.Year ?? 0)
            .Take(Math.Clamp(limit, 1, 10_000))
            .Select(t => t.Id)
            .ToListAsync(ct);

        var added = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var title = await db.Titles.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (title is null) continue;
                if (await FindAndUpsertAsync(title, ct) is not null) added++;
            }
            catch (YoutubeApiException ex)
            {
                log.LogWarning("YouTube sync зупинено (title {Id}): {Msg}", id, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Помилка youtube-збагачення тайтла {Id}", id);
            }
        }

        await UpdateSyncStateAsync(added, ct);
        log.LogInformation("YouTube sync завершено: кандидатів {Total}, додано {Added}", ids.Count, added);
        return added;
    }

    /// <summary>Пошук + фільтри + матч + upsert для одного тайтла. Повертає додане посилання (або null).</summary>
    public async Task<WatchLink?> FindAndUpsertAsync(Title title, CancellationToken ct = default)
    {
        var query = YoutubeQueryBuilder.BuildQuery(title);
        if (query.Length == 0) return null;

        var items = await SearchCachedAsync(query, ct);
        if (items.Count == 0) return null;

        // 1 videos.list на всіх кандидатів (~1 unit) — duration/definition/прев'ю
        var details = await client.DetailsAsync(items.Select(i => i.VideoId).ToList(), ct);
        var byId = details.ToDictionary(v => v.VideoId, StringComparer.Ordinal);

        var trailerVideoId = (string?)null;
        foreach (var item in items) // порядок релевантності API
        {
            if (!byId.TryGetValue(item.VideoId, out var video)) continue;

            // FR-20+: трейлери пропускаємо, але попутно ловимо ОФІЦІЙНИЙ трейлер тайтла —
            // titles.trailer_yt_id більше не залежить лише від TmdbMapper (TMDB не використовується).
            if (YoutubeFilters.IsTrailer(video.Title, video.Description))
            {
                if (YoutubeEnrichLogic.TreatAsTrailer(title, video.Title, video.Description, title.TrailerYtId))
                {
                    title.TrailerYtId = video.VideoId;
                    trailerVideoId = video.VideoId;
                    log.LogInformation(
                        "YouTube офіційний трейлер записано: title={TitleId}, video={VideoId}", title.Id, video.VideoId);
                }
                continue; // трейлер НЕ лінк для перегляду — пошук watch-лінка триває
            }
            if (!YoutubeFilters.DurationOk(
                    YoutubeQueryBuilder.IsMovieLike(title),
                    YoutubeParser.ParseDuration(video.Duration)))
                continue;
            if (!YoutubeMatcher.Matches(title, video.Title, video.Description)) continue;

            // SaveChanges в UpsertLinkAsync зберігає і записаний тут TrailerYtId (title tracked) — один раз
            return await UpsertLinkAsync(title, video, ct);
        }

        // Лінка не знайдено: якщо трейлер записали — зберігаємо його самостійно
        // (інакше мутація title залишилась би незбереженою в EnsureYoutubeLinkAsync/SyncAsync)
        if (trailerVideoId is not null)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("YouTube трейлер збережено без watch-лінка: title={TitleId}, video={VideoId}",
                title.Id, trailerVideoId);
        }
        return null;
    }

    /// <summary>search.list з 24-год кешем у source_cache (акуратно з квотою 100 units/запит).</summary>
    internal async Task<IReadOnlyList<YoutubeSearchItem>> SearchCachedAsync(string query, CancellationToken ct)
    {
        var key = $"search:{query.Trim().ToLowerInvariant()}";
        var cached = await cache.GetAsync("youtube", key, ct);
        if (cached is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<List<YoutubeSearchItem>>(cached) ?? [];
            }
            catch (JsonException)
            {
                // протухлий формат кешу — перезапитуємо
            }
        }

        var items = await client.SearchFullAsync(query, _opt.MaxResults, ct);
        await cache.SetAsync("youtube", key, JsonSerializer.Serialize(items), _opt.CacheTtlSeconds, ct);
        return items;
    }

    /// <summary>Ідемпотентний upsert watch_links (provider=youtube, kind=free, region=*) + постер-фолбек.</summary>
    internal async Task<WatchLink> UpsertLinkAsync(Title title, YoutubeVideo video, CancellationToken ct)
    {
        var patch = new WatchLink
        {
            TitleId = title.Id,
            Provider = Provider,
            Kind = "free",
            Region = "*", // YouTube глобальний
            Url = $"https://www.youtube.com/watch?v={video.VideoId}",
            IsAvailable = true,
            CheckedAt = DateTime.UtcNow,
            AudioLang = YoutubeLangDetector.Detect(video.Title, video.Description, video.ChannelTitle),
            Quality = YoutubeParser.QualityFromDefinition(video.Definition),
            Dub = "original",
        };

        var existing = await db.WatchLinks
            .Where(w => w.TitleId == title.Id && w.Provider == Provider)
            .ToListAsync(ct);
        var (link, created) = WatchLinkUpsertCore.Resolve(existing, patch);
        WatchLinkUpsertCore.Merge(link, patch);
        if (created) db.WatchLinks.Add(link);

        // Постер-фолбек: titles.poster_path IS NULL/'' → прев'ю YouTube (ПОВНИЙ URL — TitleCard вже
        // пропускає абсолютні URL, фронт показує будь-який)
        if (string.IsNullOrWhiteSpace(title.PosterPath))
            title.PosterPath = $"https://i.ytimg.com/vi/{video.VideoId}/hqdefault.jpg";

        await db.SaveChangesAsync(ct);
        log.LogInformation("YouTube лінк {State}: title={TitleId}, video={VideoId}, quality={Quality}, audio={Audio}",
            created ? "додано" : "оновлено", title.Id, video.VideoId, patch.Quality, patch.AudioLang);
        return link;
    }

    /// <summary>sync_state source=youtube kind=enrich (прогрес батч-синку).</summary>
    private async Task UpdateSyncStateAsync(int added, CancellationToken ct)
    {
        var state = await db.SyncStates.FirstOrDefaultAsync(s => s.Source == "youtube" && s.Kind == "enrich", ct);
        if (state is null)
        {
            state = new SyncState { Source = "youtube", Kind = "enrich" };
            db.SyncStates.Add(state);
        }
        state.LastRunAt = DateTime.UtcNow;
        state.LastCount = added;
        await db.SaveChangesAsync(ct);
    }
}
