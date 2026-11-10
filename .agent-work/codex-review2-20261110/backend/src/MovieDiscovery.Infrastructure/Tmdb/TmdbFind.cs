using System.Text.Json.Serialization;

namespace MovieDiscovery.Infrastructure.Tmdb;

/// <summary>
/// Результат /find/{external_id}?external_source=imdb_id: movie_results / tv_results
/// з мінімумом полів (id достатньо для подальшого /watch/providers).
/// </summary>
public sealed class TmdbFind
{
    [JsonPropertyName("movie_results")] public List<TmdbListItem> MovieResults { get; set; } = [];
    [JsonPropertyName("tv_results")] public List<TmdbListItem> TvResults { get; set; } = [];

    /// <summary>Перший id потрібного типу (movie | series); нема → null.</summary>
    public int? FirstId(string type) => type switch
    {
        "movie" => MovieResults.Count > 0 ? MovieResults[0].Id : null,
        "series" => TvResults.Count > 0 ? TvResults[0].Id : null,
        _ => null,
    };
}
