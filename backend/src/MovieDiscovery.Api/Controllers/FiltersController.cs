using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Search;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// Метадата фільтрів за категорією (адаптивна панель фільтрів фронтових розділів):
/// GET /api/filters?category={movie|series|anime|manga|tvshow|cartoon|dorama|all} —
/// category відсутня/порожня = 'all' (Головна викликає панель без категорії),
/// невалідна НЕПОРОЖНЯ → 400. Повертає ЛИШЕ ті фільтри, які релевантні категорії (фронт рендерить панель без «мертвих»
/// блоків): жанри/роки/країни завжди, студії — anime/cartoon/manga, статуси — series/anime/tvshow,
/// сорти — статичний набір. Плюс counts — розбивка категорії по типах (total/movies/series/
/// shorts/animation_movies/animation_series).
///
/// Категорії — ТОЙ САМИЙ контракт, що й /api/search (VirtualCategories → SQL-умови в
/// SearchService ~L219): movie|series|anime|manga → type; tvshow → жанри reality/talk/game/variety;
/// cartoon → type IN (movie,series) І genre animation (аніме НЕ дублюється); dorama →
/// country IN (KR,CN,JP) І genre drama.
///
/// Стіл titles ДУЖЕ великий (2.86М): кожен агрегат рахується ПО КАТЕГОРІЙНО-відфільтрованому
/// сету (WHERE спочатку, unnest — всередині), фіксована кількість запитів, без N+1; 'all' —
/// один агрегат по всьому каталогу, без пагінації. Відповідь кешується в IMemoryCache (TTL 10 хв,
/// ключ з category) — повторні навігації панелі не перераховують агрегати.
/// </summary>
[ApiController]
[Route("api/filters")]
public sealed class FiltersController(MovieDiscoveryDbContext db, IMemoryCache cache) : ControllerBase
{
    /// <summary>TTL кешу метадати фільтрів: каталог синхронізується фоновими джобами.
    /// 60 хв (понад рефреш-інтервал FiltersWarmupService 45 хв) — фасети повільні
    /// (холодний агрегат десятки секунд), кеш тримається теплим рефрешером.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);

    internal const int MaxGenres = 40;
    internal const int MaxCountries = 20;
    internal const int MaxStudios = 40;

    /// <summary>Сорти панелі — статичний набір (порядок = пріоритет UI).</summary>
    internal static readonly string[] Sorts = ["popularity", "rating", "year", "title"];

