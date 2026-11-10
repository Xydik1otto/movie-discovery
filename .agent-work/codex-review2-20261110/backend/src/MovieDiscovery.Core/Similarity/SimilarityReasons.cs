namespace MovieDiscovery.Core.Similarity;

/// <summary>
/// Причини схожості у машинних кодах (FR-21): спільні жанри shared_genres:&lt;genre&gt;,
/// спільні актори shared_actor:&lt;ім'я&gt;, та сама декада decade:&lt;YYYY0&gt;s.
/// Коди англійською (жанри — канонічні en lowercase) — фронт локалізує.
/// Ліміти: жанри ≤ 3, актори ≤ 2; порядок — від найважливішого (жанри → актори → декада).
/// </summary>
public static class SimilarityReasons
{
    public const int MaxGenres = 3;
    public const int MaxActors = 2;

    /// <summary>
    /// Коди причин для пари «еталон-профіль ↔ кандидат».
    /// seedYear — рік еталона (для multi-seed — усереднений рік агрегата; null — код декади не видається).
    /// </summary>
    public static List<string> Build(SimilarityScore score, int? candidateYear, int? seedYear)
    {
        var codes = new List<string>();

        // Спільні жанри (движок уже відсортував за вагою еталона і обрізав до 3).
        foreach (var c in score.Contributions)
        {
            if (c.Signal != "genres" || c.Items.Count == 0) continue;
            codes.AddRange(c.Items
                .Take(MaxGenres)
                .Select(g => "shared_genres:" + g.Trim().ToLowerInvariant()));
        }

        // Спільні актори (Items credits мають префікси "actor:" / "director:").
        foreach (var c in score.Contributions)
        {
            if (c.Signal != "credits") continue;
            codes.AddRange(c.Items
                .Where(i => i.StartsWith("actor:", StringComparison.Ordinal))
                .Select(i => "shared_actor:" + i["actor:".Length..])
                .Take(MaxActors));
        }

        // Та сама декада: 1994 і 1996 → "decade:1990s".
        if (candidateYear is int cy && seedYear is int sy)
        {
            var decade = cy / 10 * 10;
            if (decade == sy / 10 * 10)
                codes.Add($"decade:{decade}s");
        }

        return codes;
    }
}
