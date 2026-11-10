using System.Text.Json;
using System.Text.Json.Serialization;

namespace MovieDiscovery.Infrastructure.Tmdb;

// ─── Загальні DTO TMDB API (v3). Поля лише ті, що використовуємо. ───

public sealed class TmdbPage<T>
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("results")] public List<T> Results { get; set; } = [];
    [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
    [JsonPropertyName("total_results")] public int TotalResults { get; set; }
}

public sealed class TmdbGenre
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public class TmdbMovieSummary
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("original_title")] public string? OriginalTitle { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("genre_ids")] public List<int> GenreIds { get; set; } = [];
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("popularity")] public double Popularity { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    [JsonPropertyName("original_language")] public string? OriginalLanguage { get; set; }
}

public class TmdbTvSummary
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("original_name")] public string? OriginalName { get; set; }
    [JsonPropertyName("overview")] public string? Overview { get; set; }
    [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; set; }
    [JsonPropertyName("genre_ids")] public List<int> GenreIds { get; set; } = [];
    [JsonPropertyName("vote_average")] public double VoteAverage { get; set; }
    [JsonPropertyName("vote_count")] public int VoteCount { get; set; }
    [JsonPropertyName("popularity")] public double Popularity { get; set; }
    [JsonPropertyName("poster_path")] public string? PosterPath { get; set; }
    [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; set; }
    [JsonPropertyName("original_language")] public string? OriginalLanguage { get; set; }
}

public sealed class TmdbCountry
{
    [JsonPropertyName("iso_3166_1")] public string Iso { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class TmdbCastMember
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("character")] public string? Character { get; set; }
    [JsonPropertyName("order")] public int Order { get; set; }
}

public sealed class TmdbCrewMember
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("job")] public string? Job { get; set; }
    [JsonPropertyName("department")] public string? Department { get; set; }
}

public sealed class TmdbCredits
{
    [JsonPropertyName("cast")] public List<TmdbCastMember> Cast { get; set; } = [];
    [JsonPropertyName("crew")] public List<TmdbCrewMember> Crew { get; set; } = [];
}

public sealed class TmdbKeywordEntry
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class TmdbMovieKeywords
{
    [JsonPropertyName("keywords")] public List<TmdbKeywordEntry> Keywords { get; set; } = [];
}

public sealed class TmdbTvKeywords
{
    [JsonPropertyName("results")] public List<TmdbKeywordEntry> Keywords { get; set; } = [];
}

public sealed class TmdbVideo
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("official")] public bool Official { get; set; }
    [JsonPropertyName("iso_639_1")] public string? Language { get; set; }
    [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
}

public sealed class TmdbVideos
{
    [JsonPropertyName("results")] public List<TmdbVideo> Results { get; set; } = [];
}

public sealed class TmdbCreatedBy
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class TmdbProviderEntry
{
    [JsonPropertyName("provider_id")] public int ProviderId { get; set; }
    [JsonPropertyName("provider_name")] public string ProviderName { get; set; } = "";
    [JsonPropertyName("logo_path")] public string? LogoPath { get; set; }
    [JsonPropertyName("display_priority")] public int DisplayPriority { get; set; }
}

public sealed class TmdbWatchRegion
{
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonPropertyName("flatrate")] public List<TmdbProviderEntry>? Flatrate { get; set; }
    [JsonPropertyName("rent")] public List<TmdbProviderEntry>? Rent { get; set; }
    [JsonPropertyName("buy")] public List<TmdbProviderEntry>? Buy { get; set; }
    [JsonPropertyName("free")] public List<TmdbProviderEntry>? Free { get; set; }
}

public sealed class TmdbWatchProviders
{
    [JsonPropertyName("results")] public Dictionary<string, TmdbWatchRegion>? Results { get; set; }
}

public sealed class TmdbItemList<T>
{
    [JsonPropertyName("results")] public List<TmdbListItem> Results { get; set; } = [];
}

public sealed class TmdbListItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
}

public sealed class TmdbMovieDetails : TmdbMovieSummary
{
    [JsonPropertyName("runtime")] public int? Runtime { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("tagline")] public string? Tagline { get; set; }
    [JsonPropertyName("genres")] public List<TmdbGenre> Genres { get; set; } = [];
    [JsonPropertyName("production_countries")] public List<TmdbCountry> ProductionCountries { get; set; } = [];
    [JsonPropertyName("credits")] public TmdbCredits? Credits { get; set; }
    [JsonPropertyName("keywords")] public TmdbMovieKeywords? Keywords { get; set; }
    [JsonPropertyName("videos")] public TmdbVideos? Videos { get; set; }
    [JsonPropertyName("watch/providers")] public TmdbWatchProviders? WatchProviders { get; set; }
    [JsonPropertyName("similar")] public TmdbItemList<TmdbListItem>? Similar { get; set; }
    [JsonPropertyName("recommendations")] public TmdbItemList<TmdbListItem>? Recommendations { get; set; }
}

public sealed class TmdbTvDetails : TmdbTvSummary
{
    [JsonPropertyName("episode_run_time")] public List<int> EpisodeRunTime { get; set; } = [];
    [JsonPropertyName("number_of_seasons")] public int? NumberOfSeasons { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("genres")] public List<TmdbGenre> Genres { get; set; } = [];
    [JsonPropertyName("production_countries")] public List<TmdbCountry> ProductionCountries { get; set; } = [];
    [JsonPropertyName("created_by")] public List<TmdbCreatedBy> CreatedBy { get; set; } = [];
    [JsonPropertyName("credits")] public TmdbCredits? Credits { get; set; }
    [JsonPropertyName("keywords")] public TmdbTvKeywords? Keywords { get; set; }
    [JsonPropertyName("videos")] public TmdbVideos? Videos { get; set; }
    [JsonPropertyName("watch/providers")] public TmdbWatchProviders? WatchProviders { get; set; }
    [JsonPropertyName("similar")] public TmdbItemList<TmdbListItem>? Similar { get; set; }
    [JsonPropertyName("recommendations")] public TmdbItemList<TmdbListItem>? Recommendations { get; set; }
}

/// <summary>Статичні JsonSerializerOptions для розбору відповідей TMDB.</summary>
public static class TmdbJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
