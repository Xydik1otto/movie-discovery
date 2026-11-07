using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Tests;

/// <summary>Парсинг TMDB-відповідей — зафіксовані JSON-семпли (тест-фікстури, не реальні дані).</summary>
public class TmdbMappingTests
{
    private const string UkDetailsJson = """
        {
          "id": 12345,
          "title": "Тестовий фільм",
          "overview": "Український опис фільму.",
          "release_date": "2019-05-17",
          "genre_ids": [28, 53],
          "vote_average": 7.8,
          "vote_count": 1200,
          "popularity": 55.5,
          "poster_path": "/uk-poster.jpg",
          "videos": { "results": [ { "key": "ukTrailerKey", "name": "Український трейлер", "site": "YouTube", "type": "Trailer", "official": true, "iso_639_1": "uk" } ] }
        }
        """;

    private const string EnDetailsJson = """
        {
          "id": 12345,
          "title": "Test Movie",
          "original_title": "Test Movie",
          "overview": "An English overview of the movie.",
          "release_date": "2019-05-17",
          "genre_ids": [28, 53],
          "vote_average": 7.8,
          "vote_count": 1200,
          "popularity": 55.5,
          "poster_path": "/en-poster.jpg",
          "backdrop_path": "/backdrop.jpg",
          "original_language": "en",
          "runtime": 142,
          "status": "Released",
          "production_countries": [ { "iso_3166_1": "US", "name": "United States" } ],
          "genres": [ { "id": 28, "name": "Action" } ],
          "credits": {
            "cast": [
              { "name": "Actor One", "order": 0 },
              { "name": "Actor Two", "order": 1 },
              { "name": "Actor Three", "order": 2 },
              { "name": "Actor Four", "order": 3 },
              { "name": "Actor Five", "order": 4 },
              { "name": "Actor Six", "order": 5 },
              { "name": "Actor Seven", "order": 6 },
              { "name": "Actor Eight", "order": 7 },
              { "name": "Actor Nine", "order": 8 }
            ],
            "crew": [ { "name": "Jane Director", "job": "Director" }, { "name": "Producer Guy", "job": "Producer" } ]
          },
          "keywords": { "keywords": [ { "id": 1, "name": "dark comedy" }, { "id": 2, "name": "heist" } ] },
          "videos": { "results": [ { "key": "enTrailerKey", "name": "Official Trailer", "site": "YouTube", "type": "Trailer", "official": true, "iso_639_1": "en" } ] },
          "watch/providers": {
            "results": {
              "UA": {
                "link": "https://www.themoviedb.org/movie/12345/watch?locale=UA",
                "flatrate": [ { "provider_id": 8, "provider_name": "Netflix", "display_priority": 1 } ],
                "rent": [ { "provider_id": 2, "provider_name": "Apple TV", "display_priority": 2 } ],
                "buy": [ { "provider_id": 2, "provider_name": "Apple TV", "display_priority": 2 } ]
              }
            }
          },
          "similar": { "results": [ { "id": 999 }, { "id": 1000 } ] },
          "recommendations": { "results": [ { "id": 1001 } ] }
        }
        """;

    private const string TvDetailsJson = """
        {
          "id": 555,
          "name": "Test Show",
          "original_name": "Test Show",
          "overview": "An English overview of the show.",
          "first_air_date": "2005-03-24",
          "genre_ids": [35],
          "vote_average": 8.5,
          "vote_count": 500,
          "popularity": 90.1,
          "poster_path": "/tv.jpg",
          "backdrop_path": "/tvbd.jpg",
          "original_language": "en",
          "episode_run_time": [22],
          "number_of_seasons": 9,
          "status": "Ended",
          "production_countries": [ { "iso_3166_1": "US", "name": "United States" } ],
          "created_by": [ { "name": "Greg Showrunner" } ],
          "credits": { "cast": [ { "name": "Star A", "order": 0 } ], "crew": [] },
          "keywords": { "results": [ { "id": 5, "name": "sitcom" } ] },
          "videos": { "results": [] },
          "watch/providers": {
            "results": {
              "UA": {
                "link": "https://www.themoviedb.org/tv/555/watch?locale=UA",
                "free": [ { "provider_id": 100, "provider_name": "1+1 video", "display_priority": 1 } ]
              }
            }
          }
        }
        """;

