using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Watch;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

/// <summary>
/// UX-04 (хвиля 2): нормалізація мов — свідома чесність: невідома мова ЗАВЖДИ null,
/// ніколи не uk/ru за замовчуванням; синоніми всіх джерел (JW ISO, sweet-слаги,
/// Megogo-лейбли, легасі-коди watch_links) зводяться до ISO-639-1.
/// </summary>
public class WatchLanguagesTests
{
    [Theory]
    [InlineData("ukrajinska", "uk")]     // sweet.tv-слаг
    [InlineData("ukr", "uk")]            // легасі watch_links
    [InlineData("uk", "uk")]             // JW ISO
    [InlineData("UA", "uk")]             // регістр
    [InlineData("Українська", "uk")]     // Megogo-лейбл
    [InlineData("украинская", "uk")]     // ru-лейбл
    [InlineData("russkiy", "ru")]        // sweet.tv-слаг
    [InlineData("russ", "ru")]           // легасі
    [InlineData("ru", "ru")]
    [InlineData("Російська", "ru")]      // Megogo-лейбл
    [InlineData("русская", "ru")]
    [InlineData("english", "en")]
    [InlineData("eng", "en")]
    [InlineData("Англійська", "en")]
    [InlineData("Английский", "en")]
    [InlineData("polska", "pl")]
    [InlineData("Польська", "pl")]
    [InlineData("pl", "pl")]
    [InlineData("original", "original")]
    [InlineData("multi", "multi")]
    public void Normalize_Known_Tokens(string raw, string expected) =>
        Assert.Equal(expected, WatchLanguages.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("zhongwen")]   // невідомий слаг — НЕ вигадуємо
    [InlineData("klingon")]    // невідома мова
    [InlineData("xx")]         // невідомий 2-літерний код — чесний null
    public void Normalize_Unknown_Is_Null(string? raw) => Assert.Null(WatchLanguages.Normalize(raw));

    [Fact]
    public void NormalizeSet_Sorts_Dedups_And_Null_When_All_Unknown()
    {
        Assert.Equal("en,ru,uk", WatchLanguages.NormalizeSet(["ru", "uk", "en", "uk", "ukrajinska"]));
        Assert.Null(WatchLanguages.NormalizeSet(["klingon", "zhongwen"])); // нічого відомого → NULL
        Assert.Null(WatchLanguages.NormalizeSet(null));
        Assert.Null(WatchLanguages.NormalizeSet([]));
    }

    [Fact]
    public void Union_Merges_And_Never_Fabricates()
    {
        Assert.Equal("en,ru", WatchLanguages.Union("en", "ru"));
        Assert.Equal("en,ru", WatchLanguages.Union("en,ru", null));
        Assert.Null(WatchLanguages.Union(null, null));
        Assert.Equal("en,ru,uk", WatchLanguages.Union("en", "ru,uk"));
    }

    [Fact]
    public void GroupOf_Priority_Uk_Then_Ru_Then_Original_Then_Unknown()
    {
        Assert.Equal(WatchLanguages.GroupUk, WatchLanguages.GroupOf("uk,ru", null, null));
        Assert.Equal(WatchLanguages.GroupUk, WatchLanguages.GroupOf("en", "uk", null)); // суб uk → uk-група
        Assert.Equal(WatchLanguages.GroupRu, WatchLanguages.GroupOf("en,ru", "en", null));
        Assert.Equal(WatchLanguages.GroupOriginal, WatchLanguages.GroupOf("en", null, null)); // конкретна не-uk/ru
        Assert.Equal(WatchLanguages.GroupOriginal, WatchLanguages.GroupOf(null, null, "original"));
        Assert.Equal(WatchLanguages.GroupUnknown, WatchLanguages.GroupOf(null, null, null)); // невідома
        Assert.Equal(WatchLanguages.GroupUnknown, WatchLanguages.GroupOf(null, null, "ukr-dub-невідомо"));
    }

    [Fact]
    public void Signature_Null_Is_Empty()
    {
        Assert.Equal("", WatchLanguages.Signature(null));
        Assert.Equal("en,ru", WatchLanguages.Signature("en,ru"));
    }
}

