using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Wave2UxMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_watch_links_title_id_provider_kind_region",
                table: "watch_links");

            migrationBuilder.DropIndex(
                name: "ix_providers_title_id_provider_kind_source_region",
                table: "providers");

            migrationBuilder.AddColumn<string>(
                name: "audio_langs",
                table: "watch_links",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sub_langs",
                table: "watch_links",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "audio_langs",
                table: "providers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "providers",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "retail_price",
                table: "providers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sub_langs",
                table: "providers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "title_trailers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title_id = table.Column<int>(type: "integer", nullable: false),
                    yt_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    lang = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    official = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_title_trailers", x => x.id);
                    table.ForeignKey(
                        name: "fk_title_trailers_titles_title_id",
                        column: x => x.title_id,
                        principalTable: "titles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_watch_links_title_id_provider_kind_region_audio_langs",
                table: "watch_links",
                columns: new[] { "title_id", "provider", "kind", "region", "audio_langs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_providers_title_id_provider_kind_source_region_audio_langs",
                table: "providers",
                columns: new[] { "title_id", "provider", "kind", "source", "region", "audio_langs" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_title_trailers_title_id",
                table: "title_trailers",
                column: "title_id");

            migrationBuilder.CreateIndex(
                name: "ix_title_trailers_title_id_yt_id",
                table: "title_trailers",
                columns: new[] { "title_id", "yt_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "title_trailers");

            migrationBuilder.DropIndex(
                name: "ix_watch_links_title_id_provider_kind_region_audio_langs",
                table: "watch_links");

            migrationBuilder.DropIndex(
                name: "ix_providers_title_id_provider_kind_source_region_audio_langs",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "audio_langs",
                table: "watch_links");

            migrationBuilder.DropColumn(
                name: "sub_langs",
                table: "watch_links");

            migrationBuilder.DropColumn(
                name: "audio_langs",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "retail_price",
                table: "providers");

            migrationBuilder.DropColumn(
                name: "sub_langs",
                table: "providers");

            migrationBuilder.CreateIndex(
                name: "ix_watch_links_title_id_provider_kind_region",
                table: "watch_links",
                columns: new[] { "title_id", "provider", "kind", "region" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_providers_title_id_provider_kind_source_region",
                table: "providers",
                columns: new[] { "title_id", "provider", "kind", "source", "region" },
                unique: true);
        }
    }
}
