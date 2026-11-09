using System.Text.Json;
using MovieDiscovery.Core.Domain;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MovieDiscovery.Infrastructure.Persistence;

/// <summary>EF Core DbContext. Схема — PLAN.md (snake_case у БД через EFCore.NamingConventions).
/// База — IdentityUserContext (FR-16): AspNetUsers (+AspNetUserClaims); ролі навмисно НЕ підключені.</summary>
public class MovieDiscoveryDbContext(DbContextOptions<MovieDiscoveryDbContext> options)
    : IdentityUserContext<ApplicationUser>(options)
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
    public DbSet<WatchLink> WatchLinks => Set<WatchLink>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<InteractionLog> InteractionLogs => Set<InteractionLog>();
    public DbSet<BotIdentity> BotIdentities => Set<BotIdentity>();
    public DbSet<UserCollection> UserCollections => Set<UserCollection>();
    public DbSet<UserCollectionItem> UserCollectionItems => Set<UserCollectionItem>();
    public DbSet<TitleSeries> TitleSeries => Set<TitleSeries>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // FR-16: Identity-сутності (AspNetUsers + AspNetUserClaims; ролі НЕ потрібні).
        base.OnModelCreating(mb);
        mb.Entity<ApplicationUser>(e =>
        {
            // Явні назви таблиць Identity (без ролей): юзер + claims-сховище (AddIdentityCore).
            e.ToTable("asp_net_users");
            e.HasIndex(x => x.NormalizedEmail);
        });
        mb.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<string>>(e => e.ToTable("asp_net_user_claims"));
        mb.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>(e => e.ToTable("asp_net_user_logins"));
        mb.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>(e => e.ToTable("asp_net_user_tokens"));

        mb.HasPostgresExtension("vector");

        // SQL-функція акторів для composable-пошуку (SearchService q-гілка):
        // ILIKE по виразному GIN-індексу actors_trgm_text замість per-element unnest.
        mb.HasDbFunction(typeof(MovieDbFunctions).GetMethod(nameof(MovieDbFunctions.ActorsTrgmText))!)
            .HasName("actors_trgm_text");

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
            // FR-20 розширення: режисери-масив з IMDb crew (directors; director-рядок лишається)
            e.Property(x => x.Directors).HasMaxLength(4000);
            // Аніме/манга: пошук студій + часткова унікальність зовнішніх id (NULL не конфліктують).
            e.HasIndex(x => x.Studios).HasMethod("gin");
            e.HasIndex(x => x.MalId).IsUnique().HasFilter("mal_id IS NOT NULL");
            e.HasIndex(x => x.AnilistId).IsUnique().HasFilter("anilist_id IS NOT NULL");
            e.HasIndex(x => x.ShikimoriId).IsUnique().HasFilter("shikimori_id IS NOT NULL");
            // FR-15: IMDb bulk-каталог — унікальний tconst + часткові індекси популярності movie/series
            e.Property(x => x.ImdbId).HasMaxLength(16);
            e.HasIndex(x => x.ImdbId).IsUnique().HasFilter("imdb_id IS NOT NULL");
            e.Property(x => x.IsAdult).HasDefaultValue(false);
            e.HasIndex(x => new { x.Type, x.Popularity })
                .HasFilter("type IN ('movie', 'series')")
                .IsDescending(false, true);
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
            // pgvector.Vector мапиться лише під Npgsql (в InMemory тестах — пропускається)
            if (Database.ProviderName!.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))
                e.Property(x => x.Vec).HasColumnType("vector(1024)").IsRequired();
            e.Property(x => x.Dim).HasDefaultValue(1024);
            e.HasOne(x => x.Title)
                .WithOne()
                .HasForeignKey<EmbeddingRow>(x => x.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FR-14: «де подивитись» — watch_links
        mb.Entity<WatchLink>(e =>
        {
            e.ToTable("watch_links");
            e.Property(x => x.Provider).IsRequired();
            e.Property(x => x.Kind).IsRequired().HasMaxLength(16);
            e.Property(x => x.Region).IsRequired().HasMaxLength(8);
            e.HasOne(x => x.Title)
                .WithMany()
                .HasForeignKey(x => x.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
            // Ідемпотентність sync: повторний імпорт того самого (title, provider, kind, region) НЕ дублює
            e.HasIndex(x => new { x.TitleId, x.Provider, x.Kind, x.Region }).IsUnique();
            e.HasIndex(x => x.TitleId);
            e.HasIndex(x => new { x.Provider, x.IsAvailable });
        });

        // FR-16/17: профіль смаку юзера — зв'язка user_id → постійний профіль списків + jsonb-мапи ваг
        mb.Entity<UserProfile>(e =>
        {
            e.ToTable("user_profiles");
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.TasteGenres).HasColumnType("jsonb");
            e.Property(x => x.TasteKeywords).HasColumnType("jsonb");
            e.Property(x => x.TasteEra).HasColumnType("jsonb");
            e.Property(x => x.TasteActors).HasColumnType("jsonb");
            e.Property(x => x.NegativeGenres).HasColumnType("jsonb");
            e.Property(x => x.Quiz).HasColumnType("jsonb");
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne<ApplicationUser>()
                .WithOne()
                .HasForeignKey<UserProfile>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Profile)
                .WithMany()
                .HasForeignKey(x => x.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FR-17: «пам'ять про користувача» — interaction_log
        mb.Entity<InteractionLog>(e =>
        {
            e.ToTable("interaction_log");
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Action).IsRequired().HasMaxLength(16);
            e.HasOne(x => x.Title)
                .WithMany()
                .HasForeignKey(x => x.TitleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });

        // MD-05: telegram-ідентичність бота → власний профіль списків (PK = telegram_user_id,
        // унікальний — race-safe створення); FK на profiles з каскадом (профіль → його ідентичність).
        mb.Entity<BotIdentity>(e =>
        {
            e.ToTable("bot_identities");
            e.HasKey(x => x.TelegramUserId);
            e.HasOne(x => x.Profile)
                .WithMany()
                .HasForeignKey(x => x.ProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FR-21: збережені добірки юзера — user_collections + user_collection_items.
        // Ізоляція як у TasteProfileService: власник ТІЛЬКИ з поточного юзера (FK asp_net_users).
        mb.Entity<UserCollection>(e =>
        {
            e.ToTable("user_collections");
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Name).IsRequired();
            e.HasIndex(x => x.UserId);
            e.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        mb.Entity<UserCollectionItem>(e =>
        {
            e.ToTable("user_collection_items");
            // PK(collection_id, title_id) — також покриває вибірку items добірки за collection_id.
            e.HasKey(x => new { x.CollectionId, x.TitleId });
            e.Property(x => x.TitleId).HasColumnType("bigint");
            e.HasOne(x => x.Collection)
                .WithMany(c => c.Items)
                .HasForeignKey(x => x.CollectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FR: франшизи/саги (Wikidata P179) — title_series: imdb_id (PK) → серія
        // (QID + назви uk/ru/en) + prev/next частини. Групування на читанні — за SeriesQid.
        // FK на titles НЕ потрібні: imdb_id може посилатися на тайтли поза каталогом.
        mb.Entity<TitleSeries>(e =>
        {
            e.ToTable("title_series");
            e.HasKey(x => x.ImdbId);
            e.Property(x => x.ImdbId).HasMaxLength(16);
            e.Property(x => x.SeriesQid).HasMaxLength(16);
            // Колонки БЕЗ префікса series_: синк (TitleSeriesSync.UpsertSql/COPY) пише
            // голий SQL з label_uk/label_ru/label_en — назви колонок мають збігатись.
            e.Property(x => x.SeriesLabelUk).HasColumnName("label_uk");
            e.Property(x => x.SeriesLabelRu).HasColumnName("label_ru");
            e.Property(x => x.SeriesLabelEn).HasColumnName("label_en");
            e.Property(x => x.PrevImdb).HasMaxLength(16);
            e.Property(x => x.NextImdb).HasMaxLength(16);
            e.HasIndex(x => x.SeriesQid);
        });

        // Тестовий InMemory-провайдер не вміє jsonb/vector — наприкінці (після всіх конфігурацій),
        // інакше конфігураційні блоки вище перекриють конвертери. Npgsql не чіпається.
        // УВАГА: ProviderName для Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL" → перевіряємо саме InMemory.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var jsonToString = new ValueConverter<JsonDocument, string>(
                d => d.RootElement.GetRawText(),
                s => JsonDocument.Parse(s, default));
            var nullableJsonToString = new ValueConverter<JsonDocument?, string>(
                d => d == null ? "" : d.RootElement.GetRawText(),
                s => string.IsNullOrEmpty(s) ? null : JsonDocument.Parse(s, default));
            mb.Entity<ImportJob>().Property(x => x.Stats).HasConversion(nullableJsonToString);
            mb.Entity<SourceCacheEntry>().Property(x => x.Payload).HasConversion(jsonToString);
            mb.Entity<SavedQuery>().Property(x => x.Seeds).HasConversion(jsonToString);
            mb.Entity<SavedQuery>().Property(x => x.Filters).HasConversion(nullableJsonToString);
            mb.Entity<EmbeddingRow>().Ignore(x => x.Vec); // pgvector.Vector — не мапиться в InMemory
        }
    }
}