/// <summary>
/// UX-04: класифікатор URL — direct (ID/UUID/слаг тайтла в URL) vs find («Знайти на сервісу»).
/// Кейси з ТЗ: netflix/title/\d+, umc.cmc.*, UUID megogo, /movie/{slug}, HBO епізодні лінки;
/// homepage/пошук/колекції/null — find; url:null → НЕ direct.
/// </summary>
public class WatchUrlClassifierTests
{
    [Theory]
    [InlineData("https://www.netflix.com/title/81087583", "direct")]           // netflix/title/\d+
    [InlineData("https://tv.apple.com/ua/movie/the-matrix/umc.cmc.af8k9kcq9r1s1qmmdxpq4itn", "direct")] // umc.cmc.*
    [InlineData("https://megogo.ua/ua/view/1003951-Dictator.html", "direct")]  // megogo числовий id
    [InlineData("https://www.rakuten.tv/ua/movies/2049c094-71ba-457d-a18a-f07b6b3b0c22", "direct")] // UUID
    [InlineData("https://play.hbomax.com/series/urn:hbo:series:GXdkEjAqJ9I5jkwEAAADr?cpid=GHgYc4Q…", "direct")]
    [InlineData("https://play.max.com/movie/1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", "direct")] // HBO епізодний UUID
    [InlineData("https://watch.plex.tv/movie/mission-to-mars-2000", "direct")] // /movie/{slug}
    [InlineData("https://www.rakuten.tv/ua/movies/the-matrix", "direct")]      // slug
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "direct")]      // watch?v= (YouTube video id)
    [InlineData("https://sweet.tv/ua-uk/movie/12345-diktator", "direct")]      // числовий id у шляху
    public void Deep_Links_Are_Direct(string url, string expected) =>
        Assert.Equal(expected, WatchUrlClassifier.Classify(url));

    [Theory]
    [InlineData(null)]                                                        // url:null → НЕ direct
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://www.netflix.com/ua/")]                               // homepage
    [InlineData("https://www.netflix.com/browse")]                            // каталог
    [InlineData("https://megogo.ua/ua/search?q=матриця")]                     // пошук
    [InlineData("https://megogo.ua/ua/collection/free_films_megogo")]         // колекція
    [InlineData("https://watch.plex.tv/movie")]                               // каталог Plex
    [InlineData("https://watch.plex.tv/movie-database/2000")]                 // річний індекс
    [InlineData("https://justwatch.com/ua/видео-онлайн")]                     // rootish-подібне
    [InlineData("https://www.youtube.com")]                                   // без шляху
    [InlineData("not a url")]
    public void Homepages_And_Collections_Are_Find(string? url) =>
        Assert.Equal(WatchUrlClassifier.Find, WatchUrlClassifier.Classify(url));
}

/// <summary>
/// UX-04: дедуп WatchOptionsMerger за (provider+url+мова-сигнатура) — різні мовні варіанти
/// НЕ губляться; freer-kind-wins у межах однієї сигнатури; unknown-сигнатура не дублює відомі.
/// </summary>
public class WatchOptionsMergerLangTests
{
    private static WatchOptionRow Row(
        string provider = "Apple TV Store", string kind = "rent", string? url = "https://x",
        string? source = "justwatch", string? audioLangs = null, string? subLangs = null,
        bool? isAvailable = null, string? price = null, string? currency = null) =>
        new(provider, kind, url, source, "UA", isAvailable, null, null, null, audioLangs, subLangs, price, currency);

    [Fact]
    public void Same_Provider_Url_Different_AudioLangs_Kept_Both()
    {
        var merged = WatchOptionsMerger.Merge(
            [],
            [Row(audioLangs: "en,ru"), Row(audioLangs: "uk")]);
        Assert.Equal(2, merged.Count); // uk/ru-набори НЕ колапсують
        Assert.Contains(merged, o => o.AudioLangs == "en,ru");
        Assert.Contains(merged, o => o.AudioLangs == "uk");
    }

