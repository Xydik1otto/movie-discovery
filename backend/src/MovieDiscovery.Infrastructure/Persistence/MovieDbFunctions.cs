namespace MovieDiscovery.Infrastructure.Persistence;

/// <summary>
/// Стаби SQL-функцій БД для composable LINQ-запитів (HasDbFunction у DbContext).
/// Тіло НІКОЛИ не викликається — EF перекладає виклик у SQL під час трансляції.
/// </summary>
public static class MovieDbFunctions
{
    /// <summary>
    /// actors_trgm_text(text[]) — склеює масив акторів у текст для trgm-пошуку;
    /// на ній побудований виразний GIN-індекс ix_titles_actors_trgm (міграція
    /// 20261107204126) — ILIKE по ньому замість per-element unnest не вмикає
    /// seq-scan по 2.86 млн рядків.
    /// </summary>
    public static string? ActorsTrgmText(string[] actors) =>
        throw new InvalidOperationException("Лише для SQL-трансляції EF (HasDbFunction).");

    /// <summary>
    /// title_search_match(en, uk, ru, director, actors, q) — FTS-предикат двофазного пошуку
    /// (Фаза A в SearchService.SearchAsync): title_search_fts(5 полів) @@ title_search_tsquery(q).
    /// На виразі title_search_fts(title_en, title_uk, title_ru, director, actors) побудований
    /// GIN-індекс ix_titles_search_fts — SQL-функції інлайняться, планер бачить індексний план.
    /// </summary>
    public static bool TitleSearchMatch(string? titleEn, string? titleUk, string? titleRu, string? director, string[]? actors, string tsQuery) =>
        throw new InvalidOperationException("Лише для SQL-трансляції EF (HasDbFunction).");
}
