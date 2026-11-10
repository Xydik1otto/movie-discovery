using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Imdb;

namespace MovieDiscovery.Tests;

/// <summary>Парсер TSV IMDb datasets (FR-15): валідні рядки, \N-пропуски, пошкоджені рядки.</summary>
public class ImdbTsvTests
{
    [Fact]
    public void ParseBasics_Valid_Movie_Row()
    {
        var row = ImdbTsv.ParseBasics("tt0114369\tmovie\tSe7en\tSeven\t0\t1995\t\\N\t127\tCrime,Drama,Mystery");

        Assert.NotNull(row);
        Assert.Equal("tt0114369", row!.Tconst);
        Assert.Equal("movie", row.TitleType);
        Assert.Equal("Se7en", row.PrimaryTitle);
        Assert.Equal("Seven", row.OriginalTitle);
        Assert.False(row.IsAdult);
        Assert.Equal(1995, row.StartYear);
        Assert.Equal(127, row.RuntimeMinutes);
        Assert.Equal(3, row.Genres.Length);
    }

    [Fact]
    public void ParseBasics_NullFields_Become_Empty()
    {
        var row = ImdbTsv.ParseBasics("tt9999999\ttvSeries\tНевідома серія\t\\N\t1\t\\N\t\\N\t\\N\t\\N");

        Assert.NotNull(row);
        Assert.Equal("", row!.OriginalTitle);
        Assert.Null(row.StartYear);
        Assert.Null(row.RuntimeMinutes);
        Assert.Empty(row.Genres);
        Assert.True(row.IsAdult);
    }

    [Fact]
    public void ParseBasics_Malformed_Returns_Null()
    {
        Assert.Null(ImdbTsv.ParseBasics(null));
        Assert.Null(ImdbTsv.ParseBasics(""));
        Assert.Null(ImdbTsv.ParseBasics("tt123\tmovie\tShort\tRow\t0\t2000"));
        Assert.Null(ImdbTsv.ParseBasics("\\N\tmovie\tX\tX\t0\t2000\t\\N\t90\tAction"));
    }

    [Fact]
    public void ParseRating_Valid_And_Malformed()
    {
        var row = ImdbTsv.ParseRating("tt0114369\t8.6\t1800000");
        Assert.NotNull(row);
        Assert.Equal(8.6, row!.AverageRating, 6);
        Assert.Equal(1_800_000, row.NumVotes);

        Assert.Null(ImdbTsv.ParseRating("tt0114369\tnot-a-number\t100"));
        Assert.Null(ImdbTsv.ParseRating("tt0114369\t8.6"));
        Assert.Null(ImdbTsv.ParseRating("\\N\t8.6\t100"));
    }
}

/// <summary>Мапінг IMDb → модель каталогу: типи, жанри, popularity.</summary>
public class ImdbMapperTests
{
    private static ImdbBasicsRow Row(string type, string genres = "Sci-Fi,Crime", int? year = 1999, bool adult = false) =>
        new("tt0151804", type, "The Matrix", "Matrix", adult, year, 136, genres.Split(','));

    [Theory]
    [InlineData("movie", "movie")]
    [InlineData("tvMovie", "movie")]
    [InlineData("video", "movie")]
    [InlineData("tvSpecial", "movie")]
    [InlineData("short", "movie")]
    [InlineData("tvSeries", "series")]
    [InlineData("tvMiniSeries", "series")]
    public void MapType_Maps_Movie_And_Series_Types(string titleType, string expected) =>
        Assert.Equal(expected, ImdbMapper.MapType(titleType));

    [Theory]
    [InlineData("tvEpisode")]
    [InlineData("videoGame")]
    [InlineData("unknownType")]
    public void MapType_Skips_Episodes_Games_And_Unknown(string titleType) =>
        Assert.Null(ImdbMapper.MapType(titleType));

    [Fact]
    public void ToPatch_Skips_Excluded_MediaTypes()
    {
        var patch = ImdbMapper.ToPatch(Row("tvSeries"), null, new HashSet<string> { "movie" });
        Assert.Null(patch); // series вимкнений фільтром

        var ok = ImdbMapper.ToPatch(Row("tvSeries"), null, new HashSet<string> { "movie", "series" });
        Assert.NotNull(ok);
        Assert.Equal("series", ok!.Type);
    }

    [Fact]
    public void ToPatch_Canonical_Genres_Lowercase()
    {
        var patch = ImdbMapper.ToPatch(Row("movie", genres: "Sci-Fi,crime,Reality-TV,Film-Noir,wild-west-stuff"), null, Types("movie"));
        Assert.NotNull(patch);
        // sci-fi → science fiction (канон TMDB); crime → crime; reality-tv → reality; невідоме — як є
        Assert.Equal(new[] { "science fiction", "crime", "reality", "film noir", "wild-west-stuff" }, patch!.Genres);
    }

    [Fact]
    public void ToPatch_Rating_Votes_Popularity()
    {
        var rating = new ImdbRatingRow("tt0151804", 8.7, 2_000_000);
        var patch = ImdbMapper.ToPatch(Row("movie"), rating, Types("movie"));

        Assert.NotNull(patch);
        Assert.Equal(8.7, patch!.RatingImdb ?? 0, 6);
        Assert.Equal(2_000_000, patch.VotesImdb);
        // log10(2_000_000+1)*10 = 63.01 — лог-нормалізація, консистентна з TMDB-масштабом
        Assert.Equal(Math.Log10(2_000_001) * 10, patch.Popularity, 4);
    }

