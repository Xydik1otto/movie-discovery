using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Search;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Infrastructure.Services;

public sealed record SearchQuery(
    string? Q,
    string? Type,
    string[] Genres,
    int? YearFrom,
    int? YearTo,
    string? Country,
    double? MinRating,
    double? MaxRating,
    int? MinRuntime,
    int? MaxRuntime,
    string? Status,
    string Sort,
    int Page,
    int PageSize,
    // ── Розширені фільтри ──
    string? Actor = null,
    string? Director = null,
    string? Studio = null,
    string? Keyword = null,
    string? Language = null,
    int? MinVotes = null,
    int? SeasonsFrom = null,
    int? SeasonsTo = null,
    string[]? ExcludeGenres = null,
    bool? HasTrailer = null,
    bool? HasProviders = null,
    string? RatingSource = null,
    string ProviderRegion = "UA",
    bool? Watchable = null,
    // ── Віртуальні категорії та озвучки (FR-19) ──
    string? Category = null,
    string? AudioLang = null,
    int? MinQuality = null,
    // UX-01: швидкі перегортання (page>1) — клієнт тримає total у кеші з першої
    // сторінки і просить пропустити дорогий COUNT; сервер повертає total = -1
    // («невідомо»), totalCapped = false. Опційний: дефолтна поведінка без змін.
    bool OmitTotal = false);

public sealed record SearchResult(IReadOnlyList<Title> Items, int Total, int Page, int PageSize, bool TotalCapped = false);

