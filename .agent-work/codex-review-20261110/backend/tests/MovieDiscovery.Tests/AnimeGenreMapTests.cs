using MovieDiscovery.Infrastructure.AnimeManga;

namespace MovieDiscovery.Tests;

/// <summary>Мапа жанрів аніме та форматів: канонічні en lowercase (задача 7 — без реальних HTTP).</summary>
public class AnimeGenreMapTests
{
    [Theory]
    [InlineData("Action", "action")]
    [InlineData("Adventure", "adventure")]
    [InlineData("Comedy", "comedy")]
    [InlineData("Drama", "drama")]
    [InlineData("Fantasy", "fantasy")]
    [InlineData("Horror", "horror")]
    [InlineData("Mystery", "mystery")]
    [InlineData("Romance", "romance")]
    [InlineData("Sci-Fi", "science fiction")]
    [InlineData("Sports", "sports")]
    [InlineData("Supernatural", "supernatural")]
    [InlineData("Slice of Life", "slice of life")]
    [InlineData("School", "school")]
    [InlineData("Psychological", "psychological")]
    [InlineData("Suspense", "thriller")]
    [InlineData("Mecha", "mecha")]
    public void Canonical_Maps_Known_Anime_Genres(string raw, string expected)
    {
        Assert.Equal(expected, AnimeGenres.Canonical(raw));
    }

    [Fact]
    public void Canonical_Unknown_Passes_Through_Lowercase()
    {
        Assert.Equal("isekai", AnimeGenres.Canonical("Isekai"));
        Assert.Equal("award winning", AnimeGenres.Canonical("Award Winning"));
    }

    [Theory]
    [InlineData("Hentai")]
    [InlineData("Erotica")]
    [InlineData(null)]
    [InlineData("  ")]
    public void Canonical_Rejects_Adult_And_Empty(string? raw)
    {
        Assert.Null(AnimeGenres.Canonical(raw));
    }

    [Fact]
    public void NormalizeToEn_Known_Only()
    {
        Assert.Equal("science fiction", AnimeGenres.NormalizeToEn("sci-fi"));
        Assert.Equal("school", AnimeGenres.NormalizeToEn("School"));
        Assert.Null(AnimeGenres.NormalizeToEn("isekai")); // фільтри — лише впізнані значення
    }

    [Theory]
    [InlineData("TV", "tv")]
    [InlineData("Movie", "movie")]
    [InlineData("OVA", "ova")]
    [InlineData("ONA", "ona")]
    [InlineData("TV Special", "special")]
    [InlineData("Manga", "manga")]
    [InlineData("Manhwa", "manhwa")]
    [InlineData("One-shot", "one_shot")]
    [InlineData("Light Novel", "novel")]
    [InlineData("TV_SHORT", "tv")]
    [InlineData("ONE_SHOT", "one_shot")]
    public void Format_Normalizes_To_Canonical_Set(string raw, string expected)
    {
        Assert.Equal(expected, AnimeFormats.Normalize(raw));
    }

    [Theory]
    [InlineData("Music")]
    [InlineData("Doujin")]
    [InlineData("Manhua")]
    [InlineData(null)]
    [InlineData("")]
    public void Format_Unknown_Is_Null(string? raw)
    {
        Assert.Null(AnimeFormats.Normalize(raw));
        Assert.Equal(9, AnimeFormats.Valid.Length);
    }
}
