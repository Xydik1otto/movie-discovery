using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace MovieDiscovery.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "profiles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "source_cache",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source = table.Column<string>(type: "text", nullable: false),
                    cache_key = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    fetched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ttl_seconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_source_cache", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sync_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    last_run_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_pages = table.Column<int>(type: "integer", nullable: true),
                    last_count = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "titles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tmdb_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title_uk = table.Column<string>(type: "text", nullable: true),
                    title_en = table.Column<string>(type: "text", nullable: false),
                    overview_uk = table.Column<string>(type: "text", nullable: true),
                    overview_en = table.Column<string>(type: "text", nullable: true),
                    year = table.Column<int>(type: "integer", nullable: true),
                    countries = table.Column<string[]>(type: "text[]", nullable: false),
                    genres = table.Column<string[]>(type: "text[]", nullable: false),
                    keywords = table.Column<string[]>(type: "text[]", nullable: false),
                    actors = table.Column<string[]>(type: "text[]", nullable: false),
                    director = table.Column<string>(type: "text", nullable: true),
                    runtime_min = table.Column<int>(type: "integer", nullable: true),
                    episode_runtime_min = table.Column<int>(type: "integer", nullable: true),
                    seasons = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    original_language = table.Column<string>(type: "text", nullable: true),
                    rating_tmdb = table.Column<double>(type: "double precision", nullable: true),
                    rating_imdb = table.Column<double>(type: "double precision", nullable: true),
                    votes_imdb = table.Column<int>(type: "integer", nullable: true),
                    popularity = table.Column<double>(type: "double precision", nullable: false),
                    poster_path = table.Column<string>(type: "text", nullable: true),
                    backdrop_path = table.Column<string>(type: "text", nullable: true),
                    trailer_yt_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_titles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "queries",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    profile_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    seeds = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    filters = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_queries", x => x.id);
                    table.ForeignKey(
                        name: "fk_queries_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "embeddings",
                columns: table => new
                {
                    title_id = table.Column<int>(type: "integer", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    dim = table.Column<int>(type: "integer", nullable: false, defaultValue: 1024),
                    vec = table.Column<Vector>(type: "vector(1024)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_embeddings", x => x.title_id);
                    table.ForeignKey(
                        name: "fk_embeddings_titles_title_id",
                        column: x => x.title_id,
                        principalTable: "titles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lists",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    profile_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title_id = table.Column<int>(type: "integer", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    watched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists", x => x.id);
                    table.ForeignKey(
                        name: "fk_lists_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_lists_titles_title_id",
                        column: x => x.title_id,
                        principalTable: "titles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "providers",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    title_id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    url = table.Column<string>(type: "text", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_providers", x => x.id);
                    table.ForeignKey(
                        name: "fk_providers_titles_title_id",
                        column: x => x.title_id,
                        principalTable: "titles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_profile_id_kind_title_id",
                table: "lists",
                columns: new[] { "profile_id", "kind", "title_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lists_title_id",
                table: "lists",
                column: "title_id");

            migrationBuilder.CreateIndex(
                name: "ix_providers_title_id_provider_kind_source_region",
                table: "providers",
                columns: new[] { "title_id", "provider", "kind", "source", "region" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_queries_profile_id",
                table: "queries",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_source_cache_source_cache_key",
                table: "source_cache",
                columns: new[] { "source", "cache_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_state_source_kind",
                table: "sync_state",
                columns: new[] { "source", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_titles_actors",
                table: "titles",
                column: "actors")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_titles_genres",
                table: "titles",
                column: "genres")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_titles_keywords",
                table: "titles",
                column: "keywords")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_titles_popularity",
                table: "titles",
                column: "popularity",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_titles_tmdb_id_type",
                table: "titles",
                columns: new[] { "tmdb_id", "type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_titles_year",
                table: "titles",
                column: "year");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embeddings");

            migrationBuilder.DropTable(
                name: "lists");

            migrationBuilder.DropTable(
                name: "providers");

            migrationBuilder.DropTable(
                name: "queries");

            migrationBuilder.DropTable(
                name: "source_cache");

            migrationBuilder.DropTable(
                name: "sync_state");

            migrationBuilder.DropTable(
                name: "titles");

            migrationBuilder.DropTable(
                name: "profiles");
        }
    }
}