/// <summary>
/// Пошук/фільтри ЛИШЕ по локальній БД (NFR-1: жодних зовнішніх викликів у hot path).
/// q шукає в назвах uk/en, акторах і режисері (ILIKE). Окремі фільтри: actor/director/
/// keyword/language/minVotes/seasons/excludeGenres/hasTrailer/hasProviders/ratingSource.
/// </summary>
public sealed class SearchService(MovieDiscoveryDbContext db)
{
    public async Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct = default)
    {
        var page = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 50);

        var query = BuildQuery(q);
        // Ліміт точного підрахунку ДЛЯ ТЕКСТОВОГО ПОШУКУ: частотний підрядок
        // ('x' = 104k збігів) змушує планер до seq-scan по 2.86 млн рядків
        // (замір prod: 14.2 с тільки на count — 30 с CommandTimeoutKill при
        // навантаженні). Рахуємо максимум CountCap+1 — клієнт показує «2000+»;
        // точне число лишається для фільтрів без q (їхні count-и по GIN дешеві).
        const int CountCap = 2000;
        var capped = !string.IsNullOrWhiteSpace(q.Q);
        // Параметризовані LIMIT/ILIKE + generic-план PG = bitmap+sort без ранньої
        // термінації ('а' 12–14 с). Лікується на рівні БД: ALTER ROLE movie SET
        // plan_cache_mode = force_custom_plan (custom-план бачить реальні значення;
        // планування ~6 мс проти секунд виконання). EF.Constant тут НЕ варіант —
        // як аргумент Take(int) він викликається eagerly поза expression tree.
        // UX-01: omitTotal — клієнт гортає сторінки, маючи total у кеші з page=1;
        // повторний COUNT на кожному перегортанні пропускаємо (total = -1).
        int total;
        if (q.OmitTotal)
        {
            total = -1;
        }
        else
        {
            total = capped
                ? await query.Take(CountCap + 1).CountAsync(ct)
                : await query.CountAsync(ct);
        }

        // UX-01: клип глибини OFFSET. Сторінка не може перевищити maxPage за
        // total — застарілий bookmark/брутфорс ?page=999999 більше не змушує PG
        // проганяти сотні тисяч рядків індексу заради порожньої відповіді.
        // Коли total невідомий (omitTotal) — жорсткий ліміт сторінок (синхронний
        // із клієнтським usePagedSearch.MAX_PAGES).
        var maxPage = total >= 0
            ? Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))
            : MaxPages;
        if (page > maxPage)
            page = maxPage;

        query = Sort(query, q.Sort);

        // Перф-хвиля (11.11): чисте читання — трекінг EF не потрібен (сущності лише
        // мапляться в TitleMapper, не модифікуються); економить ChangeTracker на
        // кожній сторінці каталогу (до 50 entities за запит).
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(ct);

        return new SearchResult(items, total, page, pageSize, capped && total > CountCap);
    }

    // Жорстка стеля глибини пагінації: 5000 стор. × 50 = OFFSET ≤ 250k —
    // indexing-walk у межах мілісекунд навіть без лічильника.
    internal const int MaxPages = 5000;

    /// <summary>Побудова запиту з усіма фільтрами (public — тест трансляції без БД, як NpgsqlTranslationTests).</summary>
    public IQueryable<Title> BuildQuery(SearchQuery q)
    {
        IQueryable<Title> query;
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            var pat = "%" + EscapeLike(q.Q.Trim()) + "%";
            // COMPOSABLE LINQ, не FromSqlInterpolated: FromSql + OrderBy/Skip/Take
            // генерує SELECT * FROM (raw без LIMIT) AS m — внутрішній підзапит
            // materializує УСІ збіги (104k рядків з TOAST-масивами) перед сортуванням
            // → 15–30 с на частотних підрядках. Один рівень дає планеру ранню
            // термінацію по ix_titles_popularity (замір: 2.9 мс проти 15.5 с).
            query = db.Titles.Where(t =>
                (t.TitleUk != null && EF.Functions.ILike(t.TitleUk, pat)) ||
                (t.TitleRu != null && EF.Functions.ILike(t.TitleRu, pat)) ||
                EF.Functions.ILike(t.TitleEn, pat) ||
                (t.Director != null && EF.Functions.ILike(t.Director, pat)) ||
                EF.Functions.ILike(MovieDbFunctions.ActorsTrgmText(t.Actors)!, pat));
        }
        else
        {
            query = db.Titles;
        }

        if (!string.IsNullOrWhiteSpace(q.Type))
            query = query.Where(t => t.Type == q.Type);

        if (q.Genres is { Length: > 0 })
            query = query.Where(t => t.Genres.Any(g => q.Genres.Contains(g)));

        if (q.ExcludeGenres is { Length: > 0 })
            query = query.Where(t => !t.Genres.Any(g => q.ExcludeGenres.Contains(g)));

        if (q.YearFrom is { } yFrom)
            query = query.Where(t => t.Year >= yFrom);
        if (q.YearTo is { } yTo)
            query = query.Where(t => t.Year <= yTo);

        query = ApplyRatingFilter(query, q);

        query = ApplyRuntimeFilter(query, q);

        if (!string.IsNullOrWhiteSpace(q.Status))
            query = query.Where(t => t.Status == q.Status);

        // ── Розширені фільтри ──

        if (!string.IsNullOrWhiteSpace(q.Actor))
        {
            var pat = "%" + EscapeLike(q.Actor.Trim()) + "%";
            query = query.Where(t => t.Actors.Any(a => EF.Functions.ILike(a, pat)));
        }

        if (!string.IsNullOrWhiteSpace(q.Director))
        {
            var pat = "%" + EscapeLike(q.Director.Trim()) + "%";
            query = query.Where(t => t.Director != null && EF.Functions.ILike(t.Director, pat));
        }
        if (!string.IsNullOrWhiteSpace(q.Studio))
        {
            // Студії анімації (FR: плаваючі фільтри аніме) — точний матч по масиву studios.
            var studio = q.Studio.Trim();
            query = query.Where(t => t.Studios.Contains(studio));
        }

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var pat = "%" + EscapeLike(q.Keyword.Trim()) + "%";
            query = query.Where(t => t.Keywords.Any(k => EF.Functions.ILike(k, pat)));
        }

        if (!string.IsNullOrWhiteSpace(q.Language))
            query = query.Where(t => t.OriginalLanguage == q.Language.ToLowerInvariant());

        if (q.MinVotes is { } minV)
            query = query.Where(t => t.VotesImdb >= minV);

        if (q.SeasonsFrom is { } sFrom)
            query = query.Where(t => t.Seasons >= sFrom);
        if (q.SeasonsTo is { } sTo)
            query = query.Where(t => t.Seasons <= sTo);

        if (q.HasTrailer == true)
            query = query.Where(t =>
                // IV-06 (координація tr-2 ↔ watch-2): верифікований трейлер у НОВІЙ таблиці
                // title_trailers (UX-05, verified_status='ok') ТАКОЖ проходить hasTrailer=true,
                // не лише швидкий шлях titles.trailer_yt_id. Повний предикат:
                // EXISTS(title_trailers ok) OR trailer_yt_id IS NOT NULL.
                db.TitleTrailers.Any(tr => tr.TitleId == t.Id && tr.VerifiedStatus == "ok")
                || (t.TrailerYtId != null && t.TrailerYtId != ""));

        if (q.HasProviders == true)
        {
            var region = string.IsNullOrWhiteSpace(q.ProviderRegion) ? "UA" : q.ProviderRegion.Trim().ToUpperInvariant();
            query = query.Where(t => db.Providers.Any(p => p.TitleId == t.Id && p.Region == region));
        }

        // FR-14: «лише те, що можна подивитись» — EXISTS watch_links is_available
        if (q.Watchable == true)
            query = query.Where(t => db.WatchLinks.Any(w => w.TitleId == t.Id && w.IsAvailable));

        // ── FR-19: віртуальні категорії, країна-виробник, озвучки, якість ──

        // country: кома-список кодів — збіг з будь-яким; title.country (IMDb akas, FR-19)
        // або countries[] (TMDB) — працює до і після akas-синку
        if (!string.IsNullOrWhiteSpace(q.Country))
        {
            var countryCodes = q.Country.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (countryCodes.Length > 0)
                query = query.Where(CountryAnyPredicate(countryCodes));
        }

        // category: movie|series|anime|manga → type; tvshow/cartoon/dorama → SQL-умови
        // по genres/country (GENRE_SETS у Core — контрактна константа)
        if (!string.IsNullOrWhiteSpace(q.Category))
        {
            var category = q.Category.Trim().ToLowerInvariant();
            switch (category)
            {
                case "movie" or "series" or "anime" or "manga":
                    query = query.Where(t => t.Type == category);
                    break;
                case "tvshow":
                    // genres && ARRAY['reality-tv','talk-show','game-show','variety']
                    query = query.Where(GenresAnyPredicate(VirtualCategories.GenreSets["tvshow"]));
                    break;
                case "cartoon":
                    // genres @> ARRAY['animation'] AND type IN ('movie','series'); аніме НЕ дублюється
                    query = query.Where(t =>
                        (t.Type == "movie" || t.Type == "series"))
                        .Where(GenresAnyPredicate(VirtualCategories.GenreSets["cartoon"]));
                    break;
                case "dorama":
                    // country IN ('KR','CN','JP') AND genres @> ARRAY['drama'] (both type)
                    var doramaCountries = VirtualCategories.DoramaCountries;
                    query = query.Where(t =>
                            t.Country != null &&
                            doramaCountries.Contains(t.Country))
                        .Where(GenresAnyPredicate([VirtualCategories.DoramaGenre]));
                    break;
            }
        }

        // audioLang: EXISTS watch_links (is_available) + IV-07: ЛЮБЕ з двох мовних полів —
        // легасі одиночне audio_lang (ukr|russ|eng|original|…) АБО нові набори audio_langs
        // (csv ISO-639-1 «en,ru», JustWatch/sweet.tv/Megogo). Точний ТОКЕН-матчинг по csv
        // (Split на ',' — substring false-positives неможливі: 'rus' не матчить 'russian',
        // 'ru' не матчить 'belarusian'); синоніми через мапу (ukr≡uk, russ≡ru, eng≡en).
        // СУБТИТРИ НЕ рахуються як аудіо (sub_langs у предикаті відсутній — свідомо).
        if (!string.IsNullOrWhiteSpace(q.AudioLang))
        {
            // синоніми до SQL-рівня: "ukr" → {"ukr","uk"}, "uk" → {"uk","ukr"} тощо
            var wanted = AudioLangVariants(q.AudioLang).ToList();
            query = query.Where(AudioLinkExists(db.WatchLinks, wanted));
        }

        // minQuality: EXISTS watch_links (is_available, quality >= X)
        if (q.MinQuality is { } minQuality)
            query = query.Where(t => db.WatchLinks
                .Any(w => w.TitleId == t.Id && w.IsAvailable && w.Quality >= minQuality));

        return query;
    }

    /// <summary>
    /// IV-07: синоніми фільтра audioLang → набір ТОЧНИХ токенів для предиката:
    /// «ukr» матчить і легасі audio_lang='ukr', і нові audio_langs='uk' (csv ISO);
    /// «uk» матчить обидві форми; невідомий токен проходить як є (чесний no-match,
    /// не фолбек uk/ru). Синонімічні пари з WatchLanguages (єдине джерело мапи).
    /// </summary>
    internal static IReadOnlySet<string> AudioLangVariants(string? raw)
    {
        var canonical = MovieDiscovery.Infrastructure.Watchability.WatchLanguages.Normalize(raw);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (raw is not null)
        {
            var t = raw.Trim().ToLowerInvariant();
            if (t.Length > 0) set.Add(t);
        }
        if (canonical is not null) set.Add(canonical);
        // зворотний бік: canonical ISO → легасі-форма (щоб audioLang=uk знайшов audio_lang='ukr')
        var legacy = canonical switch
        {
            "uk" => "ukr",
            "ru" => "russ",
            "en" => "eng",
            "ja" => "jpn",
            "ko" => "kor",
            "zh" => "zho",
            "de" => "ger",
            "fr" => "fre",
            "es" => "spa",
            "it" => "ita",
            "pl" => "pol",
            _ => null,
        };
        if (legacy is not null) set.Add(legacy);
        return set;
    }

    /// <summary>
    /// IV-07: EXISTS-предикат «у тайтла є доступний watch_link з аудіо однією з бажаних мов».
    /// Легасі: точний токен у audio_lang (IN — транслюється). Нові набори: ТОЧНИЙ токен серед
    /// csv-токенів audio_langs через ('=' + csv + '=') LIKE '=uk,=' на кожен токен — OR-ланцюг
    /// будується ДЕРЕВОМ ВИРАЗІВ, бо Split/Any у виразному дереві не транслиється ані InMemory,
    /// ані Npgsql (substring false-positives неможливі: паддинг замикає токени комами,
    /// 'ru' не матчить 'belarusian'). Субтитри НЕ рахуються як аудіо (sub_langs відсутній).
    /// </summary>
    internal static Expression<Func<Title, bool>> AudioLinkExists(DbSet<WatchLink> links, IReadOnlyList<string> wanted)
    {
        var t = Expression.Parameter(typeof(Title), "t");
        var w = Expression.Parameter(typeof(WatchLink), "w");

        // DbSet у дереві — тільки через closure-член (Expression.Constant(DbSet) ламає
        // і InMemory-транслятор, і ExpressionPrinter Npgsql): захоплена змінна → MemberExpression.
        DbSet<WatchLink> linksCaptured = links;
        var linksExpr = (MemberExpression)((Expression<Func<DbSet<WatchLink>>>)(() => linksCaptured)).Body;

        var titleMatch = Expression.Equal(
            Expression.Property(w, nameof(WatchLink.TitleId)), Expression.Property(t, nameof(Title.Id)));
        var available = Expression.Equal(
            Expression.Property(w, nameof(WatchLink.IsAvailable)), Expression.Constant(true, typeof(bool)));

        // легасі-поле: w.AudioLang != null && wanted.Contains(w.AudioLang) → SQL IN
        var audioLang = Expression.Property(w, nameof(WatchLink.AudioLang));
        var wantedConst = Expression.Constant(wanted, typeof(IReadOnlyList<string>));
        var legacyIn = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Contains), new[] { typeof(string) }, wantedConst, audioLang);
        var legacy = Expression.AndAlso(
            Expression.NotEqual(audioLang, Expression.Constant(null, typeof(string))), legacyIn);

        // csv-набори: падднений csv (',' + audio_langs + ',') має містити ',tok,' хоча б для одного токена
        var langs = Expression.Property(w, nameof(WatchLink.AudioLangs));
        var concat3 = typeof(string).GetMethod(nameof(string.Concat),
            new[] { typeof(string), typeof(string), typeof(string) })!;
        var padded = Expression.Call(concat3, Expression.Constant(","), langs, Expression.Constant(","));
        Expression? csvOr = null;
        foreach (var raw in wanted)
        {
            var tok = "," + raw.Trim().ToLowerInvariant() + ",";
            var contains = Expression.Call(padded, nameof(string.Contains), Type.EmptyTypes, Expression.Constant(tok));
            var guarded = Expression.AndAlso(
                Expression.NotEqual(langs, Expression.Constant(null, typeof(string))), contains);
            csvOr = csvOr is null ? guarded : Expression.OrElse(csvOr, guarded);
        }

        var linkOk = Expression.AndAlso(titleMatch, Expression.AndAlso(available, Expression.OrElse(legacy, csvOr!)));
        var anyLambda = Expression.Lambda<Func<WatchLink, bool>>(linkOk, w);
        var anyCall = Expression.Call(
            typeof(Queryable), nameof(Queryable.Any), new[] { typeof(WatchLink) },
            linksExpr, Expression.Quote(anyLambda));
        return Expression.Lambda<Func<Title, bool>>(anyCall, t);
    }

    /// <summary>Рейтинг: ratingSource обирає, до якого джерела застосувати мінімум/максимум.</summary>
    private static IQueryable<Title> ApplyRatingFilter(IQueryable<Title> query, SearchQuery q)
    {
        if (q.MinRating is null && q.MaxRating is null) return query;

        if (q.MinRating is { } minR)
            query = q.RatingSource switch
            {
                "tmdb" => query.Where(t => t.RatingTmdb >= minR),
                "imdb" => query.Where(t => t.RatingImdb >= minR),
                _ => query.Where(t => (t.RatingTmdb ?? t.RatingImdb) >= minR),
            };
        if (q.MaxRating is { } maxR)
            query = q.RatingSource switch
            {
                "tmdb" => query.Where(t => t.RatingTmdb <= maxR),
                "imdb" => query.Where(t => t.RatingImdb <= maxR),
                _ => query.Where(t => (t.RatingTmdb ?? t.RatingImdb) <= maxR),
            };
        return query;
    }

    /// <summary>Тривалість: фільм → runtime_min; серіал → episode_runtime_min; без типу — будь-яке з двох.</summary>
    private static IQueryable<Title> ApplyRuntimeFilter(IQueryable<Title> query, SearchQuery q)
    {
        var min = q.MinRuntime;
        var max = q.MaxRuntime;
        if (min is null && max is null) return query;

        return q.Type switch
        {
            "movie" => query.Where(t =>
                (min == null || t.RuntimeMin >= min) && (max == null || t.RuntimeMin <= max)),
            "series" or "show" => query.Where(t =>
                (min == null || t.EpisodeRuntimeMin >= min) && (max == null || t.EpisodeRuntimeMin <= max)),
            _ => query.Where(t =>
                ((min == null || t.RuntimeMin >= min) && (max == null || t.RuntimeMin <= max)) ||
                ((min == null || t.EpisodeRuntimeMin >= min) && (max == null || t.EpisodeRuntimeMin <= max))),
        };
    }

    /// <summary>
    /// country: title.country == X АБО countries[] містить X — для кожного коду (OR-ланцюг).
    /// Явні equality/Contains-вирази: перекладаються і Npgsql (country = X / countries @> ...),
    /// і тестовим InMemory (parameter-list.Contains всередині Any над масивною колонкою він не перекладає).
    /// </summary>
    internal static Expression<Func<Title, bool>> CountryAnyPredicate(IReadOnlyList<string> codes)
    {
        var t = Expression.Parameter(typeof(Title), "t");
        Expression? body = null;
        foreach (var code in codes)
        {
            var eqCountry = Expression.Equal(
                Expression.Property(t, nameof(Title.Country)),
                Expression.Constant(code, typeof(string)));
            var inArray = Expression.Call(
                typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)],
                Expression.Property(t, nameof(Title.Countries)),
                Expression.Constant(code, typeof(string)));
            var perCode = Expression.OrElse(eqCountry, inArray);
            body = body is null ? perCode : Expression.OrElse(body, perCode);
        }
        return Expression.Lambda<Func<Title, bool>>(body!, t);
    }

    /// <summary>
    /// Жанрова overlap-умова: OR-ланцюг Any(g == g_i) — семантика && (перетин), побудована
    /// з GENRE_SETS-константи. Форма OR-of-single-Any — єдина, яку перекладають І Npgsql
    /// (EXISTS unnest), І тестовий InMemory (мульти-OR усередині одного Any InMemory не їсть).
    /// </summary>
    internal static Expression<Func<Title, bool>> GenresAnyPredicate(IReadOnlyList<string> genres)
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

    // UX-01: детермінований порядок — tie-breaker ThenBy(Id) в УСІ гілках.
    // Без нього рівні ключі (popularity=0 у маси тайтлів, рейтинг null→0) розкладаються
    // по-різному між сусідніми OFFSET-сторінками → дублі/пропуски при перегортанні.
    private static IQueryable<Title> Sort(IQueryable<Title> query, string sort) => sort switch
    {
        "rating" => query.OrderByDescending(t => t.RatingTmdb ?? t.RatingImdb ?? 0)
            .ThenByDescending(t => t.Popularity)
            .ThenBy(t => t.Id),
        "imdb" => query.OrderByDescending(t => t.RatingImdb ?? 0)
            .ThenByDescending(t => t.VotesImdb ?? 0)
            .ThenBy(t => t.Id),
        "votes" => query.OrderByDescending(t => t.VotesImdb ?? 0)
            .ThenByDescending(t => t.RatingImdb ?? 0)
            .ThenBy(t => t.Id),
        "year" => query.OrderByDescending(t => t.Year ?? 0)
            .ThenByDescending(t => t.Popularity)
            .ThenBy(t => t.Id),
        "popularity" => query.OrderByDescending(t => t.Popularity).ThenBy(t => t.Id),
        // relevance: локальна релевантність — популярність як проксі (повнотекстовий рейтинг — Е2)
        _ => query.OrderByDescending(t => t.Popularity)
            .ThenByDescending(t => t.Year ?? 0)
            .ThenBy(t => t.Id),
    };

    public static string EscapeLike(string s) => s
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
