namespace MovieDiscovery.Core.Domain;

/// <summary>Тайтл (фільм, серіал або телешоу) — відповідає таблиці titles.</summary>
public class Title
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public string Type { get; set; } = "movie"; // movie | series | show
    public string? TitleUk { get; set; }
    public string TitleEn { get; set; } = "";
    public string? OverviewUk { get; set; }
    public string? OverviewEn { get; set; }
    public int? Year { get; set; }
    public string[] Countries { get; set; } = [];
    public string[] Genres { get; set; } = [];
    public string[] Keywords { get; set; } = [];
    public string[] Actors { get; set; } = [];
    public string? Director { get; set; }
    public int? RuntimeMin { get; set; }
    public int? EpisodeRuntimeMin { get; set; }
    public int? Seasons { get; set; }
    public string? Status { get; set; } // released | returning | ended | canceled
    public string? OriginalLanguage { get; set; }
    public double? RatingTmdb { get; set; }
    public double? RatingImdb { get; set; }
    public int? VotesImdb { get; set; }
    public double Popularity { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? TrailerYtId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
