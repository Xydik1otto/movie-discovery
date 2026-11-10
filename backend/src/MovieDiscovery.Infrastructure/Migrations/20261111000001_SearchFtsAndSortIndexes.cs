using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <summary>
    /// Хвиля перфу пошуку (11.11, за API-аудитом):
    /// 1) FTS-функції + GIN-індекс для швидкого повнотекстового пошуку
    ///    (назви uk/en/ru + режисер + актори). ILIKE/trgm лишається фолбеком
    ///    у SearchService (Фаза B); Фаза A (FTS) прибирає 13-27с холодних запитів.
    /// 2) Sort/фільтр-індекси: status+popularity (частковий), сортування
    ///    rating/year/imdb/votes (виразні індекси під COALESCE-ключі Sort()).
    /// 3) ALTER FUNCTION actors_trgm_text COST 1000 — придушує popularity-walk
    ///    планера для ILIKE-предикатів без збігів (замір: 90с → 0.3с).
    /// Усі об'єкти створюються idempotentно (IF NOT EXISTS / OR REPLACE) —
    /// безпечний повторний прогін на БД, де індекси вже створені вручну CONCURRENTLY.
    /// </summary>
    public partial class SearchFtsAndSortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── FTS-функції (IMMUTABLE обгортки над to_tsvector/to_tsquery з фіксованим 'simple') ──
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.title_fts_vector_simple(txt text) RETURNS tsvector
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS
                $$ SELECT to_tsvector('simple'::regconfig, txt) $$;

                CREATE OR REPLACE FUNCTION public.title_search_fts(
                    en text, uk text, ru text, director text, actors text[]) RETURNS tsvector
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS
                $$ SELECT public.title_fts_vector_simple(
                       coalesce(en, '') || ' ' || coalesce(uk, '') || ' ' || coalesce(ru, '') || ' ' ||
                       coalesce(director, '') || ' ' || coalesce(array_to_string(actors, ' '), '')) $$;

                CREATE OR REPLACE FUNCTION public.title_search_tsquery(q text) RETURNS tsquery
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS
                $$ SELECT to_tsquery('simple'::regconfig, q) $$;

                CREATE OR REPLACE FUNCTION public.title_search_match(
                    en text, uk text, ru text, director text, actors text[], q text) RETURNS boolean
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS
                $$ SELECT public.title_search_fts(en, uk, ru, director, actors)
                          @@ public.title_search_tsquery(q) $$;
                """);

            // ── Кост-фікс ILIKE-прогулянки (details у коментарі класу) ──
            migrationBuilder.Sql("ALTER FUNCTION public.actors_trgm_text(text[]) COST 1000;");
            migrationBuilder.Sql("ALTER FUNCTION public.title_search_match(text, text, text, text, text[], text) COST 10000;");

            // ── Sort/фільтр-індекси (звичайний CREATE INDEX: на БД, де їх уже нема —
            //    будуються тут; на живій БД вже існують → IF NOT EXISTS = no-op) ──
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_titles_status_pop ON titles (status, popularity DESC) WHERE status IS NOT NULL;");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_titles_rating_sort ON titles ((COALESCE(rating_tmdb, rating_imdb, 0)) DESC, popularity DESC, id);");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_titles_year_sort ON titles ((COALESCE(year, 0)) DESC, popularity DESC, id);");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_titles_imdb_sort ON titles ((COALESCE(rating_imdb, 0)) DESC, (COALESCE(votes_imdb, 0)) DESC, id);");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_titles_votes_sort ON titles ((COALESCE(votes_imdb, 0)) DESC, (COALESCE(rating_imdb, 0)) DESC, id);");

            // ── FTS GIN-індекс: CONCURRENTLY (не блокує writes на 2.86М рядків);
            //    CONCURRENTLY не можна всередині транзакції → suppressTransaction ──
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_titles_search_fts ON titles USING GIN ((public.title_search_fts(title_en, title_uk, title_ru, director, actors)));",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_titles_search_fts;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_titles_votes_sort;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_titles_imdb_sort;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_titles_year_sort;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_titles_rating_sort;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_titles_status_pop;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.title_search_match(text, text, text, text, text[], text);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.title_search_tsquery(text);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.title_search_fts(text, text, text, text, text[]);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS public.title_fts_vector_simple(text);");
            migrationBuilder.Sql("ALTER FUNCTION public.actors_trgm_text(text[]) COST 100;");
        }
    }
}
