using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Search;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Tests;

/// <summary>
/// FR-19: віртуальні категорії tvshow/cartoon/dorama + country/audioLang/minQuality —
/// InMemory-логіка (семантика відбору) + трансляція SQL (Npgsql ToQueryString, без БД).
/// </summary>
public class VirtualCategorySearchTests
{
    private static MovieDiscoveryDbContext Create(string name) =>
        new(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static MovieDiscoveryDbContext CreateNpgsql() =>
        new(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseNpgsql("Host=localhost;Port=5433;Database=category_translation_check", npg => npg.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    private static async Task<MovieDiscoveryDbContext> SeedAsync()
    {
        var db = Create("vcats_" + Guid.NewGuid().ToString("N"));
        db.Titles.AddRange(
            new Title { Type = "series", TitleEn = "Big Brother", Genres = ["reality-tv", "game-show"] },
            new Title { Type = "series", TitleEn = "Late Talk", Genres = ["talk-show"] },
            new Title { Type = "anime", TitleEn = "Naruto", Genres = ["animation", "action"] },          // аніме НЕ cartoon
            new Title { Type = "movie", TitleEn = "Toy Story", Genres = ["animation", "family"] },        // cartoon (movie)
            new Title { Type = "series", TitleEn = "SpongeBob", Genres = ["animation", "comedy"] },       // cartoon (series)
            new Title { Type = "series", TitleEn = "Crash Landing", Genres = ["romance", "comedy"], Country = "KR" }, // KR без drama
            new Title { Type = "movie", TitleEn = "Parasite", Genres = ["drama"], Countries = ["KR"] },   // countries[] без title.country
            new Title { Type = "movie", TitleEn = "Oldboy", Genres = ["drama", "thriller"], Country = "KR" },
            new Title { Type = "movie", TitleEn = "Se7en", Genres = ["drama"], Country = "US" });
        db.WatchLinks.AddRange(
            new WatchLink { TitleId = 4, Provider = "youtube", Kind = "free", Region = "*", Url = "https://yt/1", IsAvailable = true, AudioLang = "ukr", Quality = 1080, Dub = "original" }, // Toy Story
            new WatchLink { TitleId = 1, Provider = "sweettv", Kind = "free", Region = "UA", Url = "https://st/2", IsAvailable = true, AudioLang = "ukr", Quality = 720, Dub = "ukr" },       // Big Brother
            new WatchLink { TitleId = 8, Provider = "archive", Kind = "free", Region = "*", Url = "https://ia/3", IsAvailable = true, AudioLang = "eng", Quality = 480, Dub = "eng" });       // Oldboy
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<SearchResult> SearchAsync(MovieDiscoveryDbContext db, SearchQuery q) =>
        await new SearchService(db).SearchAsync(q);

    private static SearchQuery Base(string? category = null, string? country = null, string? audioLang = null, int? minQuality = null) =>
        new(null, null, [], null, null, country, null, null, null, null, null,
            "relevance", 1, 50, Category: category, AudioLang: audioLang, MinQuality: minQuality);

    // ── category ──

    [Fact]
    public async Task Category_Tvshow_Overlap_Genres()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(category: "tvshow"));
        var names = result.Items.Select(t => t.TitleEn).ToHashSet();
        Assert.Contains("Big Brother", names);
        Assert.Contains("Late Talk", names);
        Assert.DoesNotContain("Toy Story", names);
    }

    [Fact]
    public async Task Category_Cartoon_Animation_Without_Anime()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(category: "cartoon"));
        var names = result.Items.Select(t => t.TitleEn).ToHashSet();
        Assert.Contains("Toy Story", names);
        Assert.Contains("SpongeBob", names);
        Assert.DoesNotContain("Naruto", names); // аніме НЕ дублюється в cartoon
    }