    [Fact]
    public void Same_Signature_Deduped_Freer_Kind_Wins_Price_Kept()
    {
        var merged = WatchOptionsMerger.Merge(
            [],
            [Row(kind: "buy", price: "7,99₴", currency: "UAH"), Row(kind: "rent", price: "3,99₴", currency: "UAH", audioLangs: null)]);
        var o = Assert.Single(merged);
        Assert.Equal("rent", o.Kind);  // freer-kind-wins збережено
        Assert.Equal("3,99₴", o.Price); // чесна ціна переможця
    }

    [Fact]
    public void Unknown_Sig_Dropped_When_Known_Same_Or_Better_Kind_Exists()
    {
        var merged = WatchOptionsMerger.Merge(
            [Row(provider: "sweettv", kind: "free", url: "https://sweet.tv/x", source: "sweettv", audioLangs: "uk,ru")],
            [Row(provider: "sweettv", kind: "free", url: "https://sweet.tv/x", source: "sweettv", audioLangs: null)]);
        var o = Assert.Single(merged); // NULL-сигнатура дублює відомий набір → дроп
        Assert.Equal("uk,ru", o.AudioLangs);
    }

    [Fact]
    public void Unknown_Sig_Kept_When_It_Is_Freer_Conflict_Visible()
    {
        var merged = WatchOptionsMerger.Merge(
            [Row(provider: "sweettv", kind: "rent", url: "https://sweet.tv/x", source: "sweettv", audioLangs: "uk")],
            [Row(provider: "sweettv", kind: "free", url: "https://sweet.tv/x", source: "sweettv", audioLangs: null)]);
        Assert.Equal(2, merged.Count); // конфлікт джерел: free без мов + rent з uk — видно обидва
        Assert.Contains(merged, o => o.Kind == "free" && o.AudioLangs == null);
        Assert.Contains(merged, o => o.Kind == "rent" && o.AudioLangs == "uk");
    }

    [Fact]
    public void WatchLink_Legacy_AudioLang_Coexists_With_Jw_Langs()
    {
        var merged = WatchOptionsMerger.Merge(
            [new WatchOptionRow("youtube", "free", "https://youtu.be/v", null, "UA", true, "eng", 720, "original")],
            [Row(provider: "youtube", kind: "free", url: "https://youtu.be/v", source: "youtube", audioLangs: "en")]);
        // однакова сигнатура (en) — дублів нема; легасі-метадані перенесено
        var o = Assert.Single(merged);
        Assert.Equal("en", o.AudioLangs);
        Assert.Equal("eng", o.AudioLang); // легасі одиночне збережено
        Assert.Equal("original", o.Dub);
    }

    [Fact]
    public void Language_Variants_Are_Sorted_After_Kind()
    {
        var merged = WatchOptionsMerger.Merge(
            [],
            [Row(audioLangs: "ru"), Row(audioLangs: "en")]);
        Assert.Equal("en", merged[0].AudioLangs);
        Assert.Equal("ru", merged[1].AudioLangs);
    }
}

/// <summary>UX-04: JustWatch-мапер мов/цін — дедуп за (провайдер, kind, мова-сигнатура).</summary>
public class JustWatchLanguagesTests
{
    private static JustWatchOffer Offer(
        string mt, string? url, string clear, string tech,
        string? presentation = null, string[]? audio = null, string[]? subs = null,
        string? price = null, string? currency = null) =>
        new(mt, url, clear, tech, presentation, audio, subs, price, currency);

    [Fact]
    public void Different_Audio_Langs_Become_Separate_Provider_Rows()
    {
        var rows = JustWatchMapper.BuildProviders(1,
        [
            Offer("RENT", "https://tv.apple.com/x", "Apple TV Store", "itunes", "SD", ["en", "ru"], ["uk"]),
            Offer("RENT", "https://tv.apple.com/x", "Apple TV Store", "itunes", "HD", ["en", "ru"], ["uk"]),      // дубль presentation
            Offer("RENT", "https://tv.apple.com/x", "Apple TV Store", "itunes", "4K", ["en", "ru", "uk"], ["uk"]), // ІНШИЙ набір
        ], "UA", DateTime.UtcNow);

        Assert.Equal(2, rows.Count); // (en,ru) і (en,ru,uk) — мовні варіанти не губляться
        Assert.Contains(rows, r => r.AudioLangs == "en,ru");
        Assert.Contains(rows, r => r.AudioLangs == "en,ru,uk");
        Assert.All(rows, r => Assert.Equal("uk", r.SubLangs));
    }

