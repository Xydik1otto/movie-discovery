using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTitleSeriesFranchises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // title_series у снапшоті є (модель містить сутність), але жодна міграція
            // ще не створювала таблицю — тому тут ПОВНИЙ CREATE з фінальними іменами
            // колонок (label_uk/label_ru/label_en — синк TitleSeriesSync пише голий SQL
            // саме з цими іменами; знімок мав series_label_* до явного HasColumnName).
            migrationBuilder.CreateTable(
                name: "title_series",
                columns: table => new
                {
                    imdb_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    series_qid = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    label_uk = table.Column<string>(type: "text", nullable: true),
                    label_ru = table.Column<string>(type: "text", nullable: true),
                    label_en = table.Column<string>(type: "text", nullable: true),
                    prev_imdb = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    next_imdb = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_title_series", x => x.imdb_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_title_series_series_qid",
                table: "title_series",
                column: "series_qid");

            // Staging для COPY у синку (джерело правди — TitleSeriesStaging.CreateTableSql;
            // UNLOGGED ставить сам синк перед COPY).
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS title_series_raw (
                    imdb_id text NOT NULL,
                    series_qid text NULL,
                    label_uk text NULL,
                    label_ru text NULL,
                    label_en text NULL,
                    prev_imdb text NULL,
                    next_imdb text NULL
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS title_series_raw;");
            migrationBuilder.DropTable(
                name: "title_series");
        }
    }
}