    /// <summary>Дозволені категорії = віртуальні категорії пошуку (єдине джерело правди) + 'all'.</summary>
    internal static readonly string[] Categories = [.. VirtualCategories.All, "all"];

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? category, CancellationToken ct = default)
    {
        var cat = category?.Trim().ToLowerInvariant();
        // ВІДСУТНЯ/порожня category → 'all' (Головна викликає /api/filters без category —
        // це валідний запит, а не помилка); невалідна НЕПОРОЖНЯ → 400 з allowlist.
        if (string.IsNullOrWhiteSpace(cat))
            cat = "all";
        if (!Categories.Contains(cat))
            return BadRequest(new
            {
                error = "invalid_request",
                message = "category має бути movie | series | anime | manga | tvshow | cartoon | dorama | all",
            });

        // TTL-кеш: відповідь = ~8 агрегатів; повторний GET тієї самої категорії в межах
        // TTL не перераховує. Ключ містить нормалізовану category — категорії не перетинаються.
        // TTL 60 хв: холодний агрегат коштує десятки секунд ('all' ~53с замір 11.11), тож
        // TTL мусить перевищувати інтервал рефрешера FiltersWarmupService (45 хв) — кеш
        // утримується вічно теплим і ніколи не експайрить «холодним» для відвідувача.
        var cacheKey = CacheKey(cat);
        if (cache.TryGetValue(cacheKey, out FiltersResponse? cached) && cached is not null)
            // ct клієнта свідомо НЕ застосовується: кешований результат уже готовий
            // (його не можна зірвати скасуванням запиту) — як у SearchController.
            return Ok(cached);

        var response = await BuildAsync(cat, ct);

        // У кеші лише успішно зібрана відповідь: 4xx вище, винятки запитів сюди не доходять.
        cache.Set(cacheKey, response, CacheTtl);
        return Ok(response);
    }

    /// <summary>Єдиний формат ключа кешу (спільний з FiltersWarmupService).</summary>
    internal static string CacheKey(string category) => $"filters:v1:{category}";

    private Task<FiltersResponse> BuildAsync(string category, CancellationToken ct) =>
        BuildResponse(db, category, ct);

    /// <summary>Побудова відповіді фільтрів — internal static: реюз у FiltersWarmupService
    /// (прогрів/рефреш кешу поза HTTP-запитом).</summary>
    internal static async Task<FiltersResponse> BuildResponse(
        MovieDiscoveryDbContext db, string category, CancellationToken ct)
    {
        // База категорії: ВСІ агрегати будуються поверх цього Where — фільтр по категорії
        // застосовується ПЕРШИМ, масиви (genres/countries/studios) розгортаються вже
        // тільки в межах категорійного сету (не всього каталогу).
        var baseQ = db.Titles.AsNoTracking().Where(CategoryPredicate(category));

        // ── counts: один GROUP BY по типах (total/movies/series) ──
        var typeCounts = await baseQ
            .GroupBy(t => t.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var row in typeCounts)
        {
            byType[row.Type] = row.Count;
            total += row.Count;
        }

        // series-блок = серіали + шоу (type domain: movie|series|show|anime|manga;
        // 'show' — ТВ-шоу, анімаційні series/show рахуються і в animation_series нижче).
        var movies = byType.GetValueOrDefault("movie");
        var series = byType.GetValueOrDefault("series") + byType.GetValueOrDefault("show");

        // shorts: короткометражки IMDb (ImdbMapper: titleType 'short' → type 'movie'),
        // тобто шорт — це movie з RuntimeMin ≤ 40 (академічна межа). RuntimeMin NULL —
        // не short (семантика SQL: NULL <= 40 → не входить).
        var shorts = await baseQ
            .Where(t => t.Type == "movie" && t.RuntimeMin != null && t.RuntimeMin <= 40)
            .CountAsync(ct);

        // animation_movies: мультфільми (movie з жанром animation).
        var animationMovies = await baseQ
            .Where(t => t.Type == "movie" && t.Genres.Any(g => g == "animation"))
            .CountAsync(ct);

        // animation_series: анімаційні серіали ((series|show) І animation) АБО весь type='anime'
        // (аніме — анімація за визначенням, жанр animation у нього часто не прописаний).
        var animationSeries = await baseQ
            .Where(t => ((t.Type == "series" || t.Type == "show") && t.Genres.Any(g => g == "animation"))
                        || t.Type == "anime")
            .CountAsync(ct);

        // ── years: min/max по категорії (порожня категорія → фільтр років не віддається) ──
        var yearsRows = await baseQ
            .GroupBy(t => true)
            .Select(g => new { Min = g.Min(t => t.Year), Max = g.Max(t => t.Year) })
            .ToListAsync(ct);
        var years = yearsRows.Count > 0 ? new YearsFacet(yearsRows[0].Min, yearsRows[0].Max) : null;

        // ── фасети по масивах: unnest агрегат ПО КАТЕГОРІЇ (один запит на фасет) ──
        var genres = await FacetAsync(db, baseQ, category, t => t.Genres, "genres", MaxGenres, ct);

        // Країни: ефективна країна тайтла — title.country (akas, FR-19) з фолбеком на
        // countries[] (TMDB) — ті самі два джерела, що читає country-фільтр /api/search,
        // без подвійного обліку тайтла, в якого заповнені обидва.
        var countries = await FacetAsync(
            db, baseQ, category,
            t => t.Country != null ? new[] { t.Country! } : t.Countries,
            "countries", MaxCountries, ct);

        // Студії — з titles.studios (аніме-специфіка): лише anime/cartoon/manga.
        // Для cartoon/manga studios у БД здебільшого порожні — фасет віддається порожнім [].
        // Нерелевантні категорії → null: ключ studios НЕ серіалізується (адаптивна панель).
        List<FacetCount>? studios = NeedsStudios(category)
            ? await FacetAsync(db, baseQ, category, t => t.Studios, "studios", MaxStudios, ct)
            : null;

        // ── статуси: лише серіальний контент (type series|show|anime) ──
        // category series (type=series), anime (type=anime), tvshow (reality/talk/game/variety
        // → series|show). movie/cartoon/dorama/all — мікс із фільмами або суцільно фільми —
        // статусний фільтр там не релевантний (null → ключ відсутній у JSON).
        List<FacetCount>? statuses = null;
        if (NeedsStatuses(category))
        {
            var statusRows = await baseQ
                .Where(t => t.Status != null)
                .GroupBy(t => t.Status!)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count).ThenBy(x => x.Value)
                .ToListAsync(ct);
            statuses = [.. statusRows.Select(r => new FacetCount(r.Value, r.Count))];
        }

        return new FiltersResponse(
            Category: category,
            Filters: new FiltersPayload(
                Genres: genres,
                Years: years,
                Countries: countries,
                Studios: studios,
                Statuses: statuses,
                Sorts: Sorts),
            Counts: new CountsPayload(
                Total: total,
                Movies: movies,
                Series: series,
                Shorts: shorts,
                AnimationMovies: animationMovies,
                AnimationSeries: animationSeries));
    }

    /// <summary>Студійний фасет релевантний лише аніме-категоріям (titles.studios заповнює аніме-синк).</summary>
    private static bool NeedsStudios(string category) => category is "anime" or "cartoon" or "manga";

    /// <summary>Статусний фасет — лише категорії, чий сет цілком серіальний (type series|show|anime).</summary>
    private static bool NeedsStatuses(string category) => category is "series" or "anime" or "tvshow";

    /// <summary>
    /// Категорія → LINQ-предикат (counts/years/statuses — перекладається І Npgsql, І InMemory).
    /// Дзеркало трансляції категорій у SearchService (~L219) — ЄДИНИЙ контракт:
    /// movie|series|anime|manga → type; tvshow → OR-of-Any по GenreSets["tvshow"];
    /// cartoon → type IN (movie,series) І genre animation; dorama → country IN (KR,CN,JP) І genre drama;
    /// all → без умови.
    /// </summary>
    internal static Expression<Func<Title, bool>> CategoryPredicate(string category) => category switch
    {
        "movie" => t => t.Type == "movie",
        "series" => t => t.Type == "series",
        "anime" => t => t.Type == "anime",
        "manga" => t => t.Type == "manga",
        "tvshow" => GenresAny(VirtualCategories.GenreSets["tvshow"]),
        "cartoon" => t => (t.Type == "movie" || t.Type == "series")
                          && t.Genres.Any(g => g == "animation"),
        "dorama" => t => t.Country != null
                         && VirtualCategories.DoramaCountries.Contains(t.Country!)
                         && t.Genres.Any(g => g == VirtualCategories.DoramaGenre),
        _ => t => true, // all
    };

    /// <summary>
    /// OR-ланцюг Any(g == g_i) — та сама форма, яку перекладають І Npgsql
    /// (genres @> ARRAY[...] на одиночному жанрі / = ANY), І InMemory
    /// (мульти-OR усередині одного Any InMemory не перекладає — досвід SearchService).
    /// </summary>
    private static Expression<Func<Title, bool>> GenresAny(IReadOnlyList<string> genres)
    {
        var t = Expression.Parameter(typeof(Title), "t");
        Expression? body = null;
        foreach (var genre in genres)
        {
            var g = Expression.Parameter(typeof(string), "g");
            var any = Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Any), [typeof(string)],
                Expression.Property(t, nameof(Title.Genres)),
                Expression.Lambda<Func<string, bool>>(
                    Expression.Equal(g, Expression.Constant(genre, typeof(string))), g));
            body = body is null ? any : Expression.OrElse(body, any);
        }
        return Expression.Lambda<Func<Title, bool>>(body!, t);
    }

    /// <summary>
    /// Фасет по масивній колонці (genres/countries/studios) з ЛІМІТОМ по частоті.
    /// Npgsql: один unnest-агрегат у SQL (WHERE категорії всередині — обмежуємо unnest
    /// категорійним сетом); InMemory (WAF-тести): та сама семантика через LINQ (raw SQL
    /// InMemory-провайдер не підтримує). Колонка/category — валідовані константи коду
    /// (allowlist), user-input у SQL не потрапляє.
    /// </summary>
    private static async Task<List<FacetCount>> FacetAsync(
        MovieDiscoveryDbContext db,
        IQueryable<Title> baseQ,
        string category,
        Expression<Func<Title, string[]?>> arraySelector,
        string column,
        int limit,
        CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            var rows = await db.Database.SqlQueryRaw<FacetRow>(FacetSql(category, column, limit))
                .ToListAsync(ct);
            return [.. rows.Select(r => new FacetCount(r.Value, r.Count))];
        }

        var arrays = await baseQ.Select(arraySelector).ToListAsync(ct);
        return [.. arrays
            .SelectMany(a => a ?? Array.Empty<string>())
            .GroupBy(g => g, StringComparer.Ordinal)
            .Select(g => new FacetCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Value)
            .Take(limit)];
    }

    /// <summary>
    /// SQL unnest-агрегату фасету: WHERE категорії ВСЕРЕДИНІ (обмежуємо unnest
    /// категорійним сетом — стіл великий), GROUP BY + частотне сортування + LIMIT.
    /// Порядок сортування фасету: count DESC, потім value (детермінізм для кешу/тестів).
    /// Країни: ефективна країна тайтла — country (akas) з фолбеком на countries[] (TMDB),
    /// ті самі два джерела, що читає country-фільтр /api/search, без подвійного обліку.
    /// </summary>
    internal static string FacetSql(string category, string column, int limit)
    {
        var unnestExpr = column == "countries"
            ? "unnest(CASE WHEN t.country IS NOT NULL THEN ARRAY[t.country] ELSE t.countries END)"
            : $"unnest(t.{column})";
        return $"""
            SELECT g AS "Value", count(*)::int AS "Count"
            FROM titles AS t, LATERAL {unnestExpr} AS g
            WHERE {CategorySql(category)}
            GROUP BY g
            ORDER BY "Count" DESC, "Value"
            LIMIT {limit}
            """;
    }

    /// <summary>
    /// SQL-фрагмент WHERE категорії для unnest-агрегатів — дзеркало CategoryPredicate /
    /// SearchService-трансляції (єдиний контракт, константи з VirtualCategories).
    /// Викликається ЛИШЕ для валідованої allowlist-категорії → фрагменти статичні.
    /// </summary>
    internal static string CategorySql(string category) => category switch
    {
        "movie" => "t.type = 'movie'",
        "series" => "t.type = 'series'",
        "anime" => "t.type = 'anime'",
        "manga" => "t.type = 'manga'",
        // && = overlap (OR-семантика GenresAny)
        "tvshow" => $"t.genres && ARRAY[{SqlArray(VirtualCategories.GenreSets["tvshow"])}]",
        "cartoon" => $"t.type IN ('movie','series') AND t.genres @> ARRAY[{SqlArray(VirtualCategories.GenreSets["cartoon"])}]",
        "dorama" => $"t.country = ANY(ARRAY[{SqlArray(VirtualCategories.DoramaCountries)}]) AND t.genres @> ARRAY['{VirtualCategories.DoramaGenre}']",
        _ => "TRUE", // all
    };

    private static string SqlArray(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => $"'{v}'"));
}

