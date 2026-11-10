using MovieDiscovery.Core.Search;
using Xunit;

namespace MovieDiscovery.Tests;

/// <summary>
/// FtsQueryBuilder: сирий рядок пошуку → tsquery для Фази A (FTS, конфіг 'simple').
/// Спецсимволи вирізаються (to_tsquery падає на синтаксисі), стопворди en/uk/ru
/// відкидаються, токени &lt;2 символів — теж; до кожного токена додається ':*'.
/// </summary>
public class FtsQueryBuilderTests
{
    [Theory]
    [InlineData("dune", "dune:*")]
    [InlineData("DUNE", "dune:*")]
    [InlineData("  Dune: Part Two* ", "dune:* & part:* & two:*")]
    [InlineData("the matrix", "matrix:*")]                       // 'the' — стопворд
    [InlineData("Втеча з Шоушенка", "втеча:* & шоушенка:*")]      // 'з' відкидається
    [InlineData("A&B | !C:D/E<F>G", "ab:* & cdefg:*")] // спецсимволи ВИРІЗАЮТЬСЯ в токені, не розбивають його
    [InlineData("don't stop", "dont:* & stop:*")]      // ' вирізано
    [InlineData("star trek (2009)", "star:* & trek:* & 2009:*")] // круглі дужки
    [InlineData(" Spider-Man ", "spider-man:*")]
    public void Build_TrimsLowercasesCutsSpecials_AddsPrefixStar(string raw, string expected) =>
        Assert.Equal(expected, FtsQueryBuilder.Build(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the of and")]   // тільки стопворди
    [InlineData("a")]            // довжина < 2
    [InlineData("!!")]           // тільки спецсимволи
    [InlineData("a & b")]        // односимвольні після чистки
    [InlineData("🚀 🙂")]         // емодзі: без літер/цифр → to_tsquery дав би syntax error
    [InlineData("🚀")]
    public void Build_ReturnsNull_WhenNoUsableTokens(string? raw) =>
        Assert.Null(FtsQueryBuilder.Build(raw));

    [Fact]
    public void Build_Emoji_MixedWithText_DropsEmojiKeepsText() =>
        Assert.Equal("dune:*", FtsQueryBuilder.Build("dune 🚀"));

    [Fact]
    public void Build_UkrainianLowercase_Works()
    {
        // Кирилиця: InvariantCulture lowercase не ламає українські літери
        var sql = FtsQueryBuilder.Build("СТАЛКЕР");
        Assert.Equal("сталкер:*", sql);
    }
}
