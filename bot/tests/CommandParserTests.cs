using MovieDiscovery.Bot;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

public class CommandParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("привіт")]
    [InlineData("/")]
    [InlineData("/ ")]
    public void Parse_NotACommand_ReturnsNull(string? text)
        => Assert.Null(CommandParser.Parse(text));

    [Fact]
    public void Parse_CommandWithoutArgs()
    {
        var cmd = CommandParser.Parse("/start");
        Assert.NotNull(cmd);
        Assert.Equal("start", cmd.Command);
        Assert.Equal(string.Empty, cmd.Args);
    }

    [Fact]
    public void Parse_CommandWithArgs()
    {
        var cmd = CommandParser.Parse("/search Друзі 1994");
        Assert.NotNull(cmd);
        Assert.Equal("search", cmd.Command);
        Assert.Equal("Друзі 1994", cmd.Args);
    }

    [Fact]
    public void Parse_StripsBotUsernameSuffix()
    {
        var cmd = CommandParser.Parse("/similar@my_kino_bot The Office; Breaking Bad");
        Assert.NotNull(cmd);
        Assert.Equal("similar", cmd.Command);
        Assert.Equal("The Office; Breaking Bad", cmd.Args);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var cmd = CommandParser.Parse("/SEARCH Breaking Bad");
        Assert.NotNull(cmd);
        Assert.Equal("search", cmd.Command);
        Assert.Equal("Breaking Bad", cmd.Args);
    }

    [Fact]
    public void Parse_CollapsesExtraSpacesInArgs()
    {
        var cmd = CommandParser.Parse("/text   легкий  ситком");
        Assert.NotNull(cmd);
        Assert.Equal("легкий  ситком", cmd.Args); // лишні пробіли всередині аргументів не рушають
    }
}

public class SimilarSeedsParserTests
{
    [Fact]
    public void Parse_OneSeed()
    {
        var seeds = SimilarSeedsParser.Parse("The Office");
        Assert.Equal(new[] { "The Office" }, seeds);
    }

    [Fact]
    public void Parse_TwoSeeds()
    {
        var seeds = SimilarSeedsParser.Parse("The Office; Breaking Bad");
        Assert.Equal(new[] { "The Office", "Breaking Bad" }, seeds);
    }

    [Fact]
    public void Parse_ThreePlusSeeds()
    {
        var seeds = SimilarSeedsParser.Parse("Друзі; Твін Пікс; Офіс; Fargo");
        Assert.Equal(new[] { "Друзі", "Твін Пікс", "Офіс", "Fargo" }, seeds);
    }

    [Fact]
    public void Parse_ExtraSemicolonsAreIgnored()
    {
        var seeds = SimilarSeedsParser.Parse(";; Dune ;;; Blade Runner ;; ;");
        Assert.Equal(new[] { "Dune", "Blade Runner" }, seeds);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var seeds = SimilarSeedsParser.Parse("  Дюна  ;  Список Шиндлера ");
        Assert.Equal(new[] { "Дюна", "Список Шиндлера" }, seeds);
    }

    [Fact]
    public void Parse_DeduplicatesCaseInsensitive()
    {
        var seeds = SimilarSeedsParser.Parse("Dune; dune; DUNE; Se7en");
        Assert.Equal(new[] { "Dune", "Se7en" }, seeds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;")]
    public void Parse_EmptyInput_GivesNoSeeds(string? args)
        => Assert.Empty(SimilarSeedsParser.Parse(args));

    [Fact]
    public void Parse_DoesNotSplitByComma_SoTitlesWithCommasStayIntact()
    {
        var seeds = SimilarSeedsParser.Parse("The Good, the Bad and the Ugly; Fargo");
        Assert.Equal(new[] { "The Good, the Bad and the Ugly", "Fargo" }, seeds);
    }
}

public class LangParserTests
{
    [Theory]
    [InlineData("uk", "uk")]
    [InlineData("UK", "uk")]
    [InlineData("ukr", "uk")]
    [InlineData("en", "en")]
    [InlineData("EN", "en")]
    [InlineData("eng", "en")]
    [InlineData(" uk ", "uk")]
    public void Parse_Valid_ReturnsCanonicalCode(string input, string expected)
        => Assert.Equal(expected, LangParser.Parse(input));

    [Theory]
    [InlineData("ru")]
    [InlineData("RU")]
    [InlineData("fr")]
    [InlineData("de")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("укр")]
    public void Parse_Invalid_ReturnsNull(string? input)
        => Assert.Null(LangParser.Parse(input)); // російська та інші мови свідомо не підтримуються
}

public class PageParserTests
{
    [Theory]
    [InlineData("", 1)]
    [InlineData(null, 1)]
    [InlineData("abc", 1)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)]
    [InlineData("2", 2)]
    [InlineData(" 3 ", 3)]
    public void Parse_DefaultsToOne_AcceptsPositiveInts(string? args, int expected)
        => Assert.Equal(expected, PageParser.Parse(args));
}
