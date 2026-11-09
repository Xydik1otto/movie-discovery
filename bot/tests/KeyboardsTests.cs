using System.Text;
using MovieDiscovery.Bot;
using MovieDiscovery.Bot.Api;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

public class KeyboardsTests
{
    private static IEnumerable<string> Data(InlineKeyboardMarkup markup)
        => markup.InlineKeyboard.SelectMany(r => r).Select(b => b.CallbackData ?? string.Empty);

    private static TitleCard Card(int id, int? tmdbId = 42) => new() { Id = id, TmdbId = tmdbId, Title = "T" };

    [Fact]
    public void ResultsActions_UsesInternalIdInCallbacks()
    {
        var markup = Keyboards.ResultsActions([Card(777001)], 1, hasPrev: false, hasNext: false, Strings.Uk);

        var data = Data(markup).ToList();
        Assert.Contains("wl:777001", data);
        Assert.Contains("wd:777001", data);
        Assert.DoesNotContain(data, d => d.Contains("tmdb") || d.StartsWith("wl:42"));
    }

    [Fact]
    public void ResultsActions_NonPositiveId_RowIsOmitted()
    {
        var markup = Keyboards.ResultsActions([Card(0), Card(-3)], 1, hasPrev: false, hasNext: false, Strings.Uk);

        Assert.Empty(Data(markup));
    }

    [Fact]
    public void ResultsActions_MixedRows_OnlyValidGetButtons()
    {
        var markup = Keyboards.ResultsActions([Card(11), Card(0), Card(12)], 1, hasPrev: false, hasNext: false, Strings.Uk);

        var data = Data(markup).ToList();
        Assert.Contains("wl:11", data);
        Assert.Contains("wd:11", data);
        Assert.Contains("wl:12", data);
        Assert.DoesNotContain(data, d => d == "wl:0" || d == "wd:0");
    }

    /// <summary>Ліміт Telegram: callback_data ≤ 64 байтів на КОЖНІЙ кнопці кожної клавіатури.</summary>
    [Fact]
    public void AllKeyboards_CallbackDataWithinTelegramLimit()
    {
        var bigId = int.MaxValue;
        var fullDraft = new FilterDraft("very long search query with many words here")
        {
            Type = "movie",
            Genres = Keyboards.FilterGenres,
            YearFrom = 2000,
            YearTo = 2009,
            MinRating = 8,
        };

        var keyboards = new[]
        {
            Keyboards.MainMenu(Strings.Uk),
            Keyboards.MainMenu(Strings.En),
            Keyboards.ResultsActions(
                Enumerable.Range(1, 10).Select(_ => Card(bigId)).ToList(), 91, hasPrev: true, hasNext: true, Strings.Uk),
            Keyboards.ListPages("watchlist", 2, 5, Strings.En),
            Keyboards.FilterBuilder(new FilterDraft("x"), Strings.Uk),
            Keyboards.FilterBuilder(fullDraft, Strings.En),
        };

        foreach (var markup in keyboards)
        {
            foreach (var d in Data(markup))
            {
                Assert.False(string.IsNullOrEmpty(d));
                Assert.True(Encoding.UTF8.GetByteCount(d) <= 64, $"callback > 64 bytes: {d}");
            }
        }
    }

    [Fact]
    public void FilterDraft_ToggleGenre_AddsAndRemoves()
    {
        var draft = new FilterDraft("q");
        var with = draft.ToggleGenre("comedy");
        Assert.Equal(["comedy"], with.Genres);

        var toggledBack = with.ToggleGenre("comedy");
        Assert.Empty(toggledBack.Genres);
    }

    [Fact]
    public void FilterDraft_ToApiFilters_MapsSelection()
    {
        var draft = new FilterDraft("q")
        {
            Type = "series",
            Genres = ["comedy", "drama"],
            YearFrom = 2000,
            YearTo = 2009,
            MinRating = 7,
        };

        var f = draft.ToApiFilters();
        Assert.Equal("series", f.Type);
        Assert.Equal(2, f.Genres!.Count);
        Assert.Equal(2000, f.YearFrom);
        Assert.Equal(2009, f.YearTo);
        Assert.Equal(7, f.MinRating);
        Assert.False(f.IsEmpty);
    }

    [Fact]
    public void FilterDraft_Empty_MapsToNullGenresAndIsEmpty()
    {
        var f = new FilterDraft("q").ToApiFilters();
        Assert.Null(f.Type);
        Assert.Null(f.Genres);
        Assert.True(f.IsEmpty);
    }

    [Theory]
    [InlineData("до 2000", "fi.year.past")]
    [InlineData("2000–2009", "fi.year.2000s")]
    [InlineData("2010–2019", "fi.year.2010s")]
    [InlineData("2020+", "fi.year.2020s")]
    public void YearPresets_LabelsExist(string expectedUk, string key)
        => Assert.Equal(expectedUk, Strings.S(Strings.Uk, key));
}
