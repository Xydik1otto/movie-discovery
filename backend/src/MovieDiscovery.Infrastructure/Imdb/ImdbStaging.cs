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
    /// Тип movie/series звіряється за IMDb ID для записів без TMDB ID.
    /// Ідемпотентно: ON CONFLICT (imdb_id) оновлює, не дублює.</summary>
    public const string MergeSql = """
        INSERT INTO titles
            (imdb_id, type, title_en, title_uk, year, runtime_min, episode_runtime_min, genres,
             rating_imdb, votes_imdb, popularity, is_adult,
             countries, keywords, actors, created_at, updated_at)
        SELECT s.tconst, s.title_type, s.primary_title, NULL, s.start_year,
               CASE WHEN s.title_type = 'movie' AND s.runtime_min > 0 THEN s.runtime_min END,
               CASE WHEN s.title_type = 'series' AND s.runtime_min > 0 THEN s.runtime_min END,
               s.genres,
               s.average_rating, s.num_votes, s.popularity, s.is_adult,
               '{}', '{}', '{}', now(), now()
        FROM imdb_raw s
        ON CONFLICT (imdb_id) WHERE imdb_id IS NOT NULL DO UPDATE SET
            type = CASE WHEN titles.tmdb_id IS NULL AND titles.type IN ('movie', 'series')
                             AND EXCLUDED.type IN ('movie', 'series')
                        THEN EXCLUDED.type ELSE titles.type END,
            title_en = EXCLUDED.title_en,
            year = CASE WHEN EXCLUDED.year > 0 THEN EXCLUDED.year ELSE titles.year END,
            runtime_min = CASE
                WHEN titles.tmdb_id IS NULL AND titles.type IN ('movie', 'series') AND EXCLUDED.type = 'series' THEN NULL
                WHEN titles.tmdb_id IS NULL AND titles.type = 'series' AND EXCLUDED.type = 'movie' THEN EXCLUDED.runtime_min
                WHEN titles.type = 'movie' AND EXCLUDED.type = 'movie' AND (titles.tmdb_id IS NULL OR titles.runtime_min IS NULL)
                    THEN COALESCE(EXCLUDED.runtime_min, titles.runtime_min)
                ELSE titles.runtime_min END,
            episode_runtime_min = CASE
                WHEN titles.tmdb_id IS NULL AND titles.type = 'series' AND EXCLUDED.type = 'movie' THEN NULL
                WHEN titles.tmdb_id IS NULL AND titles.type = 'movie' AND EXCLUDED.type = 'series' THEN EXCLUDED.episode_runtime_min
                WHEN titles.type = 'series' AND EXCLUDED.type = 'series' AND titles.tmdb_id IS NULL
                    THEN COALESCE(EXCLUDED.episode_runtime_min, titles.episode_runtime_min,
                         CASE WHEN titles.runtime_min > 0 THEN titles.runtime_min END)
                WHEN titles.type = 'series' AND EXCLUDED.type = 'series'
                    THEN COALESCE(titles.episode_runtime_min, EXCLUDED.episode_runtime_min)
                ELSE titles.episode_runtime_min END,
            seasons = CASE WHEN titles.tmdb_id IS NULL AND titles.type IN ('movie', 'series')
                               AND EXCLUDED.type IN ('movie', 'series') AND titles.type <> EXCLUDED.type
                           THEN NULL ELSE titles.seasons END,
            status = CASE WHEN titles.tmdb_id IS NULL AND titles.type IN ('movie', 'series')
                              AND EXCLUDED.type IN ('movie', 'series') AND titles.type <> EXCLUDED.type
                          THEN NULL ELSE titles.status END,
            genres = CASE WHEN cardinality(EXCLUDED.genres) > 0 THEN EXCLUDED.genres ELSE titles.genres END,
            rating_imdb = COALESCE(EXCLUDED.rating_imdb, titles.rating_imdb),
            votes_imdb = COALESCE(EXCLUDED.votes_imdb, titles.votes_imdb),
            popularity = CASE WHEN COALESCE(EXCLUDED.votes_imdb, 0) > 0
                              THEN EXCLUDED.popularity ELSE titles.popularity END,
            is_adult = titles.is_adult OR EXCLUDED.is_adult,
            updated_at = now()
        WHERE
            (titles.tmdb_id IS NULL AND titles.type IN ('movie', 'series')
                AND EXCLUDED.type IN ('movie', 'series') AND titles.type <> EXCLUDED.type)
            OR titles.title_en IS DISTINCT FROM EXCLUDED.title_en
            OR (EXCLUDED.year > 0 AND titles.year IS DISTINCT FROM EXCLUDED.year)
            OR (titles.type = 'movie' AND EXCLUDED.type = 'movie' AND EXCLUDED.runtime_min IS NOT NULL
                AND (titles.tmdb_id IS NULL OR titles.runtime_min IS NULL)
                AND titles.runtime_min IS DISTINCT FROM EXCLUDED.runtime_min)
            OR (titles.type = 'series' AND EXCLUDED.type = 'series' AND (
                (titles.tmdb_id IS NULL AND titles.runtime_min IS NOT NULL)
                OR (EXCLUDED.episode_runtime_min IS NOT NULL AND (titles.tmdb_id IS NULL OR titles.episode_runtime_min IS NULL)
                    AND titles.episode_runtime_min IS DISTINCT FROM EXCLUDED.episode_runtime_min)))
            OR (cardinality(EXCLUDED.genres) > 0 AND titles.genres IS DISTINCT FROM EXCLUDED.genres)
            OR (EXCLUDED.rating_imdb IS NOT NULL AND titles.rating_imdb IS DISTINCT FROM EXCLUDED.rating_imdb)
            OR (EXCLUDED.votes_imdb IS NOT NULL AND titles.votes_imdb IS DISTINCT FROM EXCLUDED.votes_imdb)
            OR (EXCLUDED.votes_imdb > 0 AND titles.popularity IS DISTINCT FROM EXCLUDED.popularity)
            OR (EXCLUDED.is_adult AND NOT titles.is_adult)
        """;

    /// <summary>Тимчасовий staging використовує схему з міграції та видаляється після транзакції.</summary>
    public const string CreateTempTableSql = "CREATE TEMP TABLE imdb_raw (LIKE imdb_raw INCLUDING DEFAULTS) ON COMMIT DROP;";

    public const string LinkedTypeConflictsSql = """
        SELECT t.imdb_id, t.tmdb_id, t.type, s.title_type, COUNT(*) OVER ()
        FROM imdb_raw s JOIN titles t ON t.imdb_id = s.tconst
        WHERE t.tmdb_id IS NOT NULL AND t.type IN ('movie', 'series')
            AND s.title_type IN ('movie', 'series') AND t.type <> s.title_type
        ORDER BY t.imdb_id
        LIMIT 20
        """;

    /// <summary>COPY-заголовок (binary): колонки в порядку Columns.</summary>
    public static string CopyFromStdinSql() =>
        $"COPY {TableName} ({string.Join(", ", Columns)}) FROM STDIN (FORMAT BINARY)";
}
