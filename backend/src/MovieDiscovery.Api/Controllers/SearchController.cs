using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/search")]
public sealed class SearchController(SearchService search, MovieDiscoveryDbContext db, IMemoryCache cache) : ControllerBase
{
    /// <summary>
    /// TTL кешу дефолтного каталогу: короткий (каталог живий), але достатній, щоб
    /// повторні навігації на «Головну» в межах хвилин не перераховували той самий запит.
    /// </summary>
    internal static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(90);

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? q,
        [FromQuery] string? type,
        [FromQuery] string? genres,
        [FromQuery] int? yearFrom,
        [FromQuery] int? yearTo,
        [FromQuery] string? country,
        [FromQuery] string? lang,
        [FromQuery] double? minRating,
        [FromQuery] double? maxRating,
        [FromQuery] int? minRuntime,
        [FromQuery] int? maxRuntime,
        [FromQuery] string? status,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 24,
        // ── Розширені фільтри ──
        [FromQuery] string? actor = null,
        [FromQuery] string? director = null,
        [FromQuery] string? studio = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? language = null,
        [FromQuery] int? minVotes = null,
        [FromQuery] int? seasonsFrom = null,
        [FromQuery] int? seasonsTo = null,
        [FromQuery] string? excludeGenres = null,
        [FromQuery] bool? hasTrailer = null,
        [FromQuery] bool? hasProviders = null,
        [FromQuery] string? ratingSource = null,
        [FromQuery] string? providerRegion = null,
        [FromQuery] bool? watchable = null,
        // ── Віртуальні категорії та озвучки (FR-19) ──
        [FromQuery] string? category = null,
        [FromQuery] string? audioLang = null,
        // minQuality біндиться РЯДКОМ (не int?): ASP.NET-біндинг int? на 'hd' давав
        // беззмістовний 400 "The value 'hd' is not valid" ДО входу в екшен. Дружній
        // парсер (ParseMinQuality) приймає і числа, і акаунт-рівні (hd/fhd/uhd/sd/Np).
        [FromQuery] string? minQuality = null,
        // UX-01: швидкі перегортання — клієнт із кешованим total просить пропустити COUNT.
        [FromQuery] bool omitTotal = false,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(type) && type is not ("movie" or "series" or "show" or "anime" or "manga"))
            return BadRequest(new { error = "invalid_request", message = "type має бути movie | series | show | anime | manga" });

        var langSafe = Lang(lang);
        var genreList = ParseGenres(genres);
        if (genreList is null)
            return BadRequest(new { error = "invalid_request", message = "Некоректний фільтр жанрів" });

        var excludeList = ParseGenres(excludeGenres);
        if (excludeList is null)
            return BadRequest(new { error = "invalid_request", message = "Некоректний фільтр виключених жанрів" });

        if (!string.IsNullOrWhiteSpace(language) && !System.Text.RegularExpressions.Regex.IsMatch(language.Trim(), @"^[a-zA-Z]{2,3}$"))
            return BadRequest(new { error = "invalid_request", message = "language має бути ISO-кодом (2-3 літери)" });

        if (!string.IsNullOrWhiteSpace(ratingSource) && ratingSource is not ("tmdb" or "imdb"))
            return BadRequest(new { error = "invalid_request", message = "ratingSource має бути tmdb | imdb" });

        // FR-19: валідація віртуальних категорій/озвучок/якості (усі опційні)
        if (!Core.Search.VirtualCategories.IsValid(category))
            return BadRequest(new { error = "invalid_request", message = "category має бути movie | series | anime | manga | tvshow | cartoon | dorama" });

        var audioLangSafe = audioLang?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(audioLangSafe) && !Core.Search.VirtualCategories.AudioLangs.Contains(audioLangSafe))
            return BadRequest(new { error = "invalid_request", message = "audioLang має бути ukr | russ | eng | jpn | kor | zho | original | multi" });

        // FR-19: minQuality — дружній парсер (числа + акаунт-рівні), потім allowlist.
        var (minQualityValue, minQualityError) = ParseMinQuality(minQuality);
        if (minQualityError is not null)
            return BadRequest(new { error = "invalid_request", message = minQualityError });
        if (minQualityValue is { } mq && !Core.Search.VirtualCategories.Qualities.Contains(mq))
            return BadRequest(new { error = "invalid_request", message = "minQuality має бути 480 | 720 | 1080 | 2160" });

        var query = new SearchQuery(
            q, type, genreList, yearFrom, yearTo, country,
            minRating, maxRating, minRuntime, maxRuntime, status,
            sort ?? "relevance", page, pageSize,
            Actor: actor, Director: director, Studio: studio, Keyword: keyword,
            Language: language, MinVotes: minVotes,
            SeasonsFrom: seasonsFrom, SeasonsTo: seasonsTo,
            ExcludeGenres: excludeList,
            HasTrailer: hasTrailer, HasProviders: hasProviders,
            RatingSource: ratingSource, ProviderRegion: providerRegion ?? "UA",
            Watchable: watchable,
            Category: category, AudioLang: audioLangSafe, MinQuality: minQualityValue,
            OmitTotal: omitTotal);

        // TTL-кеш дефолтного каталогу (10.11): повторний GET /api/search того самого
        // «каталожного» вигляду (без q/actor/director/keyword) не перераховує пошук.
        // Персоналізовані/точкові пошуки НЕ кешуємо.
        var cacheKey = IsCacheable(q, actor, director, keyword) ? BuildCacheKey(query, langSafe) : null;
        if (cacheKey is not null && cache.TryGetValue(cacheKey, out object? cached) && cached is not null)
            // ct клієнта свідомо НЕ застосовується: кешований результат уже готовий,
            // його не можна зірвати скасуванням запиту.
            return Ok(cached);

        var result = await search.SearchAsync(query, ct);

        // FR-14: компактні бейджі «де подивитись» на картках (лише is_available=true)
        var watchLinks = await Infrastructure.Watchability.WatchLinkLoader.LoadAvailableAsync(
            db, result.Items.Select(t => t.Id), ct);

        object response = new
        {
            items = result.Items.Select(t =>
            {
                var card = TitleMapper.ToCard(t, langSafe);
                if (watchLinks.TryGetValue(t.Id, out var links))
                    card.WatchProviders = links.Select(w =>
                        new CardWatchProvider(w.Provider, w.Kind, w.Url)).ToList();
                return card;
            }).ToList(),
            total = result.Total,
            totalCapped = result.TotalCapped,
            page = result.Page,
            pageSize = result.PageSize,
        };

        // Кешуємо лише успішну відповідь: 4xx (валідація вище) і винятки пошуку
        // сюди не доходять — у кеші лише 200 OK.
        if (cacheKey is not null)
            cache.Set(cacheKey, response, DefaultCacheTtl);

        return Ok(response);
    }

    /// <summary>
    /// Кешувати можна лише «каталожні» видачі: q порожній/whitespace І actor/director/keyword
    /// порожні. Пошук за назвою/персоною/ключовим словом — точковий, не кешуємо.
    /// </summary>
    internal static bool IsCacheable(string? q, string? actor, string? director, string? keyword) =>
        string.IsNullOrWhiteSpace(q)
        && string.IsNullOrWhiteSpace(actor)
        && string.IsNullOrWhiteSpace(director)
        && string.IsNullOrWhiteSpace(keyword);

    /// <summary>
    /// Ключ TTL-кешу: нормалізований набір УСІХ параметрів, що впливають на результат.
    /// SearchQuery вже несе нормалізовані значення (жанри — канонічні en, sort — дефолт
    /// «relevance», audioLang — lowercase, providerRegion — дефолт «UA», omitTotal — bool),
    /// тож «відсутній параметр = дефолт» дає той самий ключ. «v2» — інвалідація при
    /// зміні форми відповіді/ключа.
    /// </summary>
    internal static string BuildCacheKey(SearchQuery q, string lang)
    {
        static string D(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        static string I(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        static string B(bool? v) => v?.ToString() ?? string.Empty;
        return string.Join("|",
            "search:v2",
            $"q={q.Q?.Trim() ?? string.Empty}",
            $"type={q.Type ?? string.Empty}",
            $"genres={string.Join(',', q.Genres)}",
            $"year={I(q.YearFrom)}-{I(q.YearTo)}",
            $"country={q.Country ?? string.Empty}",
            $"lang={lang}",
            $"rating={D(q.MinRating)}-{D(q.MaxRating)}",
            $"runtime={I(q.MinRuntime)}-{I(q.MaxRuntime)}",
            $"status={q.Status ?? string.Empty}",
            $"sort={q.Sort}",
            $"page={q.Page}",
            $"size={q.PageSize}",
            $"actor={q.Actor ?? string.Empty}",
            $"director={q.Director ?? string.Empty}",
            $"keyword={q.Keyword ?? string.Empty}",
            $"language={q.Language ?? string.Empty}",
            $"votes={I(q.MinVotes)}",
            $"seasons={I(q.SeasonsFrom)}-{I(q.SeasonsTo)}",
            $"exg={(q.ExcludeGenres is { Length: > 0 } ex ? string.Join(',', ex) : string.Empty)}",
            $"trailer={B(q.HasTrailer)}",
            $"providers={B(q.HasProviders)}",
            $"rsource={q.RatingSource ?? string.Empty}",
            $"region={q.ProviderRegion ?? string.Empty}",
            $"watchable={B(q.Watchable)}",
            $"category={q.Category ?? string.Empty}",
            $"audio={q.AudioLang ?? string.Empty}",
            $"quality={I(q.MinQuality)}",
            $"omit={q.OmitTotal}");
    }

    /// <summary>Жанри-фільтри: uk-назви нормалізуємо в канонічні en (як зберігаємо в БД).</summary>
    internal static string[]? ParseGenres(string? genres)
    {
        if (string.IsNullOrWhiteSpace(genres)) return [];
        var list = new List<string>();
        foreach (var raw in genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // канонічні жанри TMDB + жанри аніме/манги (school, mecha, slice of life...)
            var en = TmdbGenres.NormalizeToEn(raw) ?? Infrastructure.AnimeManga.AnimeGenres.NormalizeToEn(raw);
            if (en is null) return null;
            if (!list.Contains(en, StringComparer.OrdinalIgnoreCase)) list.Add(en);
        }
        return [.. list];
    }

    /// <summary>
    /// Дружній парсер minQuality (SearchQuery.MinQuality лишається int?): рядок біндиться
    /// [FromQuery] як string, тут розбирається. Акаунт-рівні: hd→720, fhd/fullhd→1080,
    /// uhd/4k→2160, sd→480; суфіксовані '480p'/'720p'/'1080p'/'2160p' → число; числове —
    /// як є (allowlist перевіряє контролер). Невідоме → (null, помилка з переліком допустимих).
    /// Кейс-незалежно; порожнє/null → відсутній фільтр.
    /// </summary>
    internal static (int? Value, string? Error) ParseMinQuality(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var s = raw.Trim().ToLowerInvariant();
        switch (s)
        {
            case "hd": return (720, null);
            case "fhd" or "fullhd": return (1080, null);
            case "uhd" or "4k": return (2160, null);
            case "sd": return (480, null);
            case "480p": return (480, null);
            case "720p": return (720, null);
            case "1080p": return (1080, null);
            case "2160p": return (2160, null);
        }
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return (n, null);
        return (null, "minQuality має бути 480 | 720 | 1080 | 2160 або hd | fhd | uhd");
    }

    /// <summary>
    /// Канонічна мова відповідей: en → en, ru → ru (рішення власника 10.11 — російська
    /// повноцінна мова API-відповідей), усе інше → uk. Кейс-незалежно.
    /// </summary>
    internal static string Lang(string? lang) =>
        string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? "en"
        : string.Equals(lang, "ru", StringComparison.OrdinalIgnoreCase) ? "ru"
        : "uk";
}
