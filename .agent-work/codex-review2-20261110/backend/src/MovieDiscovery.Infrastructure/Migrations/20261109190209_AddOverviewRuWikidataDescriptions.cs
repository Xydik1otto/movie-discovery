using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOverviewRuWikidataDescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "overview_ru",
                table: "titles",
                type: "text",
                nullable: true);

            // Staging wikidata_names_raw: нові колонки коротких описів (schema:description uk/ru).
            // Ідемпотентно: таблиця вже є з міграції AddTitleRuWikidataNames (4 колонки) — просто добудовуємо.
            migrationBuilder.Sql("""
                ALTER TABLE IF EXISTS wikidata_names_raw
                    ADD COLUMN IF NOT EXISTS desc_uk text NULL,
                    ADD COLUMN IF NOT EXISTS desc_ru text NULL;
                """);

            // Пошук по ru-описах — trgm-індекс, як title_uk/title_en/title_ru.
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_titles_overview_ru_trgm ON titles USING gin (overview_ru gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_titles_overview_ru_trgm;");
            migrationBuilder.Sql("""
                ALTER TABLE IF EXISTS wikidata_names_raw
                    DROP COLUMN IF EXISTS desc_uk,
                    DROP COLUMN IF EXISTS desc_ru;
                """);
            migrationBuilder.DropColumn(
                name: "overview_ru",
                table: "titles");
        }
    }
}