    private const string PopularPageJson = """
        {
          "page": 1,
          "results": [
            { "id": 1, "title": "Movie A", "overview": "Ov A", "release_date": "2020-01-01", "genre_ids": [28], "vote_average": 6.5, "vote_count": 100, "popularity": 100.5, "poster_path": "/a.jpg", "original_language": "en" },
            { "id": 2, "title": "Movie B", "overview": "Ov B", "release_date": "", "genre_ids": [], "vote_average": 0, "vote_count": 0, "popularity": 50.0, "poster_path": null, "original_language": "ja" }
          ],
          "total_pages": 500,
          "total_results": 10000
        }
        """;

    private const string PopularPageUkJson = """
        {
          "page": 1,
          "results": [
            { "id": 1, "title": "Фільм А", "overview": "Опис А", "release_date": "2020-01-01", "genre_ids": [28], "vote_average": 6.5, "vote_count": 100, "popularity": 100.5, "poster_path": "/a.jpg", "original_language": "en" }
          ],
          "total_pages": 500,
          "total_results": 10000
        }
        """;

    [Fact]
    public void MovieDetails_Parses_And_Maps()
    {
        var en = System.Text.Json.JsonSerializer.Deserialize<TmdbMovieDetails>(EnDetailsJson, TmdbJson.Options);
        var uk = System.Text.Json.JsonSerializer.Deserialize<TmdbMovieDetails>(UkDetailsJson, TmdbJson.Options);
        Assert.NotNull(en);

        var title = new Core.Domain.Title { Type = "movie" };
        TmdbMapper.ApplyMovieDetails(title, en!, uk);

        Assert.Equal(12345, title.TmdbId);
        Assert.Equal("Test Movie", title.TitleEn);
        Assert.Equal("Тестовий фільм", title.TitleUk);
        Assert.Equal("Український опис фільму.", title.OverviewUk);
        Assert.Equal("An English overview of the movie.", title.OverviewEn);
        Assert.Equal(2019, title.Year);
        Assert.Equal(142, title.RuntimeMin);
        Assert.Equal("released", title.Status);
        Assert.Equal(["US"], title.Countries);
        Assert.Equal("Jane Director", title.Director);
        Assert.Equal(8, title.Actors.Length); // топ-8 акторів
        Assert.Equal("Actor One", title.Actors[0]);
        Assert.Contains("dark comedy", title.Keywords);
        Assert.Contains("heist", title.Keywords);
        Assert.Equal("ukTrailerKey", title.TrailerYtId); // uk перший
        Assert.Equal(7.8, title.RatingTmdb ?? 0, 6);
        Assert.Equal(55.5, title.Popularity, 6);
        // жанри з genre_ids → канонічні en назви
        Assert.Contains("Action", title.Genres);
        Assert.Contains("Thriller", title.Genres);
    }

    [Fact]
    public void MovieDetails_Trailer_Fallback_To_En_When_Uk_Empty()
    {
        var en = System.Text.Json.JsonSerializer.Deserialize<TmdbMovieDetails>(EnDetailsJson, TmdbJson.Options)!;
        var ukNoVideos = new TmdbMovieDetails { Videos = new TmdbVideos { Results = [] } };

        var title = new Core.Domain.Title { Type = "movie" };
        TmdbMapper.ApplyMovieDetails(title, en, ukNoVideos);

        Assert.Equal("enTrailerKey", title.TrailerYtId);
    }

