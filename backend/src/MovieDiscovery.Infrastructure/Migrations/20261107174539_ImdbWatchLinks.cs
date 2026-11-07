using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ImdbWatchLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-15: staging-таблиця для PG COPY з IMDb datasets (керується ImdbBulkService;
            // після злиття в titles чиститься — TRUNCATE imdb_raw). Не EF-сутність.
            migrationBuilder.Sql(
                """
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
                """);

            migrationBuilder.AddColumn<string>(
                name: "imdb_id",
                table: "titles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_adult",
                table: "titles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "watch_links",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title_id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    region = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    url = table.Column<string>(type: "text", nullable: true),
                    is_available = table.Column<bool>(type: "boolean", nullable: false),
                    checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watch_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_watch_links_titles_title_id",
                        column: x => x.title_id,
                        principalTable: "titles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_titles_imdb_id",
                table: "titles",
                column: "imdb_id",
                unique: true,
                filter: "imdb_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_titles_type_popularity",
                table: "titles",
                columns: new[] { "type", "popularity" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_titles_genres_gin",
                table: "titles",
                column: "genres",
                method: "gin");

            migrationBuilder.CreateIndex(
                name: "ix_watch_links_provider_is_available",
                table: "watch_links",
                columns: new[] { "provider", "is_available" });

            migrationBuilder.CreateIndex(
                name: "ix_watch_links_title_id",
                table: "watch_links",
                column: "title_id");

            migrationBuilder.CreateIndex(
                name: "ix_watch_links_title_id_provider_kind_region",
                table: "watch_links",
                columns: new[] { "title_id", "provider", "kind", "region" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "watch_links");

            migrationBuilder.Sql("DROP TABLE IF EXISTS imdb_raw;");

            migrationBuilder.DropIndex(
                name: "ix_titles_imdb_id",
                table: "titles");

            migrationBuilder.DropIndex(
                name: "ix_titles_type_popularity",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "imdb_id",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "is_adult",
                table: "titles");
        }
    }
}