    [Fact]
    public void Price_And_Currency_Captured()
    {
        var rows = JustWatchMapper.BuildProviders(1,
            [Offer("BUY", "https://x", "Apple TV Store", "itunes", "HD", ["en"], ["uk"], "7,99₴", "UAH")],
            "UA", DateTime.UtcNow);
        var r = Assert.Single(rows);
        Assert.Equal("7,99₴", r.Price);
        Assert.Equal("UAH", r.Currency);
    }

    [Fact]
    public void No_Languages_At_All_Nulls_Not_Empty()
    {
        var rows = JustWatchMapper.BuildProviders(1,
            [Offer("FLATRATE", "https://x", "Netflix", "netflix", "HD", [], [])],
            "UA", DateTime.UtcNow);
        var r = Assert.Single(rows);
        Assert.Null(r.AudioLangs); // невідома = NULL, не порожній рядок і не uk/ru
        Assert.Null(r.SubLangs);
    }

    [Fact]
    public void Unknown_Language_Tokens_Filtered_Not_Fabricated()
    {
        var rows = JustWatchMapper.BuildProviders(1,
            [Offer("RENT", "https://x", "Rakuten TV", "rakuten", "HD", ["klingon", "en"], ["zhongwen", "uk"])],
            "UA", DateTime.UtcNow);
        var r = Assert.Single(rows);
        Assert.Equal("en", r.AudioLangs); // невідомі відкинуті, відомі лишились
        Assert.Equal("uk", r.SubLangs);
    }

    [Fact]
    public void ParseNode_Reads_New_Offer_Fields_Tolerantly()
    {
        const string json = """
            {"data":{"popularTitles":{"edges":[{"node":{"id":"t1","objectType":"MOVIE","content":{"title":"Матриця","originalReleaseYear":1999,"externalIds":{"imdbId":"tt0133093"}},"offers":[{"monetizationType":"RENT","standardWebURL":"https://tv.apple.com/x","presentationType":"HD","audioLanguages":["en","ru"],"subtitleLanguages":["uk"],"retailPrice":"3,99₴","currency":"UAH","package":{"clearName":"Apple TV Store","technicalName":"itunes"}},{"monetizationType":"FLATRATE","standardWebURL":"https://netflix.com/title/1","package":{"clearName":"Netflix","technicalName":"netflix"}}]}},{"node":{"id":"t2","offers":[{"monetizationType":"CINEMA","package":{"clearName":"Cinemas","technicalName":"c"}}]}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}
            """;
        var page = JustWatchMapper.ParseSearch(json);
        var t = Assert.Single(page.Titles, x => x.JwId == "t1");
        var jw = t.Offers[0];
        Assert.Equal("HD", jw.PresentationType);
        Assert.Equal(["en", "ru"], jw.AudioLanguages);
        Assert.Equal(["uk"], jw.SubtitleLanguages);
        Assert.Equal("3,99₴", jw.RetailPrice);
        Assert.Equal("UAH", jw.Currency);

        var providers = JustWatchMapper.BuildProviders(5, t.Offers, "UA", DateTime.UtcNow);
        Assert.Equal(2, providers.Count); // CINEMA відкинуто; netflix-оффер без мов — NULL
        Assert.Null(providers.Single(p => p.ProviderName == "Netflix").AudioLangs);
    }
}