// ── Відповідь: JSON-контракт контрактований по кнопці адаптивної панелі (snake_case ключі
// counts за специфікацією; ключі нерелевантних фільтрів ВІДСУТНІ, не []) ──

/// <summary>Фасетне значення з частотою всередині категорії.</summary>
internal sealed record FacetCount(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("count")] int Count);

/// <summary>Діапазон років категорії (null/відсутній — коли в категорії немає тайтлів).</summary>
internal sealed record YearsFacet(
    [property: JsonPropertyName("min")] int? Min,
    [property: JsonPropertyName("max")] int? Max);

internal sealed record FiltersPayload(
    [property: JsonPropertyName("genres")] List<FacetCount> Genres,
    // нерелевантні фільтри = null → ключ не серіалізується (JsonIgnoreCondition.WhenWritingNull)
    [property: JsonPropertyName("years"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] YearsFacet? Years,
    [property: JsonPropertyName("countries")] List<FacetCount> Countries,
    [property: JsonPropertyName("studios"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<FacetCount>? Studios,
    [property: JsonPropertyName("statuses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<FacetCount>? Statuses,
    [property: JsonPropertyName("sorts")] string[] Sorts);

internal sealed record CountsPayload(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("movies")] int Movies,
    [property: JsonPropertyName("series")] int Series,
    [property: JsonPropertyName("shorts")] int Shorts,
    [property: JsonPropertyName("animation_movies")] int AnimationMovies,
    [property: JsonPropertyName("animation_series")] int AnimationSeries);

internal sealed record FiltersResponse(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("filters")] FiltersPayload Filters,
    [property: JsonPropertyName("counts")] CountsPayload Counts);

/// <summary>Ціль матеріалізації raw-SQL unnest-агрегатів (Npgsql-шлях FacetAsync).</summary>
internal sealed class FacetRow
{
    public string Value { get; set; } = "";
    public int Count { get; set; }
}
