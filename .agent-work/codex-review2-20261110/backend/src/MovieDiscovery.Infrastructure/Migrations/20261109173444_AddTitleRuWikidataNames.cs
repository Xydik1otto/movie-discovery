using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTitleRuWikidataNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "title_ru",
                table: "titles",
                type: "text",
                nullable: true);

            // Staging wikidata_names_raw — DDL у міграції (як imdb_akas_raw); UNLOGGED ставить сервіс.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS wikidata_names_raw (
                    imdb_id text NOT NULL,
                    uk text NULL,
                    ru text NULL,
                    poster_url text NULL
                );
                """);

            // Пошук по ru-назвах (фолбек) — trgm-індекс, як title_uk/title_en.
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_titles_title_ru_trgm ON titles USING gin (title_ru gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_titles_title_ru_trgm;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS wikidata_names_raw;");
            migrationBuilder.DropColumn(
                name: "title_ru",
                table: "titles");
        }
    }
}
