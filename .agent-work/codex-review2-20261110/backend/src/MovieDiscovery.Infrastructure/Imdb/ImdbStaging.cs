namespace MovieDiscovery.Infrastructure.Imdb;

/// <summary>Рядок staging-таблиці imdb_raw (між COPY і MERGE).</summary>
public sealed record ImdbStagingRow(
    string Tconst,
    string TitleType,
    string PrimaryTitle,
    string OriginalTitle,
    bool IsAdult,
    int? StartYear,
    int? RuntimeMinutes,
    string[] Genres,
    double? AverageRating,
    int? NumVotes,
    double Popularity);

/// <summary>
/// Staging imdb_raw + MERGE у titles (FR-15). SQL-константи — єдине джерело правди для
/// міграції (DDL) і сервісу (COPY/MERGE); перевіряються тестами на ключові clauses.
/// </summary>
public static class ImdbStaging
{
    public const string TableName = "imdb_raw";

    /// <summary>Порядок колонок для COPY — ЗБІГАЄТЬСЯ з DDL і стейджинг-записом.</summary>
    public static readonly string[] Columns =
    [
        "tconst", "title_type", "primary_title", "original_title", "is_adult",
        "start_year", "runtime_min", "genres", "average_rating", "num_votes", "popularity",
    ];

    /// <summary>DDL staging-таблиці (в міграції; тут — для smoke/тестів і довідки).</summary>
    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS imdb_raw (
            tconst text NOT NULL,
            title_type text NOT NULL,
            primary_title text NOT NULL,
            original_title text NOT NULL,
            is_adult boolean NOT NULL,
            start_year integer,
            runtime_min integer,
            genres text[],
            average_rating double precision,
            num_votes integer,
            popularity double precision NOT NULL DEFAULT 0
        );
        """;

    /// <summary>Злиття staging → titles за imdb_id: нові INSERT, наявні UPDATE.
    /// title_uk/type наявних НЕ чіпаються (uk — sweet.tv/TMDB; type — як створено).
    /// Ідемпотентно: ON CONFLICT (imdb_id) оновлює, не дублює.</summary>
    public const string MergeSql = """
        INSERT INTO titles
            (imdb_id, type, title_en, title_uk, year, runtime_min, genres,
             rating_imdb, votes_imdb, popularity, is_adult,
             countries, keywords, actors, created_at, updated_at)
        SELECT s.tconst, s.title_type, s.primary_title, NULL, s.start_year, s.runtime_min, s.genres,
               s.average_rating, s.num_votes, s.popularity, s.is_adult,
               '{}', '{}', '{}', now(), now()
        FROM imdb_raw s
        ON CONFLICT (imdb_id) WHERE imdb_id IS NOT NULL DO UPDATE SET
            title_en = EXCLUDED.title_en,
            year = EXCLUDED.year,
            runtime_min = EXCLUDED.runtime_min,
            genres = EXCLUDED.genres,
            rating_imdb = COALESCE(EXCLUDED.rating_imdb, titles.rating_imdb),
            votes_imdb = COALESCE(EXCLUDED.votes_imdb, titles.votes_imdb),
            popularity = CASE WHEN COALESCE(EXCLUDED.votes_imdb, 0) > 0
                              THEN EXCLUDED.popularity ELSE titles.popularity END,
            is_adult = titles.is_adult OR EXCLUDED.is_adult,
            updated_at = now()
        """;

    /// <summary>Стейджинг чиститься після злиття (і перед наступним COPY).</summary>
    public const string TruncateSql = "TRUNCATE TABLE imdb_raw;";

    /// <summary>COPY-заголовок (binary): колонки в порядку Columns.</summary>
    public static string CopyFromStdinSql() =>
        $"COPY {TableName} ({string.Join(", ", Columns)}) FROM STDIN (FORMAT BINARY)";
}
