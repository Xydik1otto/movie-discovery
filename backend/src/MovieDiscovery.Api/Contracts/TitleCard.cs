using System.Text.Json;
using MovieDiscovery.Core.Similarity;

namespace MovieDiscovery.Api.Contracts;

/// <summary>Форма TitleCard (PLAN.md) — використовується всюди.</summary>
public class TitleCard
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public string Type { get; set; } = "movie";
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Countries { get; set; } = [];
    public double? RatingTmdb { get; set; }
    public double? RatingImdb { get; set; }
    public string? PosterUrl { get; set; }
    public int? RuntimeMin { get; set; }
    public int? Seasons { get; set; }
    public string? Overview { get; set; }
}

/// <summary>Елемент відповіді схожості: TitleCard + score + reasons (1–3 локалізовані рядки).</summary>
public sealed class SimilarItem : TitleCard
{
    public double Score { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed record ProviderInfo(string Provider, string Kind, string? Url, string Source);

/// <summary>Картка тайтла з повними деталями.</summary>
public sealed class TitleDetailResponse : TitleCard
{
    public List<string> Actors { get; set; } = [];
    public string? Director { get; set; }
    public List<string> Keywords { get; set; } = [];
    public string? OverviewFull { get; set; }
    public string? BackdropUrl { get; set; }
    public string? TrailerYtId { get; set; }
    public List<ProviderInfo> Providers { get; set; } = [];
    public string? Status { get; set; }
    public string? OriginalLanguage { get; set; }
}

/// <summary>Мапінг Title → TitleCard з локалізацією uk/en (fallback en) і жанрів (en→uk).</summary>
public static class TitleMapper
{
    public static TitleCard ToCard(Core.Domain.Title t, string lang)
    {
        var uk = Core.Similarity.SimilarityTexts.IsUk(lang);
        return new TitleCard
        {
            Id = t.Id,
            TmdbId = t.TmdbId,
            Type = t.Type,
            Title = uk ? (FirstNonEmpty(t.TitleUk) ?? t.TitleEn) : t.TitleEn,
            OriginalTitle = t.TitleEn,
            Year = t.Year,
            Genres = uk ? t.Genres.Select(Infrastructure.Tmdb.TmdbGenres.ToUk).ToList() : [.. t.Genres],
            Countries = [.. t.Countries],
            RatingTmdb = t.RatingTmdb,
            RatingImdb = t.RatingImdb,
            PosterUrl = ImgUrl(t.PosterPath, "w500"),
            RuntimeMin = t.RuntimeMin,
            Seasons = t.Seasons,
            Overview = uk ? (FirstNonEmpty(t.OverviewUk) ?? t.OverviewEn) : t.OverviewEn,
        };
    }

    public static string? ImgUrl(string? path, string size) =>
        string.IsNullOrWhiteSpace(path) ? null : $"https://image.tmdb.org/t/p/{size}{path}";

    private static string? FirstNonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
