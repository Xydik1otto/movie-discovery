using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.YouTube;
namespace MovieDiscovery.Tests;

public class CodexFeatureIntegrationTests
{
    private static SearchQuery Query(bool? trailer=null, string? audio=null) =>
        new(null,null,[],null,null,null,null,null,null,null,null,"popularity",1,20,
            HasTrailer:trailer,AudioLang:audio);

    [Fact]
    public async Task CodexFeature_HasTrailer_Includes_Verified_New_Trailer_Storage()
    {
        using var db=TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title{Id=91001,Type="movie",TitleEn="Trailer fixture",Year=2000});
        db.TitleTrailers.Add(new TitleTrailer{TitleId=91001,YtId="abcdefghijk",Lang="uk",VerifiedStatus="ok"});
        await db.SaveChangesAsync();
        var result=await new SearchService(db).SearchAsync(Query(trailer:true));
        Assert.Contains(result.Items,t=>t.Id==91001);
    }

    [Fact]
    public async Task CodexFeature_AudioFilter_Includes_New_MultiLanguage_Offer()
    {
        using var db=TestDb.Create(Guid.NewGuid().ToString());
        db.Titles.Add(new Title{Id=91002,Type="movie",TitleEn="Audio fixture",Year=2000});
        db.WatchLinks.Add(new WatchLink{TitleId=91002,Provider="fixture",Kind="free",Region="UA",
            Url="https://example.test/movie/91002",IsAvailable=true,AudioLangs="uk,ru"});
        await db.SaveChangesAsync();
        var result=await new SearchService(db).SearchAsync(Query(audio:"ukr"));
        Assert.Contains(result.Items,t=>t.Id==91002);
    }
}
