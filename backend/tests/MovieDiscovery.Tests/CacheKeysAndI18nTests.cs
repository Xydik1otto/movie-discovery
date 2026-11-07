using MovieDiscovery.Infrastructure.Localization;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Tests;

public class CacheKeysAndI18nTests
{
    [Theory]
    [InlineData("popular", "movie", "uk-UA", 3, "list:movie:popular:uk-UA:p3")]
    [InlineData("discover", "series", "en-US", 12, "list:series:discover:en-US:p12")]
    public void List_Key_Format(string kind, string type, string lang, int page, string expected) =>
        Assert.Equal(expected, TmdbCacheKeys.List(kind, type, lang, page));

    [Theory]
    [InlineData("movie", 123, "uk-UA", "details:movie:123:uk-UA")]
    [InlineData("series", 555, "en-US", "details:series:555:en-US")]
    public void Details_Key_Format(string type, int id, string lang, string expected) =>
        Assert.Equal(expected, TmdbCacheKeys.Details(type, id, lang));

    [Fact]
    public void Providers_Key_Format() =>
        Assert.Equal("providers:movie:123:UA", TmdbCacheKeys.Providers("movie", 123, "UA"));

    [Fact]
    public void Omdb_Key_Format() =>
        Assert.Equal("omdb:the office:2005", TmdbCacheKeys.Omdb("The Office", 2005));

    [Fact]
    public void I18n_Uk_And_En_Have_Same_Keys()
    {
        Assert.Equal(
            I18nCatalog.Uk.Keys.OrderBy(k => k).ToList(),
            I18nCatalog.En.Keys.OrderBy(k => k).ToList());
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("en")]
    public void I18n_Covers_All_Plan_Keys(string lang)
    {
        var catalog = lang == "uk" ? I18nCatalog.Uk : I18nCatalog.En;
        string[] required =
        [
            "nav.home", "nav.similar", "nav.lists", "nav.settings",
            "search.placeholder", "search.button",
            "filters.type", "filters.genre", "filters.year", "filters.country",
            "filters.rating", "filters.runtime", "filters.apply", "filters.reset",
            "title.similar", "title.why", "title.watchlist", "title.watched", "title.watchedAdd",
            "title.providers", "title.trailer",
            "similar.seedsTitle", "similar.seedsHint", "similar.byText", "similar.byTextPlaceholder",
            "lists.watchlist", "lists.watched", "lists.empty",
            "common.loading", "common.error", "api.unavailable", "lang.switch",
        ];
        foreach (var key in required)
            Assert.True(catalog.ContainsKey(key), $"Відсутній ключ i18n: {key} ({lang})");
    }

    [Fact]
    public void I18n_Unknown_Lang_Is_Null() =>
        Assert.Null(I18nCatalog.For("ru")); // російської нема свідомо

    [Fact]
    public void Genre_Normalization_Uk_To_En()
    {
        Assert.Equal("Drama", TmdbGenres.NormalizeToEn("драма"));
        Assert.Equal("Drama", TmdbGenres.NormalizeToEn("Drama"));
        Assert.Equal("Comedy", TmdbGenres.NormalizeToEn("комедія"));
        Assert.Null(TmdbGenres.NormalizeToEn("неіснуючий жанр"));
    }
}