/// <summary>UX-04: sweet.tv парсер «Аудіодоріжки»/«Субтитри» (зразки sweet_diktator/sweet_deiv).</summary>
public class SweetTvLanguagesTests
{
    // фрагмент реальної view-сторінки (sweet_diktator.html): три аудіо + три суб
    private const string DiktatorFragment = """
        <div class="movie-available-info" data-v-78ac8c7b><div class="movie-available-info__section" data-v-78ac8c7b><div class="movie-available-info__label" data-v-78ac8c7b>Аудіодоріжки:</div><div class="movie-available-info__content" data-v-78ac8c7b><a href="/ua-uk/movies/filters/audiotrack/ukrajinska" class="movie-available-info__language movie-available-info__language--link" data-v-78ac8c7b><span class="iconify i-sweet:ua_flag movie-available-info__flag" aria-hidden="true" style="" data-v-78ac8c7b></span> Українська</a><a href="/ua-uk/movies/filters/audiotrack/english" class="movie-available-info__language movie-available-info__language--link" data-v-78ac8c7b><!----> Англійська</a><a href="/ua-uk/movies/filters/audiotrack/russkiy" class="movie-available-info__language movie-available-info__language--link" data-v-78ac8c7b><!----> Російська</a></div></div><div class="movie-available-info__section" data-v-78ac8c7b><div class="movie-available-info__label" data-v-78ac8c7b>Субтитри:</div><div class="movie-available-info__content" data-v-78ac8c7b><a href="/ua-uk/movies/filters/subtitle/ukrajinska" class="movie-available-info__language movie-available-info__language--link" data-v-78ac8c7b>Українська</a><a href="/ua-uk/movies/filters/subtitle/english" class="movie-available-info__language movie-available-info__language--link" data-v-78ac8c7b>Англійська</a><a href="/ua-uk/movies/filters/subtitle/russkiy" class="movie-available-info__language movie-available-info__language--link" data-v-78ac8c7b>Російська</a></div></div></div>
        """;

    [Fact]
    public void Parses_Audio_And_Subs_From_Real_Structure()
    {
        var langs = SweetTvParser.ParseLanguages(DiktatorFragment);
        Assert.Equal("en,ru,uk", langs.AudioLangs);
        Assert.Equal("en,ru,uk", langs.SubLangs);
    }

    [Fact]
    public void Per_Movie_Semantics_No_Ru_When_Absent()
    {
        // контрольний тайтл БЕЗ рос. озвучки: блок не містить russkiy → ru НЕ з'являється
        const string noRu = """
            <div class="movie-available-info"><div class="movie-available-info__label">Аудіодоріжки:</div><div class="movie-available-info__content"><a href="/ua-uk/movies/filters/audiotrack/ukrajinska">Українська</a><a href="/ua-uk/movies/filters/audiotrack/english">Англійська</a></div></div>
            """;
        var langs = SweetTvParser.ParseLanguages(noRu);
        Assert.Equal("en,uk", langs.AudioLangs);
        Assert.Null(langs.SubLangs); // блоку субтитрів нема → NULL (не вигадуємо)
    }

    [Fact]
    public void Unknown_Slags_Dropped_Empty_Page_Null()
    {
        var langs = SweetTvParser.ParseLanguages("<a href=\"/ua-uk/movies/filters/audiotrack/klingon\">?</a>");
        Assert.Null(langs.AudioLangs);
        Assert.Null(SweetTvParser.ParseLanguages("").AudioLangs);
    }
}

/// <summary>UX-04: Megogo парсер «Переклад»/«Субтитри» (зразок megogo_view_sample.html).</summary>
public class MegogoLanguagesTests
{
    private const string ViewFragment = """
        <div class="video-block video-audio">
                <h4>Переклад</h4>

                <span>Українська</span>, <span>Російська</span>, <span>Англійська</span>, <span>Польська</span>, <span>Угорська</span>, <span>Чеська</span>
            </div>
            <div class="video-block video-cc">
                <h4>Субтитри</h4>
                <span>Українська</span>, <span>Російська</span>, <span>Болгарська</span>
            </div>
        """;

    [Fact]
    public void Parses_Translation_Block_From_Real_Structure()
    {
        var langs = MegogoParser.ParseViewLanguages(ViewFragment);
        Assert.Equal("cs,en,hu,pl,ru,uk", langs.AudioLangs);
        Assert.Equal("bg,ru,uk", langs.SubLangs);
    }