    [Fact]
    public void ToPatch_Without_Rating_Nulls_And_Zero_Popularity()
    {
        var patch = ImdbMapper.ToPatch(Row("movie"), null, Types("movie"));
        Assert.NotNull(patch);
        Assert.Null(patch!.RatingImdb);
        Assert.Null(patch.VotesImdb);
        Assert.Equal(0, patch.Popularity);
    }

    [Fact]
    public void ToPatch_Adult_And_NoTitle_Skip()
    {
        var patch = ImdbMapper.ToPatch(Row("movie", adult: true), null, Types("movie"));
        Assert.NotNull(patch);
        Assert.True(patch!.IsAdult);

        var noTitle = ImdbMapper.ToPatch(new ImdbBasicsRow("tt1", "movie", "\\N", "\\N", false, 2000, 90, []), null, Types("movie"));
        Assert.Null(noTitle); // без назви — рядок марний
    }

    [Fact]
    public void PopularityFromVotes_Log_Normalized()
    {
        Assert.Equal(0, ImdbMapper.PopularityFromVotes(0));
        Assert.Equal(10, ImdbMapper.PopularityFromVotes(9), 6);          // log10(10)*10
        Assert.Equal(60, ImdbMapper.PopularityFromVotes(999_999), 3);    // 1M голосів → 60
    }

    private static HashSet<string> Types(params string[] t) => [.. t];
}

/// <summary>Ідемпотентне злиття IMDb-патчів (FR-15): повторний sync оновлює, не дублює.</summary>
public class ImdbMergeTests
{
    [Fact]
    public void Resolve_Creates_When_ImdbId_Missing()
    {
        var patch = Patch("tt0114369");
        var (title, created) = ImdbUpsertCore.Resolve([], patch);

        Assert.True(created);
        Assert.Equal("tt0114369", title.ImdbId);
        Assert.Equal("movie", title.Type);
        Assert.Equal("Breaking Bad", title.TitleEn); // фіксована назва тестового патча
        Assert.Null(title.TitleUk); // uk-назви — sweet.tv/TMDB
    }

    [Fact]
    public void Second_Run_Does_Not_Duplicate()
    {
        var first = Patch("tt0114369");
        var (title, created1) = ImdbUpsertCore.Resolve([], first);
        title.Id = 42;
        Assert.True(created1);

        var second = Patch("tt0114369", rating: 8.8); // ретинк: рейтинг трохи оновився
        var (title2, created2) = ImdbUpsertCore.Resolve([title], second);

        Assert.False(created2);
        Assert.Same(title, title2);
        ImdbUpsertCore.Merge(title, second);
        Assert.Equal(42, title.Id);                 // той самий рядок
        Assert.Equal(8.8, title.RatingImdb ?? 0, 6); // оновлено
    }

    [Fact]
    public void Merge_Keeps_TitleUk_And_Type()
    {
        var target = new Title
        {
            Id = 1, Type = "series", ImdbId = "tt0903747",
            TitleEn = "Breaking Bad old", TitleUk = "Бреакінг Бед", Genres = ["drama"],
        };
        var patch = Patch("tt0903747", type: "series");

        ImdbUpsertCore.Merge(target, patch);

        Assert.Equal("Бреакінг Бед", target.TitleUk);  // uk НЕ затерто
        Assert.Equal("series", target.Type);           // type наявного НЕ змінено
        Assert.Equal("Breaking Bad", target.TitleEn);  // en — авторитетний з IMDb
        Assert.Equal(["crime", "drama", "thriller"], target.Genres);
    }

    [Fact]
    public void Merge_Twice_Is_Stable()
    {
        var target = new Title { Id = 3, Type = "movie", ImdbId = "tt0114369" };
        var patch = Patch("tt0114369");

        ImdbUpsertCore.Merge(target, patch);
        var en1 = target.TitleEn; var genres1 = target.Genres; var pop1 = target.Popularity; var year1 = target.Year;

        ImdbUpsertCore.Merge(target, patch);
        Assert.Equal(en1, target.TitleEn);
        Assert.Equal(genres1, target.Genres);  // замінено, не сконкатеновано
        Assert.Equal(pop1, target.Popularity);
        Assert.Equal(year1, target.Year);
    }

    [Fact]
    public void Merge_Does_Not_Null_Out_Rating_When_Unrated()
    {
        var target = new Title { Id = 4, Type = "movie", ImdbId = "tt1", RatingImdb = 7.5, VotesImdb = 500, Popularity = 27 };
        var unrated = new ImdbTitlePatch("tt1", "movie", "X", 2000, null, [], null, null, 0, false);

        ImdbUpsertCore.Merge(target, unrated);

        Assert.Equal(7.5, target.RatingImdb ?? 0, 6); // без оцінки — не затираємо
        Assert.Equal(500, target.VotesImdb);
        Assert.Equal(27, target.Popularity);          // без голосів — popularity не скидається
    }

    [Fact]
    public void Merge_Never_Removes_IsAdult()
    {
        var target = new Title { Id = 5, Type = "movie", ImdbId = "tt2", IsAdult = true };
        ImdbUpsertCore.Merge(target, Patch("tt2", adult: false));
        Assert.True(target.IsAdult); // is_adult не знімається заднім числом
    }

    private static ImdbTitlePatch Patch(
        string imdbId, string type = "movie", double rating = 8.6, bool adult = false) =>
        new(imdbId, type, "Breaking Bad", 2008, 49, ["crime", "drama", "thriller"], rating, 1_900_000,
            Math.Log10(1_900_001) * 10, adult);
}
