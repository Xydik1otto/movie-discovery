using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Tmdb;

/// <summary>Мапінг відповідей TMDB у сутності titles/providers (upsert-джерело).</summary>
public static class TmdbMapper
{
    /// <summary>Оновити Title із en-резюме + uk-оверлеєм (назва/опис uk там, де є переклад).</summary>
    public static void ApplyMovieSummary(Title t, TmdbMovieSummary en, TmdbMovieSummary? uk)
    {
        t.TmdbId = en.Id;
        t.TitleEn = NonEmpty(en.Title) ?? NonEmpty(en.OriginalTitle) ?? t.TitleEn ?? "";
        if (uk is not null && NonEmpty(uk.Title) is { } ukTitle) t.TitleUk = ukTitle;
        if (NonEmpty(en.Overview) is { } enOverview) t.OverviewEn = enOverview;
        if (uk is not null && NonEmpty(uk.Overview) is { } ukOverview) t.OverviewUk = ukOverview;
        t.Year = ParseYear(en.ReleaseDate) ?? t.Year;
        t.RatingTmdb = en.VoteCount > 0 ? en.VoteAverage : t.RatingTmdb;
        t.Popularity = en.Popularity;
        t.PosterPath = NonEmpty(en.PosterPath) ?? t.PosterPath;
        t.BackdropPath = NonEmpty(en.BackdropPath) ?? t.BackdropPath;
        t.OriginalLanguage = NonEmpty(en.OriginalLanguage) ?? t.OriginalLanguage;
        t.Genres = GenreNames(en.GenreIds);
        t.UpdatedAt = DateTime.UtcNow;
    }

    public static void ApplyTvSummary(Title t, TmdbTvSummary en, TmdbTvSummary? uk)
    {
        t.TmdbId = en.Id;
        t.TitleEn = NonEmpty(en.Name) ?? NonEmpty(en.OriginalName) ?? t.TitleEn ?? "";
        if (uk is not null && NonEmpty(uk.Name) is { } ukName) t.TitleUk = ukName;
        if (NonEmpty(en.Overview) is { } enOverview) t.OverviewEn = enOverview;
        if (uk is not null && NonEmpty(uk.Overview) is { } ukOverview) t.OverviewUk = ukOverview;
        t.Year = ParseYear(en.FirstAirDate) ?? t.Year;
        t.RatingTmdb = en.VoteCount > 0 ? en.VoteAverage : t.RatingTmdb;
        t.Popularity = en.Popularity;
        t.PosterPath = NonEmpty(en.PosterPath) ?? t.PosterPath;
        t.BackdropPath = NonEmpty(en.BackdropPath) ?? t.BackdropPath;
        t.OriginalLanguage = NonEmpty(en.OriginalLanguage) ?? t.OriginalLanguage;
        t.Genres = GenreNames(en.GenreIds);
        t.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Збагачення деталями: країни, статус, тривалість, credits, keywords, трейлер.</summary>
    public static void ApplyMovieDetails(Title t, TmdbMovieDetails en, TmdbMovieDetails? uk)
    {
        ApplyMovieSummary(t, en, uk);
        t.Countries = en.ProductionCountries.Select(c => c.Iso).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        t.RuntimeMin = en.Runtime > 0 ? en.Runtime : t.RuntimeMin;
        t.Status = MapMovieStatus(en.Status) ?? t.Status;
        var credits = en.Credits;
        if (credits is not null)
        {
            var director = credits.Crew.FirstOrDefault(c => c.Job == "Director")?.Name;
            if (NonEmpty(director) is { } d) t.Director = d;
            var actors = credits.Cast.OrderBy(c => c.Order).Take(8).Select(c => c.Name).ToArray();
            if (actors.Length > 0) t.Actors = actors;
        }
        if (en.Keywords?.Keywords is { Count: > 0 } kws)
            t.Keywords = kws.Select(k => k.Name.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trailer = PickTrailer(uk?.Videos, en.Videos);
        if (trailer is not null) t.TrailerYtId = trailer;
    }

    public static void ApplyTvDetails(Title t, TmdbTvDetails en, TmdbTvDetails? uk)
    {
        ApplyTvSummary(t, en, uk);
        t.Countries = en.ProductionCountries.Select(c => c.Iso).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        t.EpisodeRuntimeMin = en.EpisodeRunTime.Count > 0 ? en.EpisodeRunTime[0] : t.EpisodeRuntimeMin;
        t.Seasons = en.NumberOfSeasons > 0 ? en.NumberOfSeasons : t.Seasons;
        t.Status = MapTvStatus(en.Status) ?? t.Status;
        var credits = en.Credits;
        if (credits is not null)
        {
            var director = en.CreatedBy.FirstOrDefault()?.Name
                ?? credits.Crew.FirstOrDefault(c => c.Job is "Director" or "Series Director")?.Name;
            if (NonEmpty(director) is { } d) t.Director = d;
            var actors = credits.Cast.OrderBy(c => c.Order).Take(8).Select(c => c.Name).ToArray();
            if (actors.Length > 0) t.Actors = actors;
        }
        if (en.Keywords?.Keywords is { Count: > 0 } kws)
            t.Keywords = kws.Select(k => k.Name.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trailer = PickTrailer(uk?.Videos, en.Videos);
        if (trailer is not null) t.TrailerYtId = trailer;
    }

    /// <summary>Трейлер YouTube: uk перший, інакше en official.</summary>
    public static string? PickTrailer(TmdbVideos? ukVideos, TmdbVideos? enVideos)
    {
        var uk = BestTrailer(ukVideos);
        if (uk is not null) return uk.Key;
        var en = BestTrailer(enVideos);
        return en?.Key;
    }

    private static TmdbVideo? BestTrailer(TmdbVideos? videos)
    {
        if (videos?.Results is not { Count: > 0 } list) return null;
        return list
            .Where(v => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Official)
            .ThenBy(v => v.PublishedAt)
            .FirstOrDefault();
    }

    /// <summary>Провайдери регіону з watch/providers (дані JustWatch через TMDB).</summary>
    public static List<Provider> ExtractProviders(int titleId, TmdbWatchProviders? wp, string region)
    {
        var result = new List<Provider>();
        if (wp?.Results is null || !wp.Results.TryGetValue(region, out var r) || r is null) return result;

        var kinds = new (List<TmdbProviderEntry>?, string)[] { (r.Flatrate, "stream"), (r.Rent, "rent"), (r.Buy, "buy"), (r.Free, "free") };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (list, kind) in kinds)
        {
            if (list is null) continue;
            foreach (var p in list.OrderBy(p => p.DisplayPriority))
            {
                if (string.IsNullOrWhiteSpace(p.ProviderName)) continue;
                var key = $"{p.ProviderName}|{kind}";
                if (!seen.Add(key)) continue;
                result.Add(new Provider
                {
                    TitleId = titleId,
                    ProviderName = p.ProviderName.Trim(),
                    Kind = kind,
                    Url = r.Link,
                    Source = "justwatch",
                    Region = region,
                    UpdatedAt = DateTime.UtcNow,
                });
            }
        }
        return result;
    }

    public static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return null;
        return int.TryParse(date[..4], out var y) && y is > 1880 and < 2100 ? y : null;
    }

    private static string[] GenreNames(List<int> ids) =>
        ids.Select(TmdbGenres.EnName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? MapMovieStatus(string? status) => status switch
    {
        "Released" => "released",
        _ => null,
    };

    private static string? MapTvStatus(string? status) => status switch
    {
        "Returning Series" => "returning",
        "Ended" => "ended",
        "Canceled" or "Cancelled" => "canceled",
        _ => null,
    };

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
