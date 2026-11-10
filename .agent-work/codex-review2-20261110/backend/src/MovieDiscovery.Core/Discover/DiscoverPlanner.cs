namespace MovieDiscovery.Core.Discover;

/// <summary>
/// Збірка видачі discover (FR-18): мікс exploit/safe/explore (70/20/10) + diversity —
/// не більше MaxPerGenre тайтлів одного жанру в одній видачі; недобір добирається з інших пулів.
/// Чистий, без I/O.
/// </summary>
public static class DiscoverPlanner
{
    public sealed record Pick(DiscoverCandidate Item, double Score, string Pool); // exploit | safe | explore

    public const string PoolExploit = "exploit";
    public const string PoolSafe = "safe";
    public const string PoolExplore = "explore";

    public static List<Pick> Build(
        IReadOnlyList<(DiscoverCandidate Item, double Score)> exploit,
        IReadOnlyList<(DiscoverCandidate Item, double Score)> safe,
        IReadOnlyList<(DiscoverCandidate Item, double Score)> explore,
        int count,
        DiscoverOptions options)
    {
        var exploitN = (int)Math.Round(count * options.ExploitShare);
        var safeN = (int)Math.Round(count * options.SafeShare);
        var exploreN = Math.Max(0, count - exploitN - safeN);

        var result = new List<Pick>(count);
        var genreCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        Take(explore, exploreN, PoolExplore);
        Take(safe, safeN, PoolSafe);
        Take(exploit, exploitN, PoolExploit);

        // Добір до повної видачі з залишків пулів (exploit → safe → explore).
        // Diversity — жорстке правило (PLAN FR-18): якщо пулам не хватає різноманіття, видача буде меншою за count.
        foreach (var (pool, name) in new[] { (exploit, PoolExploit), (safe, PoolSafe), (explore, PoolExplore) })
            if (result.Count < count)
                Take(pool, count - result.Count, name);

        return result;

        bool CanAdd(DiscoverCandidate item) =>
            item.Genres.All(g => genreCount.GetValueOrDefault(g) < options.MaxPerGenre);

        void Take(IReadOnlyList<(DiscoverCandidate Item, double Score)> pool, int take, string poolName)
        {
            var taken = 0;
            foreach (var (item, score) in pool)
            {
                if (result.Count >= count || taken >= take)
                    break;
                if (result.Any(p => p.Item.Id == item.Id))
                    continue;
                if (!CanAdd(item))
                    continue;
                result.Add(new Pick(item, score, poolName));
                taken++;
                foreach (var g in item.Genres)
                    genreCount[g] = genreCount.GetValueOrDefault(g) + 1;
            }
        }
    }
}
