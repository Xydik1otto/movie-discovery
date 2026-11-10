using MovieDiscovery.Bot;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

public class StringsTests
{
    [Fact]
    public void UkAndEn_HaveIdenticalKeySets()
    {
        var uk = Strings.For(Strings.Uk).Keys.ToHashSet();
        var en = Strings.For(Strings.En).Keys.ToHashSet();
        Assert.True(uk.SetEquals(en),
            "Ключі uk та en мають збігатися. Тільки в uk: " + string.Join(", ", uk.Except(en)) +
            "; тільки в en: " + string.Join(", ", en.Except(uk)));
    }

    [Fact]
    public void S_UnknownLanguage_FallsBackToUk()
        => Assert.Equal(Strings.For(Strings.Uk)["start.greeting"], Strings.S("de", "start.greeting"));

    [Fact]
    public void S_MissingKey_ReturnsKeyItself()
        => Assert.Equal("no.such.key", Strings.S(Strings.Uk, "no.such.key"));

    [Fact]
    public void S_DefaultLanguage_IsUk()
        => Assert.Equal(Strings.For(Strings.Uk)["start.greeting"], Strings.S(null, "start.greeting"));

    [Fact]
    public void F_FormatsNumbers()
        => Assert.Equal("моя оцінка 8/10", Strings.F(Strings.Uk, "lists.myRating", 8));

    [Fact]
    public void LangSwitched_DiffersPerLanguage()
    {
        Assert.Contains("українськ", Strings.S(Strings.Uk, "lang.switched"));
        Assert.Contains("English", Strings.S(Strings.En, "lang.switched"));
    }

    /// <summary>Російської мови НЕ ПОВИННО БУТИ НІДЕ: нема літер ы/э/ъ/ё в жодному тексті.</summary>
    [Fact]
    public void NoRussianSpecificLetters_InAnyUiText()
    {
        var russianOnly = new[] { 'ы', 'э', 'ъ', 'ё', 'Ы', 'Э', 'Ъ', 'Ё' };
        foreach (var dict in new[] { Strings.For(Strings.Uk), Strings.For(Strings.En) })
        {
            foreach (var (key, value) in dict)
            {
                foreach (var ch in russianOnly)
                    Assert.True(!value.Contains(ch), $"Російська літера '{ch}' у «{key}»: {value}");
            }
        }
    }
}
