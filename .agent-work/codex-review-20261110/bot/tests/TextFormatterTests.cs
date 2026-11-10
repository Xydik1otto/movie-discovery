using MovieDiscovery.Bot;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

public class TextFormatterTests
{
    // ---------------------------------------------------------------- Escape (HTML)

    [Fact]
    public void Escape_EncodesAngleBracketsAndAmpersand()
    {
        Assert.Equal("&lt;b&gt;", TextFormatter.Escape("<b>"));
        Assert.Equal("a&amp;b", TextFormatter.Escape("a&b"));
        Assert.Equal("&lt;i&gt;назва&lt;/i&gt;", TextFormatter.Escape("<i>назва</i>"));
    }

    [Fact]
    public void Escape_NullAndEmpty_AreSafe()
    {
        Assert.Equal(string.Empty, TextFormatter.Escape(null));
        Assert.Equal(string.Empty, TextFormatter.Escape(string.Empty));
    }

    [Fact]
    public void Escape_UkTextWithHtmlFragment_IsFullyEscaped()
    {
        var escaped = TextFormatter.Escape("Фільм <С Woods> & «Дюна»");
        Assert.Equal("Фільм &lt;С Woods&gt; &amp; «Дюна»", escaped);
    }

    // ---------------------------------------------------------------- NormalizeWhitespace

    [Theory]
    [InlineData("  a\n\n b\t c ", "a b c")]
    [InlineData("один рядок", "один рядок")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeWhitespace_CollapsesWhitespace(string? input, string expected)
        => Assert.Equal(expected, TextFormatter.NormalizeWhitespace(input));

    // ---------------------------------------------------------------- Truncate (опис)

    [Fact]
    public void Truncate_ShortText_Unchanged()
    {
        Assert.Equal("Короткий опис", TextFormatter.Truncate("Короткий опис", 300));
        Assert.Equal("Короткий опис", TextFormatter.Truncate("Короткий опис", 13));
    }

    [Fact]
    public void Truncate_LongTextNoSpaces_CutsAtMaxWithEllipsis()
    {
        var longText = new string('а', 500);
        var result = TextFormatter.Truncate(longText, 300);
        Assert.Equal(301, result.Length);
        Assert.EndsWith("…", result);
        Assert.StartsWith("ааа", result);
    }

    [Fact]
    public void Truncate_LongText_CutsOnWordBoundaryAndAddsEllipsis()
    {
        var text = string.Join(' ', Enumerable.Repeat("слово", 100)); // 599 символів
        var result = TextFormatter.Truncate(text, 300);
        Assert.EndsWith("…", result);
        Assert.True(result.Length <= 301);
        Assert.EndsWith("слово…", result); // обрізано на межі слова, без «напівслов»
    }

    [Fact]
    public void Truncate_NullOrWhitespace_IsEmpty()
    {
        Assert.Equal(string.Empty, TextFormatter.Truncate(null, 300));
        Assert.Equal(string.Empty, TextFormatter.Truncate("   \n\t ", 300));
    }

    [Fact]
    public void Truncate_ZeroOrNegativeMax_IsEmpty()
    {
        Assert.Equal(string.Empty, TextFormatter.Truncate("abc", 0));
        Assert.Equal(string.Empty, TextFormatter.Truncate("abc", -5));
    }

    // ---------------------------------------------------------------- емодзі (мапінг kind/type)

    [Theory]
    [InlineData("movie", "🎬")]
    [InlineData("MOVIE", "🎬")]
    [InlineData("series", "📺")]
    [InlineData("show", "🎙️")]
    [InlineData("show ", "🎙️")]
    [InlineData("cartoon", "🎞️")]
    [InlineData(null, "🎞️")]
    public void TypeEmoji_MapsTypeToEmoji(string? type, string expected)
        => Assert.Equal(expected, TextFormatter.TypeEmoji(type));

    [Theory]
    [InlineData("watchlist", "📌")]
    [InlineData("Watchlist", "📌")]
    [InlineData("watched", "✅")]
    [InlineData("Watched", "✅")]
    [InlineData("custom", "📋")]
    [InlineData(null, "📋")]
    public void KindEmoji_MapsListKindToEmoji(string? kind, string expected)
        => Assert.Equal(expected, TextFormatter.KindEmoji(kind));

    // ---------------------------------------------------------------- TMDB-посилання

    [Theory]
    [InlineData("movie", 456, "https://www.themoviedb.org/movie/456")]
    [InlineData("series", 789, "https://www.themoviedb.org/tv/789")]
    [InlineData("show", 111, "https://www.themoviedb.org/tv/111")]
    [InlineData(null, 222, "https://www.themoviedb.org/movie/222")]
    public void TmdbLink_MovieGoesToMovie_SeriesAndShowGoToTv(string? type, int tmdbId, string expected)
        => Assert.Equal(expected, TextFormatter.TmdbLink(type, tmdbId));

    // ---------------------------------------------------------------- рейтинги

    [Fact]
    public void FormatRatings_BothRatings_JoinedWithDot()
        => Assert.Equal("⭐ 7.8 · IMDb 8.1", TextFormatter.FormatRatings(7.8, 8.1));

    [Fact]
    public void FormatRatings_OnlyTmdb()
        => Assert.Equal("⭐ 7.5", TextFormatter.FormatRatings(7.5, null));

    [Fact]
    public void FormatRatings_None_IsEmpty()
        => Assert.Equal(string.Empty, TextFormatter.FormatRatings(null, null));

    [Fact]
    public void FormatRatings_UsesInvariantDecimalPoint()
        => Assert.Equal("⭐ 7.0", TextFormatter.FormatRatings(7.0, null)); // без «7,0» незалежно від культури
}
