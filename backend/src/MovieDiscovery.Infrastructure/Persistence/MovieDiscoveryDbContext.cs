using MovieDiscovery.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace MovieDiscovery.Infrastructure.Persistence;

/// <summary>EF Core DbContext. Схема — PLAN.md (snake_case у БД через EFCore.NamingConventions).</summary>
public class MovieDiscoveryDbContext(DbContextOptions<MovieDiscoveryDbContext> options)
    : DbContext(options)
{
    public DbSet<Title> Titles => Set<Title>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ListEntry> ListEntries => Set<ListEntry>();
    public DbSet<SavedQuery> SavedQueries => Set<SavedQuery>();
    public DbSet<SourceCacheEntry> SourceCache => Set<SourceCacheEntry>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<EmbeddingRow> Embeddings => Set<EmbeddingRow>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasPostgresExtension("vector");

        mb.Entity<Title>(e =>
        {
            e.Property(x => x.Type).IsRequired().HasMaxLength(16);
            e.Property(x => x.TitleEn).IsRequired();
            e.Property(x => x.Format).HasMaxLength(16);
            // Унікальність (tmdb_id, type): TMDB id фільмів і серіалів — різні простори і почислів,
            // тому саме складений ключ, а не унікальність одного tmdb_id.
            // Для аніме/манги tmdb_id NULL — у PostgreSQL NULL-и не конфліктують в unique-індексі.
            e.HasIndex(x => new { x.TmdbId, x.Type }).IsUnique();
            e.HasIndex(x => x.Year);
            e.HasIndex(x => x.Popularity).IsDescending();
            e.HasIndex(x => x.Genres).HasMethod("gin");
            e.HasIndex(x => x.Keywords).HasMethod("gin");
            e.HasIndex(x => x.Actors).HasMethod("gin");
            // Аніме/манга: пошук студій + часткова унікальність зовнішніх id (NULL не конфліктують).
            e.HasIndex(x => x.Studios).HasMethod("gin");
            e.HasIndex(x => x.MalId).IsUnique().HasFilter("mal_id IS NOT NULL");
            e.HasIndex(x => x.AnilistId).IsUnique().HasFilter("anilist_id IS NOT NULL");
            e.HasIndex(x => x.ShikimoriId).IsUnique().HasFilter("shikimori_id IS NOT NULL");
        });

        mb.Entity<Provider>(e =>
        {
            e.Property(x => x.ProviderName).HasColumnName("provider").IsRequired();
            e.Property(x => x.Kind).IsRequired().HasMaxLength(16);
            e.Property(x => x.Source).IsRequired();
            e.Property(x => x.Region).IsRequired().HasMaxLength(8);
            e.HasOne(x => x.Title)
                .WithMany()
                .HasForeignKey(x => x.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TitleId, x.ProviderName, x.Kind, x.Source, x.Region }).IsUnique();
        });

        mb.Entity<Profile>(e =>
        {
            e.Property(x => x.Name).IsRequired();
        });

        mb.Entity<ListEntry>(e =>
        {
            e.ToTable("lists");
            e.Property(x => x.Kind).IsRequired().HasMaxLength(16);
            e.HasOne(x => x.Profile)
                .WithMany(p => p.Lists)
                .HasForeignKey(x => x.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Title)
                .WithMany()
                .HasForeignKey(x => x.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProfileId, x.Kind, x.TitleId }).IsUnique();
        });

        mb.Entity<SavedQuery>(e =>
        {
            e.ToTable("queries");
            e.Property(x => x.Name).IsRequired();
            e.Property(x => x.Seeds).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Filters).HasColumnType("jsonb");
            e.HasOne(x => x.Profile)
                .WithMany()
                .HasForeignKey(x => x.ProfileId)
                .OnDelete(DeleteBehavior.SetNull);
        });
        mb.Entity<SourceCacheEntry>(e =>
        {
            e.Property(x => x.Source).IsRequired();
            e.Property(x => x.CacheKey).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => new { x.Source, x.CacheKey }).IsUnique();
        });

        mb.Entity<SyncState>(e =>
        {
            e.ToTable("sync_state");
            e.Property(x => x.Source).IsRequired();
            e.Property(x => x.Kind).IsRequired();
            e.HasIndex(x => new { x.Source, x.Kind }).IsUnique();
        });

        mb.Entity<ImportJob>(e =>
        {
            e.ToTable("import_jobs");
            e.Property(x => x.Source).IsRequired().HasMaxLength(16);
            e.Property(x => x.Username).IsRequired();
            e.Property(x => x.Status).IsRequired().HasMaxLength(16);
            e.Property(x => x.Stats).HasColumnType("jsonb");
            e.HasOne(x => x.Profile)
                .WithMany()
                .HasForeignKey(x => x.ProfileId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.Status);
        });

        mb.Entity<EmbeddingRow>(e =>
        {
            e.HasKey(x => x.TitleId);
            e.Property(x => x.Model).IsRequired();
            e.Property(x => x.Vec).HasColumnType("vector(1024)").IsRequired();
            e.Property(x => x.Dim).HasDefaultValue(1024);
            e.HasOne(x => x.Title)
                .WithOne()
                .HasForeignKey<EmbeddingRow>(x => x.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
