using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AnimeManga : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "tmdb_id",
                table: "titles",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "anilist_id",
                table: "titles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "authors",
                table: "titles",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "chapters",
                table: "titles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "episodes",
                table: "titles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "format",
                table: "titles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "mal_id",
                table: "titles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "score_anilist",
                table: "titles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "score_mal",
                table: "titles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "score_shikimori",
                table: "titles",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "shikimori_id",
                table: "titles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "studios",
                table: "titles",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "volumes",
                table: "titles",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "import_jobs",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    profile_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    stats = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_jobs_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_titles_anilist_id",
                table: "titles",
                column: "anilist_id",
                unique: true,
                filter: "anilist_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_titles_mal_id",
                table: "titles",
                column: "mal_id",
                unique: true,
                filter: "mal_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_titles_shikimori_id",
                table: "titles",
                column: "shikimori_id",
                unique: true,
                filter: "shikimori_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_titles_studios",
                table: "titles",
                column: "studios")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_profile_id",
                table: "import_jobs",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_status",
                table: "import_jobs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_jobs");

            migrationBuilder.DropIndex(
                name: "ix_titles_anilist_id",
                table: "titles");

            migrationBuilder.DropIndex(
                name: "ix_titles_mal_id",
                table: "titles");

            migrationBuilder.DropIndex(
                name: "ix_titles_shikimori_id",
                table: "titles");

            migrationBuilder.DropIndex(
                name: "ix_titles_studios",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "anilist_id",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "authors",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "chapters",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "episodes",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "format",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "mal_id",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "score_anilist",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "score_mal",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "score_shikimori",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "shikimori_id",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "studios",
                table: "titles");

            migrationBuilder.DropColumn(
                name: "volumes",
                table: "titles");

            migrationBuilder.AlterColumn<int>(
                name: "tmdb_id",
                table: "titles",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