    [Fact]
    public async Task Category_Dorama_Country_And_Drama()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(category: "dorama"));
        var names = result.Items.Select(t => t.TitleEn).ToHashSet();
        Assert.Contains("Oldboy", names);           // title.country=KR + drama
        Assert.DoesNotContain("Crash Landing", names); // KR без жанру drama
        Assert.DoesNotContain("Parasite", names);      // лише countries[] (title.country порожній) — категорія читає саме title.country
        Assert.DoesNotContain("Se7en", names);         // US
    }

    [Fact]
    public async Task Category_Movie_Maps_To_Type()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(category: "movie"));
        Assert.All(result.Items, t => Assert.Equal("movie", t.Type));
    }

    // ── country: title.country (akas) АБО countries[] (TMDB) ──

    [Fact]
    public async Task Country_Matches_TitleCountry_Or_Countries_Array()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(country: "KR"));
        var names = result.Items.Select(t => t.TitleEn).ToHashSet();
        Assert.Contains("Oldboy", names);     // title.country (FR-19 akas)
        Assert.Contains("Parasite", names);   // countries[] (TMDB) — працює ДО akas-синку
        Assert.DoesNotContain("Se7en", names);
    }

    [Fact]
    public async Task Country_Comma_List_Any_Match()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(country: "UA,US"));
        var names = result.Items.Select(t => t.TitleEn).ToHashSet();
        Assert.Contains("Se7en", names);
        Assert.DoesNotContain("Oldboy", names);
    }

    // ── audioLang / minQuality: EXISTS watch_links ──

    [Fact]
    public async Task AudioLang_Exists_On_Available_Watch_Links()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(audioLang: "ukr"));
        var names = result.Items.Select(t => t.TitleEn).ToHashSet();
        Assert.Contains("Toy Story", names);    // youtube-лінк audioLang=ukr
        Assert.Contains("Big Brother", names);  // sweettv-лінк audioLang=ukr
        Assert.DoesNotContain("Oldboy", names); // eng
    }

    [Fact]
    public async Task MinQuality_Greater_Or_Equal()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(minQuality: 1080));
        var names = result.Items.Select(t => t.TitleEn).ToHashSet();
        Assert.Contains("Toy Story", names);   // 1080
        Assert.DoesNotContain("Big Brother", names); // 720 < 1080
        Assert.DoesNotContain("Oldboy", names);      // 480
    }

    [Fact]
    public async Task Filters_Combine()
    {
        var db = await SeedAsync();
        var result = await SearchAsync(db, Base(category: "cartoon", audioLang: "ukr", minQuality: 1080));
        var names = result.Items.Select(t => t.TitleEn).ToHashSet();
        Assert.Contains("Toy Story", names);
        Assert.DoesNotContain("SpongeBob", names); // без лінків
    }

    // ── трансляція SQL (Npgsql, ToQueryString — без звернення до БД) ──

    [Fact]
    public void Sql_Dorama_Translates_To_Country_And_Genres()
    {
        using var db = CreateNpgsql();
        var sql = new SearchService(db).BuildQuery(Base(category: "dorama")).ToQueryString();
        Assert.Contains("country", sql);
        Assert.Contains("genres", sql);
    }

    [Fact]
    public void Sql_Tvshow_Overlap_Translates()
    {
        using var db = CreateNpgsql();
        var sql = new SearchService(db).BuildQuery(Base(category: "tvshow")).ToQueryString();
        Assert.Contains("genres", sql);
    }

    [Fact]
    public void Sql_Cartoon_Containment_And_Type_Filter()
    {
        using var db = CreateNpgsql();
        var sql = new SearchService(db).BuildQuery(Base(category: "cartoon")).ToQueryString();
        Assert.Contains("genres", sql);
        Assert.Contains("type", sql);
        Assert.Contains("animation", sql);
    }

    [Fact]
    public void Sql_AudioLang_Exists_WatchLinks()
    {
        using var db = CreateNpgsql();
        var sql = new SearchService(db).BuildQuery(Base(audioLang: "ukr")).ToQueryString();
        Assert.Contains("watch_links", sql);
        Assert.Contains("audio_lang", sql);
        Assert.Contains("is_available", sql);
    }

    [Fact]
    public void Sql_MinQuality_Exists_WatchLinks()
    {
        using var db = CreateNpgsql();
        var sql = new SearchService(db).BuildQuery(Base(minQuality: 1080)).ToQueryString();
        Assert.Contains("watch_links", sql);
        Assert.Contains("quality", sql);
    }

    [Fact]
    public void Sql_Country_Uses_TitleCountry_And_Array()
    {
        using var db = CreateNpgsql();
        var sql = new SearchService(db).BuildQuery(Base(country: "US")).ToQueryString();
        Assert.Contains("country", sql);
        Assert.Contains("countries", sql);
    }

    // ── валідація параметрів контролера ──

    [Fact]
    public void VirtualCategories_Constants_Match_Contract()
    {
        Assert.Equal(["movie", "series", "anime", "manga", "tvshow", "cartoon", "dorama"], VirtualCategories.All);
        Assert.Equal(["reality-tv", "talk-show", "game-show", "variety"], VirtualCategories.GenreSets["tvshow"]);
        Assert.Equal(["animation"], VirtualCategories.GenreSets["cartoon"]);
        Assert.Equal(["KR", "CN", "JP"], VirtualCategories.DoramaCountries);
        Assert.Equal("drama", VirtualCategories.DoramaGenre);
        Assert.Equal(["ukr", "russ", "eng", "jpn", "kor", "zho", "original", "multi"], VirtualCategories.AudioLangs);
        Assert.Equal([480, 720, 1080, 2160], VirtualCategories.Qualities);
        Assert.True(VirtualCategories.IsValid(null));
        Assert.True(VirtualCategories.IsValid("  Dorama "));
        Assert.False(VirtualCategories.IsValid("telenovela"));
    }
}
