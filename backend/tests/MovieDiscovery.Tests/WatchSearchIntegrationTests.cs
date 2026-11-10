using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Tests;

/// <summary>
/// IV-06/IV-07 (Codex-приймання, адаптація fixtures CodexFeatureIntegrationTests):
///   IV-06: HasTrailer = EXISTS(title_trailers verified_status='ok') OR titles.trailer_yt_id ≠ '';
///   IV-07: audioLang-фільтр бачить І легасі watch_links.audio_lang, І нові audio_langs
///          (csv ISO), точний токен-матчинг (без substring false-positives),
///          субтитри НЕ рахуються як аудіо.
/// </summary>
public class WatchSearchIntegrationTests
{
    private static SearchQuery Query(bool? trailer = null, string? audio = null) =>
        new(null, null, [], null, null, null, null, null, null, null, null, "popularity", 1, 20,
            HasTrailer: trailer, AudioLang: audio);

    // ── IV-06: hasTrailer ──

    [Fact]
    public async Task HasTrailer_Includes_Verified_New_Trailer_Storage()
    {
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Id = 91001, Type = "movie", TitleEn = "Trailer fixture", Year = 2000 });
        db.TitleTrailers.Add(new TitleTrailer { TitleId = 91001, YtId = "abcdefghijk", Lang = "uk", VerifiedStatus = "ok" });
        await db.SaveChangesAsync();

        var result = await new SearchService(db).SearchAsync(Query(trailer: true));
        Assert.Contains(result.Items, t => t.Id == 91001);
    }

    [Fact]
    public async Task HasTrailer_Legacy_YtId_Still_Matches()
    {
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Id = 91003, Type = "movie", TitleEn = "Legacy trailer", Year = 2001, TrailerYtId = "legacyid001" });
        await db.SaveChangesAsync();

        var result = await new SearchService(db).SearchAsync(Query(trailer: true));
        Assert.Contains(result.Items, t => t.Id == 91003);
    }

    [Fact]
    public async Task HasTrailer_Excludes_Gone_And_NonVerified_Trailers()
    {
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Id = 91004, Type = "movie", TitleEn = "Gone trailer", Year = 2002 });
        db.TitleTrailers.Add(new TitleTrailer { TitleId = 91004, YtId = "gonevid0001", Lang = "uk", VerifiedStatus = "gone" });
        db.Titles.Add(new Title { Id = 91005, Type = "movie", TitleEn = "Unverified trailer", Year = 2003 });
        db.TitleTrailers.Add(new TitleTrailer { TitleId = 91005, YtId = "unvervid001", Lang = "uk", VerifiedStatus = null });
        await db.SaveChangesAsync();

        var result = await new SearchService(db).SearchAsync(Query(trailer: true));
        Assert.DoesNotContain(result.Items, t => t.Id == 91004); // gone ≠ ok
        Assert.DoesNotContain(result.Items, t => t.Id == 91005); // не верифіковано ≠ ok
    }

    // ── IV-07: audioLang ──

    [Fact]
    public async Task AudioFilter_Includes_New_MultiLanguage_Offer()
    {
        // точні fixtures Codex: AudioLangs='uk,ru', AudioLang=null, фільтр ukr → матч
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Id = 91002, Type = "movie", TitleEn = "Audio fixture", Year = 2000 });
        db.WatchLinks.Add(new WatchLink
        {
            TitleId = 91002, Provider = "fixture", Kind = "free", Region = "UA",
            Url = "https://example.test/movie/91002", IsAvailable = true, AudioLangs = "uk,ru",
        });
        await db.SaveChangesAsync();

        var result = await new SearchService(db).SearchAsync(Query(audio: "ukr"));
        Assert.Contains(result.Items, t => t.Id == 91002);
    }

    [Fact]
    public async Task AudioFilter_Iso_Query_Matches_Legacy_Ukr_Field()
    {
        // зворотний бік: фільтр «uk» знаходить легасі audio_lang='ukr'
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Id = 91006, Type = "movie", TitleEn = "Legacy ukr fixture", Year = 2004 });
        db.WatchLinks.Add(new WatchLink
        {
            TitleId = 91006, Provider = "fixture", Kind = "free", Region = "UA",
            Url = "https://example.test/movie/91006", IsAvailable = true, AudioLang = "ukr",
        });
        await db.SaveChangesAsync();

        var result = await new SearchService(db).SearchAsync(Query(audio: "uk"));
        Assert.Contains(result.Items, t => t.Id == 91006);
    }

    [Fact]
    public async Task AudioFilter_No_Substring_False_Positive()
    {
        // 'russ' НЕ матчить 'russian' (інша мова), 'ru' НЕ матчить 'belarusian':
        // у рядку з audio_lang='russian' фільтр 'russ' НЕ спрацьовує (russian — не канонічний
        // токен синонімів russ→ru), а в audio_langs='be' (belarusian ISO) фільтр 'ru' не матчить.
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Id = 91007, Type = "movie", TitleEn = "Russian full", Year = 2005 });
        db.WatchLinks.Add(new WatchLink
        {
            TitleId = 91007, Provider = "fixture", Kind = "free", Region = "UA",
            Url = "https://example.test/movie/91007", IsAvailable = true, AudioLang = "russian",
        });
        db.Titles.Add(new Title { Id = 91008, Type = "movie", TitleEn = "Belarusian csv", Year = 2005 });
        db.WatchLinks.Add(new WatchLink
        {
            TitleId = 91008, Provider = "fixture", Kind = "free", Region = "UA",
            Url = "https://example.test/movie/91008", IsAvailable = true, AudioLangs = "be,ru",
        });
        await db.SaveChangesAsync();

        var russResult = await new SearchService(db).SearchAsync(Query(audio: "russ"));
        Assert.DoesNotContain(russResult.Items, t => t.Id == 91007); // 'russian' ≠ канонічний 'ru' — чесний no-match

        var ruResult = await new SearchService(db).SearchAsync(Query(audio: "ru"));
        Assert.Contains(ruResult.Items, t => t.Id == 91008);  // точний токен 'ru' у csv матчить
        Assert.DoesNotContain(ruResult.Items, t => t.Id == 91007); // 'russian' не зіпсований substring'ом
    }

    [Fact]
    public async Task AudioFilter_Subtitles_Do_Not_Count_As_Audio()
    {
        // uk лише в СУБТИТРАХ (sub_langs='uk'), аудіо en → фільтр uk НЕ матчить (IV-07: суб ≠ аудіо)
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Id = 91009, Type = "movie", TitleEn = "Subs only", Year = 2006 });
        db.WatchLinks.Add(new WatchLink
        {
            TitleId = 91009, Provider = "fixture", Kind = "free", Region = "UA",
            Url = "https://example.test/movie/91009", IsAvailable = true,
            AudioLangs = "en", SubLangs = "uk",
        });
        await db.SaveChangesAsync();

        var result = await new SearchService(db).SearchAsync(Query(audio: "ukr"));
        Assert.DoesNotContain(result.Items, t => t.Id == 91009);
    }

    [Fact]
    public async Task AudioFilter_Unavailable_Links_Ignored()
    {
        // is_available=false — не кандидат (як і до хвилі-2)
        await using var db = TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title { Id = 91010, Type = "movie", TitleEn = "Unavailable uk", Year = 2007 });
        db.WatchLinks.Add(new WatchLink
        {
            TitleId = 91010, Provider = "fixture", Kind = "free", Region = "UA",
            Url = "https://example.test/movie/91010", IsAvailable = false, AudioLangs = "uk",
        });
        await db.SaveChangesAsync();

        var result = await new SearchService(db).SearchAsync(Query(audio: "ukr"));
        Assert.DoesNotContain(result.Items, t => t.Id == 91010);
    }

    [Fact]
    public void AudioLangVariants_Synonym_Pairs_Exact_Tokens()
    {
        // множинна семантика (варіанти — Set): {raw, canonical ISO, легасі-форма}
        Assert.True(SearchService.AudioLangVariants("ukr").SetEquals(["ukr", "uk"]));
        Assert.True(SearchService.AudioLangVariants("uk").SetEquals(["uk", "ukr"]));
        Assert.True(SearchService.AudioLangVariants("russ").SetEquals(["russ", "ru"]));
        Assert.True(SearchService.AudioLangVariants("eng").SetEquals(["eng", "en"]));
        Assert.True(SearchService.AudioLangVariants("rus").SetEquals(["rus", "ru", "russ"])); // 'rus'→'ru' канонічний, 'russ' легасі-форма — усі три синоніми
        // невідомий токен — чесний no-match (як є, без вигаданого фолбеку)
        Assert.True(SearchService.AudioLangVariants("klingon").SetEquals(["klingon"]));
    }
}