    [Fact]
    public void Providers_Extract_Region_UA_With_Dedupe()
    {
        var en = System.Text.Json.JsonSerializer.Deserialize<TmdbMovieDetails>(EnDetailsJson, TmdbJson.Options)!;
        var providers = TmdbMapper.ExtractProviders(7, en.WatchProviders, "UA");

        // Apple TV у rent+buy → дублікат (provider+kind різні — обидва легальні)
        Assert.Contains(providers, p => p.ProviderName == "Netflix" && p.Kind == "stream");
        Assert.Contains(providers, p => p.ProviderName == "Apple TV" && p.Kind == "rent");
        Assert.Contains(providers, p => p.ProviderName == "Apple TV" && p.Kind == "buy");
        Assert.All(providers, p =>
        {
            Assert.Equal("justwatch", p.Source);
            Assert.Equal("UA", p.Region);
            Assert.Equal(7, p.TitleId);
            Assert.Equal("https://www.themoviedb.org/movie/12345/watch?locale=UA", p.Url);
        });
        // нема дублікатів одного (provider, kind)
        Assert.Equal(providers.Count, providers.Select(p => (p.ProviderName, p.Kind)).Distinct().Count());
    }

    [Fact]
    public void TvDetails_Parses_And_Maps()
    {
        var en = System.Text.Json.JsonSerializer.Deserialize<TmdbTvDetails>(TvDetailsJson, TmdbJson.Options)!;

        var title = new Core.Domain.Title { Type = "series" };
        TmdbMapper.ApplyTvDetails(title, en, null);

        Assert.Equal(555, title.TmdbId);
        Assert.Equal("Test Show", title.TitleEn);
        Assert.Equal(2005, title.Year);
        Assert.Equal(22, title.EpisodeRuntimeMin);
        Assert.Equal(9, title.Seasons);
        Assert.Equal("ended", title.Status);
        Assert.Equal("Greg Showrunner", title.Director); // created_by
        Assert.Equal(["Star A"], title.Actors);
        Assert.Equal("sitcom", Assert.Single(title.Keywords));
        Assert.Null(title.TrailerYtId); // відео нема
    }

    [Fact]
    public void PopularPage_Parses_And_Maps_UkOverlay()
    {
        var page = System.Text.Json.JsonSerializer.Deserialize<TmdbPage<TmdbMovieSummary>>(PopularPageJson, TmdbJson.Options)!;
        var ukPage = System.Text.Json.JsonSerializer.Deserialize<TmdbPage<TmdbMovieSummary>>(PopularPageUkJson, TmdbJson.Options)!;

        Assert.Equal(2, page.Results.Count);
        var en1 = page.Results[0];
        var uk1 = ukPage.Results[0];

        var title = new Core.Domain.Title { Type = "movie" };
        TmdbMapper.ApplyMovieSummary(title, en1, uk1);

        Assert.Equal("Movie A", title.TitleEn);
        Assert.Equal("Фільм А", title.TitleUk);
        Assert.Equal(2020, title.Year);
        Assert.Equal(["Action"], title.Genres);

        // тайтл без голосів і дати: rating null, year не встановлюється
        var en2 = page.Results[1];
        var title2 = new Core.Domain.Title { Type = "movie" };
        TmdbMapper.ApplyMovieSummary(title2, en2, null);
        Assert.Null(title2.RatingTmdb);
        Assert.Null(title2.Year);
        Assert.Equal("Movie B", title2.TitleEn);
    }

    [Fact]
    public void Similar_And_Recommendation_Ids_Are_Parsed()
    {
        var en = System.Text.Json.JsonSerializer.Deserialize<TmdbMovieDetails>(EnDetailsJson, TmdbJson.Options)!;
        var similarIds = en.Similar?.Results.Select(r => r.Id).ToList() ?? [];
        var recIds = en.Recommendations?.Results.Select(r => r.Id).ToList() ?? [];

        Assert.Equal([999, 1000], similarIds);
        Assert.Equal([1001], recIds);
    }
}
