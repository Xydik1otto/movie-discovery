using MovieDiscovery.Core.Taste;

namespace MovieDiscovery.Core.Discover;

/// <summary>
/// Чистий скоринг кандидата discover (FR-18), БЕЗ I/O — тестопридатний.
/// score = Σ(вага спільного жанру) × Jaccard(candidate.genres ∪ genres профілю)
///       + вага ери кандидата (декада) + сума ваг спільних акторів
///       + rating_norm + popularity_norm − штраф негативних (cooldown) жанрів.
/// Порожній профіль → чесний базовий скоринг на popularity+rating («до квізу — popularity+safe»).
/// </summary>
public sealed class DiscoverScorer(DiscoverOptions? options = null)
{
    public DiscoverOptions Options { get; } = options ?? new();

    public double Score(DiscoverCandidate candidate, TasteProfileData profile)
    {
        double score = 0;

        var sharedGenres = candidate.Genres.Where(profile.Genres.ContainsKey).ToArray();
        if (sharedGenres.Length > 0)
        {
            var union = candidate.Genres.Union(profile.Genres.Keys).Count();
            var jaccard = (double)sharedGenres.Length / union;
            score += Options.GenreWeight * jaccard * sharedGenres.Sum(g => profile.Genres[g]);
        }

        var negGenres = candidate.Genres.Where(profile.NegativeGenres.ContainsKey).ToArray();
        if (negGenres.Length > 0)
        {
            var union = candidate.Genres.Union(profile.NegativeGenres.Keys).Count();
            var jaccard = (double)negGenres.Length / union;
            score -= Options.NegativePenaltyWeight * jaccard * negGenres.Sum(g => profile.NegativeGenres[g]);
        }

        var decade = DecadeOf(candidate.Year);
        if (decade is not null && profile.Era.TryGetValue(decade, out var eraWeight))
            score += Options.EraWeight * eraWeight;

        score += Options.ActorWeight *
                 candidate.Actors.Where(profile.Actors.ContainsKey).Sum(a => profile.Actors[a]);

        var rating = candidate.RatingImdb ?? candidate.RatingTmdb ?? 5.0;
        score += Options.RatingNormWeight * (rating / 10.0);
        score += Options.PopularityNormWeight * (candidate.Popularity / (candidate.Popularity + 10.0));

        return score;
    }

    /// <summary>Рік → ключ декади ("1994" → "1990s"); без року → null.</summary>
    public static string? DecadeOf(int? year) =>
        year is { } y ? $"{y / 10 * 10}s" : null;
}
