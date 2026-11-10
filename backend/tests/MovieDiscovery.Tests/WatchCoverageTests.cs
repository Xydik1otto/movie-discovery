using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

/// <summary>
/// Watch-верифікація (чесний контракт покриття): ClassifyCoverage за формою URL,
/// верифікатор (duration/episode-маркери/oEmbed), оновлення coverage+verified_at,
/// ретраї 429 і порядок обходу (verified_at IS NULL першими).
/// Реальний Breaking Bad URL — рядок із prod-БД: watch_links (title 845164, youtube, free)
/// https://www.youtube.com/watch?v=8PnJurh7t_Y — форма watch?v= НЕ розрізняє фільм/епізод,
/// тому на рівні URL це чесний unknown (вирішує верифікатор по живих сигналах).
/// </summary>
public class WatchCoverageTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=8PnJurh7t_Y", "youtube", "series", WatchUrlClassifier.Unknown)] // реальний Breaking Bad S1E6 з БД
    [InlineData("https://www.youtube.com/watch?v=abc123XYZ_-&list=PLxyz", "youtube", "movie", WatchUrlClassifier.Unknown)] // плейліст — може бути і сезон, і збірка
    [InlineData("https://youtu.be/8PnJurh7t_Y", "youtube", "series", WatchUrlClassifier.Unknown)]
    [InlineData("https://www.imdb.com/video/vi123456789/", "imdb", "movie", WatchUrlClassifier.Episode)] // imdb.com/video/ → епізод/фрагмент
    [InlineData("https://www.imdb.com/title/tt0903747/", "imdb", "series", WatchUrlClassifier.Full)]     // сторінка тайтла IMDb = весь тайтл
    [InlineData("https://watch.plex.tv/movie/mission-to-mars-2000", "plex", "movie", WatchUrlClassifier.Full)]
    [InlineData("https://watch.plex.tv/show/breaking-bad", "plex", "series", WatchUrlClassifier.Full)]   // лендинг усього серіалу
    [InlineData("https://watch.plex.tv/show/breaking-bad/season/1/episode/6", "plex", "series", WatchUrlClassifier.Episode)]
    [InlineData("https://megogo.ua/ua/view/13024-sezon-ubivc.html", "megogo", "movie", WatchUrlClassifier.Full)] // реальна форма megogo з БД
    [InlineData("https://sweet.tv/ua-uk/movie/33488-misiia-nemozliva-finalna-rozplata", "sweettv", "movie", WatchUrlClassifier.Full)] // реальна форма sweet.tv з БД
    [InlineData("https://sweet.tv/ua-uk/serial/12345-shchodenniky", "sweettv", "series", WatchUrlClassifier.Full)]
    [InlineData("https://www.netflix.com/title/81087583", "netflix", "series", WatchUrlClassifier.Full)]
    [InlineData("https://www.netflix.com/watch/70178817", "netflix", "series", WatchUrlClassifier.Episode)] // плеєр серіалу = епізод
    [InlineData("https://www.netflix.com/watch/70178817", "netflix", "movie", WatchUrlClassifier.Full)]     // той самий URL для фільму = сам фільм
    [InlineData("https://archive.org/details/night_of_the_living_dead", "archive", "movie", WatchUrlClassifier.Full)] // item = сам фільм
    [InlineData("https://tv.apple.com/ua/movie/the-matrix/umc.cmc.af8k9kcq9r1s1qmmdxpq4itn", "Apple TV Store", "movie", WatchUrlClassifier.Full)]
    [InlineData("https://www.crunchyroll.com/watch/GY2P1Q98Y/ep1", "crunchyroll", "series", WatchUrlClassifier.Episode)]
    [InlineData("https://justwatch.com/ua/видео-онлайн", "JustWatch TV Beta", "movie", WatchUrlClassifier.Unknown)] // невідомий хост — чесний unknown
    [InlineData(null, "youtube", "movie", WatchUrlClassifier.Unknown)]
    [InlineData("", "plex", "movie", WatchUrlClassifier.Unknown)]
    [InlineData("not a url", "plex", "movie", WatchUrlClassifier.Unknown)]
    public void ClassifyCoverage_Url_Form_Heuristics(string? url, string? provider, string? mediaType, string expected) =>
        Assert.Equal(expected, WatchUrlClassifier.ClassifyCoverage(url, provider, mediaType));

    [Fact]
    public void ClassifyCoverage_Existing_Classify_Untouched() // UX-04 direct/find не зламано
    {
        Assert.Equal(WatchUrlClassifier.Direct, WatchUrlClassifier.Classify("https://www.youtube.com/watch?v=8PnJurh7t_Y"));
        Assert.Equal(WatchUrlClassifier.Direct, WatchUrlClassifier.Classify("https://watch.plex.tv/movie/mission-to-mars-2000"));
        Assert.Equal(WatchUrlClassifier.Find, WatchUrlClassifier.Classify("https://www.youtube.com"));
    }

    // ════════════════ Маркери епізоду/сезону + вердикт Decide (чисті сигнали верифікатора) ════════════════

    [Theory]
    [InlineData("Breaking Bad Season 1 Episode 6 «Crazy Handful of Nothin'»", true)]
    [InlineData("Breaking Bad - S01E06", true)]
    [InlineData("Breaking Bad s1 e6", true)]
    [InlineData("Пуститися берегів — Серія 12 (укр)", true)]
    [InlineData("12 серія", true)]
    [InlineData("Епізод 5", true)]
    [InlineData("Ep. 6", true)]
    [InlineData("The Matrix 1999 Full Movie", false)]
    [InlineData("Сезон убивць (2021) дивитися онлайн", false)] // фільм зі словом «сезон» у НАЗВІ — не маркер
    [InlineData("Топ 10 серій про помсту", false)]            // добірка без номера біля слова — не епізод
    [InlineData("", false)]
    public void Episode_Marker(string title, bool expected) =>
        Assert.Equal(expected, WatchCoverageHeuristics.HasEpisodeMarker(title));

    [Theory]
    [InlineData("Breaking Bad Season 1", true)]
    [InlineData("Сезон 1 повністю", true)]
    [InlineData("Crazy Handful of Nothin'", false)]
    public void Season_Marker(string title, bool expected) =>
        Assert.Equal(expected, WatchCoverageHeuristics.HasSeasonMarker(title));

    [Fact]
    public void Decide_Movie_Duration_95min_Is_Full()
    {
        var d = WatchCoverageHeuristics.Decide(WatchUrlClassifier.Unknown, "The Matrix", 95 * 60, "movie");
        Assert.Equal(WatchUrlClassifier.Full, d);
    }

    [Fact]
    public void Decide_Series_Duration_47min_Is_NOT_Full() // 45 хв серіалу = звичайний епізод, не фільм
    {
        var d = WatchCoverageHeuristics.Decide(WatchUrlClassifier.Unknown, "Crazy Handful of Nothin'", 47 * 60, "series");
        Assert.Equal(WatchUrlClassifier.Unknown, d);
    }

    [Fact]
    public void Decide_Youtube_Series_Season_Title_Is_Full_Season()
    {
        var d = WatchCoverageHeuristics.Decide(WatchUrlClassifier.Unknown, "Breaking Bad Season 1", null, "series", isYouTube: true);
        Assert.Equal(WatchUrlClassifier.Full, d);
    }

    [Fact]
    public void Decide_Uncertain_Stays_Unknown()
    {
        var d = WatchCoverageHeuristics.Decide(WatchUrlClassifier.Unknown, null, null, "movie");
        Assert.Equal(WatchUrlClassifier.Unknown, d);
    }

    [Fact]
    public void Decide_Definite_Url_Class_Wins_When_No_Marker()
    {
        Assert.Equal(WatchUrlClassifier.Full,
            WatchCoverageHeuristics.Decide(WatchUrlClassifier.Full, "The Matrix", null, "movie"));
        Assert.Equal(WatchUrlClassifier.Episode,
            WatchCoverageHeuristics.Decide(WatchUrlClassifier.Episode, "Trailer", 120, "movie"));
    }

    [Fact]
    public void Decide_Episode_Marker_Overrides_Url_Class()
    {
        Assert.Equal(WatchUrlClassifier.Episode,
            WatchCoverageHeuristics.Decide(WatchUrlClassifier.Full, "Сезон 1 Серія 6", null, "series"));
    }

    // ════════════════ Парсер голови сторінки: duration (lengthSeconds/approxDurationMs/ISO/og) і og:title/title ════════════════

    [Fact]
    public void Youtube_LengthSeconds_Parsed()
    {
        const string html = """<html><script>var x = {"videoId":"abc","lengthSeconds":"5700","channelId":"c"}</script></html>""";
        Assert.Equal(5700, WatchPageSignals.ExtractDurationSeconds(html));
    }

    [Fact]
    public void ApproxDurationMs_Parsed()
    {
        const string html = """{"approxDurationMs":"5700000"}""";
        Assert.Equal(5700, WatchPageSignals.ExtractDurationSeconds(html));
    }

    [Fact]
    public void Itemprop_Iso_Duration_Parsed() // 95 хв = PT1H35M
    {
        const string html = """<meta itemprop="duration" content="PT1H35M">""";
        Assert.Equal(95 * 60, WatchPageSignals.ExtractDurationSeconds(html));
    }

    [Fact]
    public void JsonLd_Duration_Parsed()
    {
        const string html = """<script type="application/ld+json">{"@type":"Movie","duration":"PT95M","name":"X"}</script>""";
        Assert.Equal(95 * 60, WatchPageSignals.ExtractDurationSeconds(html));
    }

    [Fact]
    public void Og_Video_Duration_Parsed()
    {
        const string html = """<meta property="og:video:duration" content="5700"/>""";
        Assert.Equal(5700, WatchPageSignals.ExtractDurationSeconds(html));
    }

    [Fact]
    public void No_Duration_Null()
    {
        Assert.Null(WatchPageSignals.ExtractDurationSeconds("<html><body>нема тривалості</body></html>"));
        Assert.Null(WatchPageSignals.ExtractDurationSeconds(""));
    }

    [Fact]
    public void Og_Title_First_Then_Title_Tag_Decoded()
    {
        const string withOg = """<html><head><meta property="og:title" content="Breaking Bad &#8212; Серія 6"/></head></html>""";
        Assert.Equal("Breaking Bad — Серія 6", WatchPageSignals.ExtractTitle(withOg));

        const string onlyTitle = "<html><head><title>Breaking Bad Season 1 Episode 6</title></head></html>";
        Assert.Equal("Breaking Bad Season 1 Episode 6", WatchPageSignals.ExtractTitle(onlyTitle));
    }

    [Fact]
    public void Reversed_Meta_Attributes_Parsed()
    {
        const string html = """<meta content="PT1H35M" itemprop="duration">""";
        Assert.Equal(95 * 60, WatchPageSignals.ExtractDurationSeconds(html));
    }

    [Fact]
    public void ParseIsoDuration_Formats()
    {
        Assert.Equal(5700, WatchPageSignals.ParseIsoDuration("PT1H35M"));
        Assert.Equal(95 * 60, WatchPageSignals.ParseIsoDuration("PT95M"));
        Assert.Equal(1810, WatchPageSignals.ParseIsoDuration("PT30M10S"));
        Assert.Null(WatchPageSignals.ParseIsoDuration("PT"));
        Assert.Null(WatchPageSignals.ParseIsoDuration("2 години"));
    }

    // ════════════════ Прогін верифікатора на InMemory-БД зі стабом HTTP (реальних викликів нема) ════════════════
    // (b) duration 95 хв → full; (c) episode-маркер → episode; (d) оновлення coverage+verified_at;
    // (e) ретрай 429; (f) verified_at IS NULL обходяться першими (навіть проти більш популярного
    // простроченого рядка); + 404 → is_available=false, coverage не чіпається.

    private const string WatchHtmlFull = """<html><head><title>The Matrix</title></head><body>{"lengthSeconds":"5700"}</body></html>""";
    private const string OembedFullMovie = """{"title":"The Matrix 1999 Full Movie 1080p","author_name":"Studio"}""";
    private const string EpisodeHtml = """<html><head><meta property="og:title" content="Breaking Bad Season 1 Episode 6"/></head></html>""";
    private const string PlainHtml = "<html><head><title>Місія нездійсненна</title></head></html>";

    private static MovieDiscoveryDbContext NewDb(string name) => TestDb.Create(name);

    private static WatchLinkVerificationService NewSvc(MovieDiscoveryDbContext db, HttpMessageHandler handler) =>
        new(db, new HttpClient(handler) { BaseAddress = new Uri("https://test.local/") },
            Logs.Null<WatchLinkVerificationService>(), throttle: TimeSpan.Zero);

    private static Title SeedTitle(MovieDiscoveryDbContext db, int id, string type, double popularity, string name)
    {
        var t = new Title { Id = id, Type = type, TitleEn = name, Popularity = popularity };
        db.Titles.Add(t);
        return t;
    }

    private static WatchLink SeedLink(MovieDiscoveryDbContext db, int titleId, string provider, string url,
        DateTime? verifiedAt = null, double popularity = 5, string type = "movie", string name = "T")
    {
        SeedTitle(db, titleId, type, popularity, name);
        var link = new WatchLink
        {
            TitleId = titleId, Provider = provider, Kind = "free", Region = "UA",
            Url = url, IsAvailable = true, VerifiedAt = verifiedAt,
        };
        db.WatchLinks.Add(link);
        return link;
    }

    private static Provider SeedProvider(MovieDiscoveryDbContext db, int titleId, string providerName, string url,
        DateTime? verifiedAt = null, double popularity = 5, string type = "movie", string name = "T")
    {
        SeedTitle(db, titleId, type, popularity, name);
        var row = new Provider
        {
            TitleId = titleId, ProviderName = providerName, Kind = "free", Source = "plex", Region = "UA",
            Url = url, VerifiedAt = verifiedAt,
        };
        db.Providers.Add(row);
        return row;
    }

    /// <summary>(b) 200 + duration 95 хв (фільм): YouTube lengthSeconds і generic itemprop PT1H35M → coverage=full.</summary>
    [Fact]
    public async Task Duration_95min_Movie_Becomes_Full()
    {
        var name = $"cov_full_{Guid.NewGuid():N}";
        var db = NewDb(name);
        SeedLink(db, 1, "youtube", "https://www.youtube.com/watch?v=MOVIE123", type: "movie");
        SeedLink(db, 2, "kinofabryka", "https://kinofabryka.example/watch/12345", type: "movie"); // невідомий хост — вирішує duration
        db.SaveChanges();

        var handler = new StubHttpHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("oembed")) return StubHttp.Json(OembedFullMovie);
            if (u.Contains("youtube.com/watch")) return StubHttp.Html(WatchHtmlFull);
            return StubHttp.Html("""<html><head><meta itemprop="duration" content="PT1H35M"></head></html>""");
        });
        var result = await NewSvc(db, handler).RunAsync(maxRows: 10);

        Assert.Equal(2, result.Checked);
        Assert.Equal(2, result.Full);
        var links = NewDb(name).WatchLinks.OrderBy(w => w.Id).ToList();
        Assert.All(links, l => Assert.Equal(WatchUrlClassifier.Full, l.Coverage));
        Assert.All(links, l => Assert.NotNull(l.VerifiedAt));
        Assert.All(links, l => Assert.True(l.IsAvailable));
    }

    /// <summary>(c) 200 + episode-маркер у og:title (невідомий хост, URL-клас unknown) → coverage=episode.</summary>
    [Fact]
    public async Task Episode_Marker_Becomes_Episode()
    {
        var name = $"cov_ep_{Guid.NewGuid():N}";
        var db = NewDb(name);
        var link = SeedLink(db, 7, "kinofabryka", "https://kino.example/serial/12345", type: "series");
        db.SaveChanges();

        var result = await NewSvc(db, new StubHttpHandler(_ => StubHttp.Html(EpisodeHtml))).RunAsync(maxRows: 10);

        Assert.Equal(1, result.Episode);
        db.ChangeTracker.Clear();
        var fresh = await NewDb(name).WatchLinks.SingleAsync(w => w.Id == link.Id);
        Assert.Equal(WatchUrlClassifier.Episode, fresh.Coverage);
        Assert.NotNull(fresh.VerifiedAt);
    }

    /// <summary>(d) джоба оновлює coverage+verified_at у БД для watch_links І providers (персистентність).</summary>
    [Fact]
    public async Task Job_Persists_Coverage_And_VerifiedAt()
    {
        var name = $"cov_db_{Guid.NewGuid():N}";
        var db = NewDb(name);
        SeedLink(db, 1, "youtube", "https://www.youtube.com/watch?v=MOVIE123", type: "movie");
        SeedLink(db, 2, "sweettv", "https://sweet.tv/ua-uk/movie/33488-misiia-nemozliva-finalna-rozplata", type: "movie");
        SeedProvider(db, 3, "Plex", "https://watch.plex.tv/movie/0000-a-shark-odyssey", type: "movie");
        db.SaveChanges();

        var handler = new StubHttpHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.Contains("oembed")) return StubHttp.Json(OembedFullMovie);
            if (u.Contains("youtube.com/watch")) return StubHttp.Html(WatchHtmlFull);
            return StubHttp.Html(PlainHtml); // sweet.tv і plex — URL-клас full, plain-сторінка без маркерів
        });
        var result = await NewSvc(db, handler).RunAsync(maxRows: 10);

        Assert.Equal(3, result.Checked);
        Assert.Equal(3, result.Full);
        Assert.Equal(0, result.Transient);

        var fresh = NewDb(name); // свіжий контекст — доводить персистентність
        var link1 = await fresh.WatchLinks.SingleAsync(w => w.TitleId == 1);
        var link2 = await fresh.WatchLinks.SingleAsync(w => w.TitleId == 2);
        var prov = await fresh.Providers.SingleAsync(p => p.TitleId == 3);
        Assert.Equal(WatchUrlClassifier.Full, link1.Coverage);
        Assert.Equal(WatchUrlClassifier.Full, link2.Coverage);
        Assert.Equal(WatchUrlClassifier.Full, prov.Coverage);
        Assert.NotNull(link1.VerifiedAt);
        Assert.NotNull(link2.VerifiedAt);
        Assert.NotNull(prov.VerifiedAt);
    }

    /// <summary>404/410: coverage НЕ стає episode — лишається як був (unknown), verified_at пишеться,
    /// прапорець недоступності — наявним IsAvailable-шляхом.</summary>
    [Fact]
    public async Task Gone_Link_Marks_Unavailable_Keeps_Coverage_Untouched()
    {
        var name = $"cov_404_{Guid.NewGuid():N}";
        var db = NewDb(name);
        var link = SeedLink(db, 1, "youtube", "https://www.youtube.com/watch?v=GONE123", type: "movie");
        var prov = SeedProvider(db, 2, "Plex", "https://watch.plex.tv/movie/dead-2001", type: "movie");
        db.SaveChanges();

        var result = await NewSvc(db, new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") })).RunAsync(maxRows: 10);

        Assert.Equal(2, result.Checked);
        Assert.Equal(2, result.Unavailable);
        var fresh = NewDb(name);
        var fl = await fresh.WatchLinks.SingleAsync(w => w.Id == link.Id);
        var fp = await fresh.Providers.SingleAsync(p => p.Id == prov.Id);
        Assert.Null(fl.Coverage);            // провал верифікації ≠ episode
        Assert.False(fl.IsAvailable);        // наявний IsAvailable-шлях
        Assert.NotNull(fl.VerifiedAt);
        Assert.Null(fp.Coverage);            // у providers — лише verified_at
        Assert.NotNull(fp.VerifiedAt);
    }

    /// <summary>(e) 429 → ретрай з Retry-After (≤3 с): 429, 429, 200 → успіх на 3-й спробі; вичерпання → transient без оновлень.</summary>
    [Fact]
    public async Task Retries_On_429_Then_Succeeds_And_Gives_Up_Transient()
    {
        var name = $"cov_429_{Guid.NewGuid():N}";
        var db = NewDb(name);
        var ok = SeedLink(db, 1, "sweettv", "https://sweet.tv/ua-uk/movie/33488-misiia-nemozliva-finalna-rozplata", type: "movie");
        var stuck = SeedLink(db, 2, "sweettv", "https://sweet.tv/ua-uk/movie/99999-stuck", type: "movie");
        db.SaveChanges();

        var okHandler = new CountingHandler(2, _ => StubHttp.Html(PlainHtml));
        var result = await NewSvc(db, okHandler).RunAsync(maxRows: 1);
        Assert.Equal(3, okHandler.Requests.Count); // 2×429 + 200
        Assert.Equal(1, result.Full);
        var fresh = NewDb(name);
        var fl = await fresh.WatchLinks.SingleAsync(w => w.Id == ok.Id);
        Assert.Equal(WatchUrlClassifier.Full, fl.Coverage);
        Assert.NotNull(fl.VerifiedAt);

        HttpResponseMessage Stuck429(HttpRequestMessage _) => new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(""),
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) },
        };
        var stuckHandler = new CountingHandler(int.MaxValue, Stuck429);
        var result2 = await NewSvc(db, stuckHandler).RunAsync(maxRows: 1);
        Assert.Equal(3, stuckHandler.Requests.Count); // вичерпано 3 спроби
        Assert.Equal(1, result2.Transient);
        var fresh2 = NewDb(name);
        var fs = await fresh2.WatchLinks.SingleAsync(w => w.Id == stuck.Id);
        Assert.Null(fs.Coverage);    // чесно не верифікували
        Assert.Null(fs.VerifiedAt);  // наступний прогін добере
    }

    /// <summary>(f) порядок обходу: verified_at IS NULL першими (навіть проти БІЛЬШ популярного
    /// простроченого рядка), всередині групи — популярність DESC; свіжі (10 днів) не чіпаються.</summary>
    [Fact]
    public async Task Never_Verified_Rows_First_Then_Stale_By_Popularity()
    {
        var name = $"cov_order_{Guid.NewGuid():N}";
        var db = NewDb(name);
        var staleHighPop = SeedLink(db, 1, "sweettv", "https://sweet.tv/ua-uk/movie/9001-a",
            verifiedAt: DateTime.UtcNow.AddDays(-40), popularity: 9);   // прострочений, найпопулярніший
        var neverMidPop = SeedLink(db, 2, "sweettv", "https://sweet.tv/ua-uk/movie/9002-b",
            verifiedAt: null, popularity: 5);
        var neverLowPop = SeedLink(db, 3, "sweettv", "https://sweet.tv/ua-uk/movie/9003-c",
            verifiedAt: null, popularity: 1);
        var episodeTop = SeedLink(db, 4, "sweettv", "https://sweet.tv/ua-uk/movie/9004-d",
            verifiedAt: null, popularity: 7, type: "series", name: "Breaking Bad");
        var freshRecent = SeedLink(db, 5, "sweettv", "https://sweet.tv/ua-uk/movie/9005-e",
            verifiedAt: DateTime.UtcNow.AddDays(-10), popularity: 100); // свіжа верифікація — поза вибіркою
        db.SaveChanges();

        // крок 1: maxRows=1 → верх вибірки = verified_at IS NULL з макс. популярністю (episode-HTML)
        var result1 = await NewSvc(db, new StubHttpHandler(_ => StubHttp.Html(EpisodeHtml))).RunAsync(maxRows: 1);
        Assert.Equal(1, result1.Checked);
        Assert.Equal(1, result1.Episode);
        var fresh = NewDb(name);
        Assert.Equal(WatchUrlClassifier.Episode, (await fresh.WatchLinks.SingleAsync(w => w.Id == episodeTop.Id)).Coverage);
        Assert.Null((await fresh.WatchLinks.SingleAsync(w => w.Id == staleHighPop.Id)).Coverage); // popularity 9 НЕ обійшов першим
        Assert.Null((await fresh.WatchLinks.SingleAsync(w => w.Id == neverMidPop.Id)).VerifiedAt);
        // свіжі (10 днів) — поза 30-денним вікном: verified_at НЕ перезаписаний
        Assert.Equal(freshRecent.VerifiedAt, (await fresh.WatchLinks.SingleAsync(w => w.Id == freshRecent.Id)).VerifiedAt);

        // крок 2: добір решти never-verified (pop 5, pop 1); прострочений і свіжий — не чіпались
        var result2 = await NewSvc(db, new StubHttpHandler(_ => StubHttp.Html(PlainHtml))).RunAsync(maxRows: 2);
        Assert.Equal(2, result2.Checked);
        var fresh2 = NewDb(name);
        Assert.Equal(WatchUrlClassifier.Full, (await fresh2.WatchLinks.SingleAsync(w => w.Id == neverMidPop.Id)).Coverage);
        Assert.Equal(WatchUrlClassifier.Full, (await fresh2.WatchLinks.SingleAsync(w => w.Id == neverLowPop.Id)).Coverage);
        Assert.Null((await fresh2.WatchLinks.SingleAsync(w => w.Id == staleHighPop.Id)).Coverage);
        Assert.Null((await fresh2.WatchLinks.SingleAsync(w => w.Id == freshRecent.Id)).Coverage);
    }

    /// <summary>Рядок БЕЗ URL: чесно позначаємо verified_at (нема чого перевіряти), coverage лишається null.</summary>
    [Fact]
    public async Task Null_Url_Row_Marked_Verified_Without_Coverage()
    {
        var name = $"cov_nourl_{Guid.NewGuid():N}";
        var db = NewDb(name);
        var link = SeedLink(db, 1, "sweettv", url: null!);
        db.SaveChanges();

        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("не має бути жодного HTTP-виклику"));
        var result = await NewSvc(db, handler).RunAsync(maxRows: 10);

        Assert.Equal(1, result.Checked);
        Assert.Equal(0, handler.Requests.Count);
        var fresh = NewDb(name);
        var fl = await fresh.WatchLinks.SingleAsync(w => w.Id == link.Id);
        Assert.Null(fl.Coverage);
        Assert.NotNull(fl.VerifiedAt);
        Assert.True(fl.IsAvailable); // доступність не чіпається — лінка нема, але це не «мертвий» URL
    }

    /// <summary>Стаб-хендлер: N перших відповідей → 429 (Retry-After 0), далі → fallback.</summary>
    private sealed class CountingHandler(int rateLimited, Func<HttpRequestMessage, HttpResponseMessage> fallback)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? "");
            if (Requests.Count <= rateLimited)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent(""),
                    Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) },
                });
            return Task.FromResult(fallback(request));
        }
    }
}
