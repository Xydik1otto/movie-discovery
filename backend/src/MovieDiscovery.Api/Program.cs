using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Caching;
using MovieDiscovery.Infrastructure.Embeddings;
using MovieDiscovery.Infrastructure.Localization;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Sync;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        MapEnvDefaults(builder.Configuration);

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins("http://localhost:3001", "http://127.0.0.1:3001")
            .AllowAnyHeader()
            .AllowAnyMethod()));

        builder.Services.Configure<SimilarityOptions>(builder.Configuration.GetSection("Similarity"));
        builder.Services.Configure<TmdbOptions>(builder.Configuration.GetSection("Tmdb"));
        builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
        builder.Services.Configure<OmdbOptions>(builder.Configuration.GetSection("Omdb"));
        builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
        builder.Services.Configure<ShikimoriOptions>(builder.Configuration.GetSection("Shikimori"));
        builder.Services.Configure<MalOfficialOptions>(builder.Configuration.GetSection("MalOfficial"));
        builder.Services.Configure<JikanOptions>(builder.Configuration.GetSection("Jikan"));

        var cs = builder.Configuration.GetConnectionString("Default")
                 ?? throw new InvalidOperationException("ConnectionStrings:Default не задано");
        builder.Services.AddDbContext<MovieDiscoveryDbContext>(o =>
            o.UseNpgsql(cs, npg => npg.UseVector()).UseSnakeCaseNamingConvention());

        builder.Services.AddHttpClient<TmdbClient>(c =>
        {
            c.BaseAddress = new Uri("https://api.themoviedb.org/3/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient<OmdbClient>(c =>
        {
            c.BaseAddress = new Uri("https://www.omdbapi.com/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddHttpClient("ollama", c =>
        {
            var url = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
            c.BaseAddress = new Uri(url.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(60);
        });
        builder.Services.AddHttpClient<JikanClient>((sp, c) =>
        {
            // Jikan (дзеркало MAL) — dormant: EOL/недоступний хост; BaseUrl перевизначається env JIKAN_URL.
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JikanOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient<ShikimoriClient>((sp, c) =>
        {
            c.BaseAddress = new Uri("https://shikimori.one/api/");
            c.Timeout = TimeSpan.FromSeconds(30);
            // ОБОВ'ЯЗКОВІ заголовки Shikimori: User-Agent завжди; X-Application-Name — після реєстрації застосунку.
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ShikimoriOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opt.UserAgent);
            if (!string.IsNullOrWhiteSpace(opt.ApplicationName))
                c.DefaultRequestHeaders.TryAddWithoutValidation("X-Application-Name", opt.ApplicationName);
        });
        builder.Services.AddHttpClient<AniListClient>(c =>
        {
            c.BaseAddress = new Uri("https://graphql.anilist.co");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient<MalOfficialClient>((sp, c) =>
        {
            c.BaseAddress = new Uri("https://api.myanimelist.net/v2/");
            c.Timeout = TimeSpan.FromSeconds(30);
            // Офіційний MAL API v2: обов'язковий заголовок X-MAL-CLIENT-ID (безкоштовний ключ)
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MalOfficialOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opt.ClientId))
                c.DefaultRequestHeaders.TryAddWithoutValidation("X-MAL-CLIENT-ID", opt.ClientId);
        });
        builder.Services.AddSingleton<IHttpClientFactoryAccessor, HttpClientFactoryAccessor>();

        builder.Services.AddSingleton<EmbeddingQueue>();
        builder.Services.AddSingleton<SyncQueue>();
        builder.Services.AddSingleton<AnimeSyncQueue>();
        builder.Services.AddSingleton<ImportQueue>();
        // Тротлінг джерел — спільні singletons (один на джерело для всіх споживачів).
        builder.Services.AddSingleton<JikanThrottle>();
        builder.Services.AddSingleton<ShikimoriThrottle>();
        builder.Services.AddSingleton<AniListThrottle>();
        builder.Services.AddSingleton<MalOfficialThrottle>();
        builder.Services.AddScoped<ICacheStore, SourceCacheStore>();
        builder.Services.AddSingleton<IEmbedder, OllamaEmbedder>();
        builder.Services.AddScoped<SimilarityEngine>(sp =>
            new SimilarityEngine(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimilarityOptions>>().Value));
        builder.Services.AddScoped<SearchService>();
        builder.Services.AddScoped<SimilarityService>();
        builder.Services.AddScoped<TitleService>();
        builder.Services.AddScoped<AnimeTitleUpsert>();
        builder.Services.AddScoped<AnimeCatalogService>();   // Jikan — dormant (EOL), лишається поза активним шляхом
        builder.Services.AddScoped<AniListCatalogService>(); // PRIMARY sync каталогу аніме/манги + MAL official-шар
        builder.Services.AddScoped<ImportService>();

        builder.Services.AddHostedService<EmbeddingBackgroundService>();
        builder.Services.AddHostedService<SyncBackgroundService>();
        builder.Services.AddHostedService<AnimeSyncBackgroundService>();
        builder.Services.AddHostedService<ImportBackgroundService>();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseCors();

        // Міграції + сід єдиного профілю «Головний». На design-time (dotnet ef) БД не чіпаємо.
        if (!EF.IsDesignTime)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
                await db.Database.MigrateAsync();
                await DbSeeder.SeedDefaultProfileAsync(db);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[startup] БД недоступна — API стартує без міграцій: {ex.Message}");
            }
        }

        app.MapControllers();
        await app.RunAsync();
    }

    /// <summary>ENV-змінні (.env) → конфіг-секції (контракт PLAN.md).</summary>
    private static void MapEnvDefaults(IConfiguration cfg)
    {
        void Map(string envName, string configKey)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(cfg[configKey]))
                cfg[configKey] = value;
        }
        Map("TMDB_API_KEY", "Tmdb:ApiKey");
        Map("OMDB_API_KEY", "Omdb:ApiKey");
        Map("OLLAMA_URL", "Ollama:Url");
        Map("OLLAMA_EMBED_MODEL", "Ollama:EmbedModel");
        Map("ADMIN_KEY", "Admin:Key");
        Map("JIKAN_URL", "Jikan:BaseUrl");
        Map("MAL_CLIENT_ID", "MalOfficial:ClientId");
    }
}
