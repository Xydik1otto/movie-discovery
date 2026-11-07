using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VirtualCategoriesYoutube : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "directors",
                table: "titles",
                type: "text[]",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "country",
                table: "titles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "audio_lang",
                table: "watch_links",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "quality",
                table: "watch_links",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "dub",
                table: "watch_links",
                type: "text",
                nullable: true);

            // FR-19/20: staging-таблиці IMDb bulk-синків (керуються ImdbAkasService/ImdbCrewService;
            // UNLOGGED ставить сервіс перед COPY, після злиття чиститься TRUNCATE). Не EF-сутності.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS imdb_akas_raw (
                    imdb_id text NOT NULL,
                    region text NOT NULL
                );
                CREATE TABLE IF NOT EXISTS imdb_crew_raw (
                    imdb_id text NOT NULL,
                    directors text[]
                );
                CREATE TABLE IF NOT EXISTS imdb_principals_raw (
                    imdb_id text NOT NULL,
                    actors text[]
                );
                CREATE TABLE IF NOT EXISTS imdb_names_raw (
                    nconst text NOT NULL,
                    primary_name text NOT NULL
                );
                CREATE EXTENSION IF NOT EXISTS pg_trgm;
                CREATE INDEX IF NOT EXISTS ix_titles_title_en_trgm
                    ON titles USING gin (title_en gin_trgm_ops);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS imdb_akas_raw;
                DROP TABLE IF EXISTS imdb_crew_raw;
                DROP TABLE IF EXISTS imdb_principals_raw;
                DROP TABLE IF EXISTS imdb_names_raw;
                """);

            migrationBuilder.DropColumn(
                name: "dub",
                table: "watch_links");

            migrationBuilder.DropColumn(
                name: "quality",
                table: "watch_links");

            migrationBuilder.DropColumn(
                name: "audio_lang",
                table: "watch_links");

            migrationBuilder.DropColumn(
                name: "country",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "directors",
                table: "titles");
        }
    }
}
