using MovieDiscovery.Core.Text;

namespace MovieDiscovery.Tests;

public class KeywordExtractorTests
{
    [Fact]
    public void Extract_Ukrainian_Text()
    {
        var signals = KeywordExtractor.Extract("щось легке, як ситком, але з детективним сюжетом");

        Assert.Contains("comedy", signals.Genres);
        Assert.Contains("mystery", signals.Genres);
        Assert.Contains("sitcom", signals.Keywords);
        Assert.Contains("feel-good", signals.Keywords);
    }

    [Fact]
    public void Extract_English_Text()
    {
        var signals = KeywordExtractor.Extract("a dark and gritty crime thriller with a mind-bending twist");

        Assert.Contains("thriller", signals.Genres);
        Assert.Contains("crime", signals.Genres);
        Assert.Contains("dark", signals.Keywords);
        Assert.Contains("mind-bending", signals.Keywords);
    }

    [Fact]
    public void Extract_Empty_Text_IsEmpty()
    {
        var signals = KeywordExtractor.Extract("   ");
        Assert.True(signals.IsEmpty);
    }

    [Fact]
    public void Extract_No_Duplicates()
    {
        var signals = KeywordExtractor.Extract("комедія і ще раз комедія, comedy comedy");
        Assert.Equal(1, signals.Genres.Count(g => g == "comedy"));
    }

    [Fact]
    public void Extract_Salient_Words_For_Db_Search()
    {
        var signals = KeywordExtractor.Extract("хочу щось про шахраїв та пограбування банку");
        Assert.Contains("шахраїв", signals.Keywords);
        Assert.Contains("пограбування", signals.Keywords);
        Assert.DoesNotContain("хочу", signals.Keywords); // стоп-слово
    }
}
