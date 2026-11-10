using MovieDiscovery.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

/// <summary>CR-06: паритет фільтрів бота з web (MD-06) — country/runtime/status у
/// FilterDraft, ToApiFilters, клавіатурі конструктора і callback-обробці.</summary>
public class CR06FilterParityTests
{
    [Fact]
    public void FilterDraft_NewFields_MapToApiFilters()
    {
        var draft = new FilterDraft("дюна")
        {
            Country = "KR",
            RuntimeMin = 60,
            RuntimeMax = 120,
            Status = "airing",
        };
        var f = draft.ToApiFilters();
        Assert.Equal("KR", f.Country);
        Assert.Equal(60, f.MinRuntime);
        Assert.Equal(120, f.MaxRuntime);
        Assert.Equal("airing", f.Status);
        Assert.True(f.MinRating is null); // старі поля не зламані
        Assert.True(draft.HasSelection);
    }

    [Fact]
    public void FilterDraft_Empty_NewFields_NotSent()
    {
        var f = new FilterDraft("дюна").ToApiFilters();
        Assert.Null(f.Country);
        Assert.Null(f.MinRuntime);
        Assert.Null(f.MaxRuntime);
        Assert.Null(f.Status);
        Assert.False(f.IsEmpty is false && true); // тривіально; основне — null-и вище
    }

    [Fact]
    public void FilterBuilder_ContainsCountryRuntimeStatusRows()
    {
        var kb = Keyboards.FilterBuilder(new FilterDraft("дюна"), "uk");
        var data = kb.InlineKeyboard.SelectMany(r => r).Select(b => b.CallbackData).ToList();

        // країни — усі пресети присутні
        foreach (var c in Keyboards.FilterCountries)
            Assert.Contains($"fi:country:{c}", data);
        // тривалість — усі пресети
        foreach (var (min, max) in Keyboards.FilterRuntimes)
            Assert.Contains($"fi:runtime:{min}-{max}", data);
        // статус
        foreach (var s in Keyboards.FilterStatuses)
            Assert.Contains($"fi:status:{s}", data);
    }

    [Fact]
    public void FilterBuilder_MarksSelectedNewFilters()
    {
        var draft = new FilterDraft("дюна") { Country = "UA", RuntimeMin = 60, RuntimeMax = 120, Status = "finished" };
        var kb = Keyboards.FilterBuilder(draft, "uk");
        var marked = kb.InlineKeyboard
            .SelectMany(r => r)
            .Where(b => b.CallbackData is not null && b.Text.StartsWith("✓"))
            .Select(b => b.CallbackData!)
            .ToList();

        Assert.Contains("fi:country:UA", marked);
        Assert.Contains("fi:runtime:60-120", marked);
        Assert.Contains("fi:status:finished", marked);
    }
}
