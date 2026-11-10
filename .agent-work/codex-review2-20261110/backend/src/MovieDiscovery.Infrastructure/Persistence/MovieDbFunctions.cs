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
}