    [Fact]
    public void No_Blocks_Null()
    {
        var langs = MegogoParser.ParseViewLanguages("<html><body>без блоків</body></html>");
        Assert.Null(langs.AudioLangs);
        Assert.Null(langs.SubLangs);
    }

    [Fact]
    public void ViewPath_Formats_Link()
    {
        Assert.Equal("/ua/view/1003951-Dictator.html", MegogoParser.ViewPath(1003951, "Dictator"));
    }

    [Fact]
    public void BuildProviderRow_Langs_Nullable()
    {
        var card = new MegogoCatalogItem(1, "slug", "Назва", 2020, null, "free", "https://megogo.ua/ua/view/1-slug.html");
        var row = MegogoSyncService.BuildProviderRow(42, card, "en,uk", null);
        Assert.Equal("en,uk", row.AudioLangs);
        Assert.Null(row.SubLangs);
        Assert.Equal("Megogo", row.ProviderName);
    }
}

/// <summary>UX-04: Plex tri-state — парсер «Where to Watch» (RSC-флайт, реальні форми проб хвилі-2).</summary>
public class PlexAvailabilityTests
{
    private static string Rsc(string payloadJson) =>
        $"<script>self.__next_f.push([2,\"{payloadJson}\"])</script>";

    [Fact]
    public void Available_When_AvailabilityCards_Present()
    {
        // форма з реальної сторінки the-matrix (plex_matrix.html):
        // {"title":"Apple TV","subtitles":["Buy UAH 7.99"],...,"_component":"AvailabilityCard"}
        const string json = """
            {"children":[{"title":"Apple TV","subtitles":["Buy UAH 7.99"],"link":{"url":"https://tv.apple.com/ua/movie/x"},"_component":"AvailabilityCard"},{"title":"Amazon Video","subtitles":["Buy UAH 3.99"],"_component":"AvailabilityCard"}]}
            """;
        var escaped = json.Replace("\"", "\\\"");
        var result = PlexAvailabilityParser.Parse(Rsc(escaped));
        Assert.Equal(PlexAvailabilityResult.Available, result.Status);
        Assert.Contains("Apple TV", result.Services);
        Assert.Contains("Amazon Video", result.Services);
    }

    [Fact]
    public void Unavailable_When_NoAvailabilities_Marker()
    {
        // форма з реальної сторінки mission-to-mars (mtm_plex.html)
        const string json = """
            {"visible":true,"id":"noAvailabilities","label":"No Streaming Availability"}
            """;
        var escaped = json.Replace("\"", "\\\"");
        var result = PlexAvailabilityParser.Parse(Rsc(escaped));
        Assert.Equal(PlexAvailabilityResult.Unavailable, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html>звичайна сторінка без RSC</html>")] // бот-стіна/змінена розмітка → unknown
    public void Unparseable_Or_Empty_Is_Unknown(string? html)
    {
        var result = PlexAvailabilityParser.Parse(html);
        Assert.Equal(PlexAvailabilityResult.Unknown, result.Status);
        Assert.Empty(result.Services);
    }

    [Fact]
    public void Metadata_Title_Without_Subtitles_Is_Not_A_Card()
    {
        // метадата тайтла теж має "title", але без сусіднього "subtitles" — не хибне спрацювання
        const string json = """
            {"metadataItem":{"title":"Mission to Mars","type":"movie"},"other":"x"}
            """;
        var escaped = json.Replace("\"", "\\\"");
        var result = PlexAvailabilityParser.Parse(Rsc(escaped));
        Assert.Equal(PlexAvailabilityResult.Unknown, result.Status);
    }

    [Fact]
    public void Catalog_Provider_Rows_Have_Null_Langs_Never_Fabricated()
    {
        var p = PlexSyncService.BuildProvider(42,
            new PlexCatalogItem("the-matrix", "The Matrix", 1999, "/movie/the-matrix"),
            "https://watch.plex.tv");
        Assert.Equal("plex", p.Source);
        Assert.Equal("free", p.Kind);
        Assert.Null(p.AudioLangs); // мови НЕ вигадуємо (UX-04)
        Assert.Null(p.SubLangs);
    }
}
