using MovieDiscovery.Bot;
using MovieDiscovery.Bot.Api;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

public class RendererTests
{
    private static TitleCard Movie(int tmdbId = 456, string title = "Дюна", string overview = "Опис фільму.")
        => new()
        {
            Id = 1,
            TmdbId = tmdbId,
            Type = "movie",
            Title = title,
            Year = 2021,
            RatingTmdb = 7.8,
            RatingImdb = 8.1,
            Overview = overview,
        };

    // ---------------------------------------------------------------- блок тайтла

    [Fact]
    public void FormatTitleBlock_ContainsTitleYearRatingsLinkAndEscapedOverview()
    {
        var block = Renderer.FormatTitleBlock(1, Movie(), Strings.Uk);

        Assert.StartsWith("1. 🎬 <b>Дюна</b> (2021) · ⭐ 7.8 · IMDb 8.1", block);
        Assert.Contains("<i>Опис фільму.</i>", block);
        Assert.Contains("https://www.themoviedb.org/movie/456", block);
    }

    [Fact]
    public void FormatTitleBlock_EscapesTitleAndOverview()
    {
        var t = Movie(title: "<b>Тітуs</b>", overview: "Опис <3 & >2");
        var block = Renderer.FormatTitleBlock(1, t, Strings.Uk);

        Assert.Contains("&lt;b&gt;Тітуs&lt;/b&gt;", block);
        Assert.Contains("Опис &lt;3 &amp; &gt;2", block);
        Assert.DoesNotContain("<b>Тітуs</b>", block); // тег користувача не проліз у розмітку
    }

    [Fact]
    public void FormatTitleBlock_IncludesReasons_AndSkipsOverviewWhenAsked()
    {
        var t = Movie();
        t.Reasons = ["спільні жанри: драма, трилер", "спільний актор X", "близький рік", "четверта причина"];

        var block = Renderer.FormatTitleBlock(3, t, Strings.Uk);
        Assert.Contains("3. 🎬", block);
        Assert.Contains("🧩 спільні жанри: драма, трилер", block);
        Assert.Contains("🧩 близький рік", block);
        Assert.DoesNotContain("четверта причина", block); // reasons обмежені трьома

        var compact = Renderer.FormatTitleBlock(3, t, Strings.Uk, includeOverview: false, userRating: 8);
        Assert.DoesNotContain("<i>", compact);
        Assert.Contains("моя оцінка 8/10", compact);
    }

    [Fact]
    public void FormatTitleBlock_IncludesScoreAndSeasons()
    {
        var t = Movie();
        t.Score = 0.834;
        t.Seasons = 5;
        t.Type = "series";

        var block = Renderer.FormatTitleBlock(1, t, Strings.Uk);
        Assert.Contains("збіг 83%", block);
        Assert.Contains("сезонів: 5", block);
        Assert.Contains("https://www.themoviedb.org/tv/456", block);
    }

    [Fact]
    public void FormatTitleBlock_LongOverview_TruncatedToAbout300()
    {
        var t = Movie(overview: new string('о', 1000));
        var block = Renderer.FormatTitleBlock(1, t, Strings.Uk);
        Assert.Contains("…</i>", block);
        // опис + теги <i></i> — близько 300, точно не 1000
        Assert.True(block.Length < 600);
    }

    // ---------------------------------------------------------------- заголовки

    [Fact]
    public void FormatSearchHeader_EscapesQuery()
    {
        var header = Renderer.FormatSearchHeader("<x> & y", 2, 5, 123, Strings.Uk);
        Assert.Contains("&lt;x&gt; &amp; y", header);
        Assert.Contains("сторінка 2/5", header);
        Assert.DoesNotContain("<x>", header);
    }

    // ---------------------------------------------------------------- розрізання на чанки

    [Fact]
    public void ChunkBlocks_SingleSmallBlock_SingleChunkStartingWithHeader()
    {
        var (chunks, lastFirst) = Renderer.ChunkBlocks("H", ["BLOCK-1"]);
        Assert.Single(chunks);
        Assert.StartsWith("H", chunks[0]);
        Assert.Contains("BLOCK-1", chunks[0]);
        Assert.Equal(1, lastFirst);
    }

    [Fact]
    public void ChunkBlocks_NoBlocks_KeepsHeaderOnly()
    {
        var (chunks, lastFirst) = Renderer.ChunkBlocks("Тільки заголовок", []);
        Assert.Single(chunks);
        Assert.Equal("Тільки заголовок", chunks[0]);
        Assert.Equal(1, lastFirst);
    }

    [Fact]
    public void ChunkBlocks_ManyLongBlocks_SplitsWithinLimitsAndKeepsOrder()
    {
        const int count = 30;
        var blocks = new List<string>();
        for (var i = 1; i <= count; i++)
            blocks.Add($"{i}. " + new string('x', 500));

        var result = Renderer.ChunkBlocks("Заголовок", blocks);

        Assert.True(result.Chunks.Count > 1);
        foreach (var chunk in result.Chunks)
            Assert.True(chunk.Length <= Renderer.MaxMessageLength, $"чанк {chunk.Length} > 4096");

        // усі блоки на місці, у правильному порядку (границі чанків з'єднуємо переносом)
        var joined = string.Join("\n\n", result.Chunks);
        for (var i = 1; i <= count; i++)
            Assert.Contains($"\n{i}. " + new string('x', 500), joined);

        // останній чанк починається з блоку під номером LastChunkFirstNumber
        Assert.StartsWith($"{result.LastChunkFirstNumber}. ", result.Chunks[^1]);
        Assert.InRange(result.LastChunkFirstNumber, 2, count);
    }

    [Fact]
    public void ChunkBlocks_FirstNumberOffset_TracksLastChunkStart()
    {
        // номери в блоках ставить FormatTitleBlock; ChunkBlocks лише відстежує перший номер чанка
        var blocks = new[] { "11. A-block", "12. B-block" };
        var result = Renderer.ChunkBlocks("H", blocks, firstNumber: 11);
        Assert.Equal(11, result.LastChunkFirstNumber);
        Assert.Contains("11. A-block", result.Chunks[0]);
        Assert.Contains("12. B-block", result.Chunks[0]);
    }
}
