using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Auth;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Api.Extensions;
using MovieDiscovery.Core;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Discover;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Core.Similarity;
using MovieDiscovery.Core.Taste;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Caching;
using MovieDiscovery.Infrastructure.Embeddings;
using MovieDiscovery.Infrastructure.Imdb;
using MovieDiscovery.Infrastructure.Localization;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;
using MovieDiscovery.Infrastructure.Sync;
using MovieDiscovery.Infrastructure.Cinemeta;
using MovieDiscovery.Infrastructure.Tmdb;
using MovieDiscovery.Infrastructure.Watch;
using MovieDiscovery.Infrastructure.Storage;
using MovieDiscovery.Infrastructure.Watchability;
using MovieDiscovery.Infrastructure.Wikipedia;
using MovieDiscovery.Infrastructure.Wikidata;
using MovieDiscovery.Infrastructure.Series;
using MovieDiscovery.Infrastructure.YouTube;

namespace MovieDiscovery.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        MapEnvDefaults(builder.Configuration);

        builder.Services.AddControllers();
        // Перф (10.11): компресія відповідей (Brotli+Gzip) + OutputCache каталогу —
        // реєстрація одна (AddPerfOptimizations інкапсулює обидві); middleware нижче.
        builder.Services.AddPerfOptimizations(builder.Configuration);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins("http://localhost:3001", "http://127.0.0.1:3001")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        builder.Services.Configure<SimilarityOptions>(builder.Configuration.GetSection("Similarity"));
        builder.Services.Configure<TmdbOptions>(builder.Configuration.GetSection("Tmdb"));
        builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
        builder.Services.Configure<OmdbOptions>(builder.Configuration.GetSection("Omdb"));
        builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
        builder.Services.Configure<ShikimoriOptions>(builder.Configuration.GetSection("Shikimori"));
        builder.Services.Configure<MalOfficialOptions>(builder.Configuration.GetSection("MalOfficial"));
        builder.Services.Configure<JikanOptions>(builder.Configuration.GetSection("Jikan"));
        builder.Services.Configure<ImdbOptions>(builder.Configuration.GetSection("Imdb"));
        builder.Services.Configure<ImdbAkasOptions>(builder.Configuration.GetSection("Imdb"));
        builder.Services.Configure<ImdbCrewOptions>(builder.Configuration.GetSection("Imdb"));
        // Назви/постери з Wikidata (uk + ru-фолбек; рішення власника 09.11).
        builder.Services.Configure<WikidataNamesOptions>(builder.Configuration.GetSection("Wikidata"));
        // FR: франшизи/саги (P179) — синк title_series.
        builder.Services.Configure<TitleSeriesSyncOptions>(builder.Configuration.GetSection("TitleSeries"));
        // «Де подивитись»: JustWatch (усі провайдери, рішення власника 09.11) + Megogo free (UA) + Plex free.
        builder.Services.Configure<JustWatchSyncOptions>(builder.Configuration.GetSection("JustWatch"));
        builder.Services.Configure<MegogoOptions>(builder.Configuration.GetSection("Megogo"));
        // Auth-листи (10.11): SMTP env-gated (MAIL_ENABLED=false → чесний лог-режим).
        builder.Services.AddEmailing(builder.Configuration);
        builder.Services.Configure<PlexOptions>(builder.Configuration.GetSection("Plex"));
        // UX-02: Wikipedia-постери (QID-first) + чистки фейкових/wrong-Commons постерів.
        builder.Services.Configure<WikipediaPosterOptions>(builder.Configuration.GetSection("Wikipedia"));
        builder.Services.Configure<SweetTvOptions>(builder.Configuration.GetSection("SweetTv"));
        builder.Services.Configure<YoutubeOptions>(builder.Configuration.GetSection("Youtube"));
        // FR-16/17/18: ваги профілю смаку + опції discover
        builder.Services.Configure<ProfileOptions>(builder.Configuration.GetSection("Profile"));
        builder.Services.Configure<DiscoverOptions>(builder.Configuration.GetSection("Discover"));
        // MD-05: бот — секція "Bot" (BOT_API_KEY через MapEnvDefaults; порожній ключ → bot-auth вимкнено)
        builder.Services.Configure<BotOptions>(builder.Configuration.GetSection("Bot"));

        var cs = builder.Configuration.GetConnectionString("Default")
                 ?? (builder.Environment.IsEnvironment("Testing")
                     ? "Host=localhost;Database=testing;Username=testing;Password=testing"
                     : throw new InvalidOperationException("ConnectionStrings:Default не задано"));
        builder.Services.AddDbContext<MovieDiscoveryDbContext>(o =>
            o.UseNpgsql(cs, npg => npg.UseVector()).UseSnakeCaseNamingConvention());

        // FR-16: Identity (EF store) — лише core (без ролей, без UI); lockout 5×15 хв; пароль ≥8 + цифра + велика.
        builder.Services.AddIdentityCore<ApplicationUser>(o =>
        {
            o.Lockout.AllowedForNewUsers = true;
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            o.Password.RequiredLength = 8;
            o.Password.RequireDigit = true;
            o.Password.RequireUppercase = true;
            o.Password.RequireNonAlphanumeric = false; // контракт: ≥8 + цифра + велика (без спецсимвола)
            o.User.RequireUniqueEmail = true;
        }).AddEntityFrameworkStores<MovieDiscoveryDbContext>();

        // FR-16: cookie-автентифікація HttpOnly SameSite=Lax. Secure — env-гейт:
        // публічний доступ іде через HTTPS-тунель (FRONT_PUBLIC_URL = https://…), тож
        // сесійна cookie на 30 діб не повинна ходити HTTP-легеню (код-рев'ю 11.11:
        // mitm/downgrade на внутрішньому плечі). На http://localhost браузери все одно
        // приймають Secure-cookie (localhost = trustworthy origin), LAN-HTTP-доступ —
        // свідомо не підтримується для логіна.
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "md_auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                var frontPublicUrl = Environment.GetEnvironmentVariable("FRONT_PUBLIC_URL");
                var publicHttps = frontPublicUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
                o.Cookie.SecurePolicy = publicHttps
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.None;
                // FR-PERSIST: rememberMe → persistent-cookie на 30 діб замість сесійної;
                // sliding продовжує активні сесії (перевидача після половини строку).
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        builder.Services.AddAuthorization();

        // FR-PERSIST: key-ring DataProtection має ПЕРЕЖИВАТИ перестворення контейнера —
        // інакше кожен recreate генерує нові ключі і ВСІ md_auth cookies стають
        // недійсними (власник: «при кожному перестворенні вилітає логін»).
        // Стабільне ApplicationName робить ключі незалежними від шляху збірки;
        // файлове сховище вмикається лише коли ASPNETKEYS_DIRECTORY задана
        // (docker compose мапить volume ./data/keys:/data/keys — задача оркестратора),
        // бо шлях /root на Windows-хості інтеграційних тестів невалідний.
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName(CookieSetup.ApplicationName);
        var keysDirectory = CookieSetup.ResolveKeysDirectory();
        if (keysDirectory is not null)
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        // За тунелем/проксі реальна адреса клієнта приходить у X-Forwarded-For
        // (cloudflared → Next-проксі передає заголовок далі). Без цього
        // RemoteIpAddress = IP проксі, і auth-ліміти (login 5/хв, register 3/год)
        // стають СПІЛЬНИМИ для всіх зовнішніх користувачів — перші ж спроби
        // вичерпують квоту на всіх (429 для всіх).
        // Код-рев'ю 11.11: Clear() = довіра XFF будь-кому → спуфінг заголовка
        // обходить УСІ per-IP лімітери (кожен запит з новим XFF = свіжий бакет).
        // Довіра тепер точкова: docker-мережа compose (web/bot/gateway) +
        // DNS-резолв імен сервісів на старті; XFF від прямих клієнтів (LAN)
        // ігнорується. Підмережа — env MD_TRUSTED_NETWORKS (кома-список CIDR),
        // бо recreate мережі може змінити 172.x.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var cidr in (Environment.GetEnvironmentVariable("MD_TRUSTED_NETWORKS")
                                      ?? "172.23.0.0/16,127.0.0.0/8,::1/128")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(cidr, out var net))
                    o.KnownNetworks.Add(net);
            }

            foreach (var host in new[] { "web", "bot" })
            {
                try
                {
                    foreach (var ip in System.Net.Dns.GetHostAddresses(host))
                        o.KnownProxies.Add(ip);
                }
                catch
                {
                    // поза контейнером (дизайн-тайм/тести) резолву нема — не фатально
                }
            }

            if (System.Net.IPAddress.TryParse("172.23.0.1", out var gw))
                o.KnownProxies.Add(gw);
        });

        // FR-16: RateLimiter — login 5/хв/IP, register 3/год/IP (429 при перевищенні).
        // Codex-додаток: 429 має РОЗПІЗНАВАНЕ тіло {error:"rate_limited", retryAfterMinutes} —
        // інакше фронт показує загальне «Не вдалося» і юзер стукатилася марно.
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("auth-login", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                }));
            o.AddPolicy("auth-register", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3, Window = TimeSpan.FromHours(1), QueueLimit = 0,
                }));
            o.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
                // FixedWindow віддає RetryAfter у lease-метаданих; коли його нема — null
                // (фронт показує чесне «пізніше» без вигаданих хвилин).
                int? retryAfterMinutes = null;
                // (fix tr-2 для спільного build-гейту: класу RetryAfterMetadata нема —
                //  ключ метадати Retry-After рядковий; значення — TimeSpan)
                if (context.Lease?.TryGetMetadata("Retry-After", out var retryAfter) == true
                    && retryAfter is TimeSpan ra)
                    retryAfterMinutes = Math.Max(1, (int)Math.Ceiling(ra.TotalMinutes));
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = "rate_limited",
                    retryAfterMinutes,
                });
                await context.HttpContext.Response.WriteAsync(payload, ct);
            };
        });

        // Квота YouTube on-demand (трейлери/watch-enrich): партіція 10/хв/IP + денний
        // лічильник search-викликів (1 search = 100 units, ліміт 90/день — код-рев'ю
        // 11.11: анонім з перебором id випалював денну квоту API за один прогін).
        builder.Services.AddYouTubeRateLimiting();

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
        // FR-15: IMDb datasets — довге потокове завантаження .gz (до ~700 MB)
        builder.Services.AddHttpClient<ImdbBulkService>(c =>
        {
            c.BaseAddress = new Uri("https://datasets.imdbws.com/");
            c.Timeout = TimeSpan.FromMinutes(60);
        });
        // FR-19: imdb-akas (title.akas.tsv.gz ~300 MB) — той самий довгий стрім-патерн
        builder.Services.AddHttpClient<ImdbAkasService>(c =>
        {
            c.BaseAddress = new Uri("https://datasets.imdbws.com/");
            c.Timeout = TimeSpan.FromMinutes(60);
        });
        // Розширення FR-19/20: imdb-crew (crew + principals + name.basics — стріми до ~600 MB сумарно)
        builder.Services.AddHttpClient<ImdbCrewService>(c =>
        {
            c.BaseAddress = new Uri("https://datasets.imdbws.com/");
            c.Timeout = TimeSpan.FromMinutes(60);
        });
        // Назви/постери з Wikidata Query Service (SPARQL, текст/CSV — короткі запити)
        builder.Services.AddHttpClient<WikidataNamesService>(c =>
        {
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        // FR: франшизи/саги — SPARQL VALUES-батчі у Query Service (той самий короткий патерн)
        builder.Services.AddHttpClient<TitleSeriesSyncService>(c =>
        {
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        // JustWatch GraphQL (провайдери «де подивитись»): батчі популярних + пошук по каталогу
        builder.Services.AddHttpClient<JustWatchSyncService>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
        });
        // Megogo free-каталог UA: тротлінг 700 мс у сервісі; авто-редіекти вимкнено —
        // 302 → /ua означає КІНЕЦЬ видачі колекції, з редіректом синк змішав би картки головної
        builder.Services.AddHttpClient<MegogoClient>((sp, c) =>
        {
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MegogoOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(60);
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opt.UserAgent);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        // Фонова джоба megogo ресолвить сервіс зі scope (черга → SyncAsync)
        builder.Services.AddScoped<MegogoSyncService>();
        // Plex movie-database: важкі year-сторінки (5MB+) → довгий таймаут
        builder.Services.AddHttpClient<PlexSyncService>((sp, c) =>
        {
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlexOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(120);
        });
        // UX-02: Wikipedia-постери (QID-first: imdb→P345→QID→sitelink→pageimages) + чистки
        builder.Services.AddHttpClient<WikipediaPosterService>((sp, c) =>
        {
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WikipediaPosterOptions>>().Value;
            c.Timeout = TimeSpan.FromSeconds(60);
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opt.UserAgent);
        });
        builder.Services.AddScoped<PosterFakeCleanupService>();
        builder.Services.AddScoped<CommonsWrongPosterCleanupService>();
        // Постери-MinIO (10.11): сховище fail-closed (Enabled=false без env) + синк poster_path → бакет.
        builder.Services.AddSingleton<IPosterStorage, MinioPosterStorage>();
        builder.Services.AddSingleton<PosterStorageSyncQueue>();
        builder.Services.AddHttpClient<PosterStorageSyncService>();
        // Джерела постерів (10.11): TMDB-резолв + масовий keyset-синк + аніме-покриття.
        builder.Services.AddSingleton<PosterSourceSyncQueue>();
        builder.Services.AddScoped<ITmdbImagesService, TmdbImagesService>();
        // Cinemata (метадата Stremio): постери/бекдропи за imdb_id БЕЗ ключа і реєстрації —
        // джерело-фолбек, коли TMDB_API_KEY не задано; валідація назви проти нашого каталогу.
        builder.Services.AddSingleton<CinemetaThrottle>();
        builder.Services.AddHttpClient<ICinemetaImagesService, CinemetaImagesService>(c =>
        {
            c.BaseAddress = new Uri("https://v3-cinemeta.strem.io/");
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MovieDiscovery/1.0 (self-hosted catalog)");
        });
        builder.Services.AddScoped<PosterCoverageService>();
        builder.Services.AddScoped<PosterSourceSyncService>();
        // Watch-верифікація (10.11): coverage full|episode|unknown — фоновий обхід лінків.
        builder.Services.AddHttpClient<WatchLinkVerificationService>((sp, c) =>
            c.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.AddSingleton<WatchLinkVerificationQueue>();
        // UX-04: перевірка «Where to Watch» сторінки тайтла (tri-state, обмежений кеш) —
        // окремий короткий таймаут (PlexOptions.AvailabilityTimeoutMs, 10s), не sync-таймаут.
        builder.Services.AddHttpClient<PlexAvailabilityService>((sp, c) =>
        {
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlexOptions>>().Value;
            // (fix tr-2 для спільного build-гейту: c.UserAgent не існує — DefaultRequestHeaders.UserAgent)
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                c.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
            c.Timeout = TimeSpan.FromSeconds(Math.Max(5, opt.AvailabilityTimeoutMs / 1000 + 5));
        });
        // FR-20: YouTube Data API v3 (base з опцій; ключ YOUTUBE_API_KEY через MapEnvDefaults)
        builder.Services.AddHttpClient<YoutubeClient>((sp, c) =>
        {
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<YoutubeOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        // FR-14: Internet Archive advancedsearch (сторінка на 500 рядків у IA віддається
        // довше 30 с — таймаут 120 с; інакше sync падає TaskCanceledException)
        builder.Services.AddHttpClient<ArchiveWatchService>(c =>
        {
            c.BaseAddress = new Uri("https://archive.org/");
            c.Timeout = TimeSpan.FromSeconds(120);
        });
        // FR-14: sweet.tv — чесний User-Agent, тротлінг 1 req/2 c у сервісі
        builder.Services.AddHttpClient<SweetTvService>((sp, c) =>
        {
            var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SweetTvOptions>>().Value;
            c.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", opt.UserAgent);
        });
        builder.Services.AddSingleton<IHttpClientFactoryAccessor, HttpClientFactoryAccessor>();

        builder.Services.AddSingleton<EmbeddingQueue>();
        builder.Services.AddSingleton<SyncQueue>();
        builder.Services.AddSingleton<AnimeSyncQueue>();
        builder.Services.AddSingleton<ImportQueue>();
        builder.Services.AddSingleton<ImdbSyncQueue>();
        builder.Services.AddSingleton<WatchSyncQueue>();
        builder.Services.AddSingleton<ImdbAkasSyncQueue>();
        builder.Services.AddSingleton<ImdbCrewSyncQueue>();
        builder.Services.AddSingleton<WikidataNamesSyncQueue>();
        builder.Services.AddSingleton<TitleSeriesSyncQueue>();
        builder.Services.AddSingleton<YoutubeSyncQueue>();
        builder.Services.AddSingleton<TitleTrailerSyncQueue>();
        builder.Services.AddSingleton<JustWatchSyncQueue>();
        builder.Services.AddSingleton<MegogoSyncQueue>();
        builder.Services.AddSingleton<PlexSyncQueue>();
        builder.Services.AddSingleton<MegogoThrottle>();
        builder.Services.AddSingleton<WikipediaPosterSyncQueue>();
        builder.Services.AddSingleton<PosterCleanupQueue>();
        // Тротлінг джерел — спільні singletons (один на джерело для всіх споживачів).
        builder.Services.AddSingleton<JikanThrottle>();
        builder.Services.AddSingleton<ShikimoriThrottle>();
        builder.Services.AddSingleton<AniListThrottle>();
        builder.Services.AddSingleton<MalOfficialThrottle>();
        builder.Services.AddSingleton<SweetTvThrottle>();
        builder.Services.AddScoped<ICacheStore, SourceCacheStore>();
        // MD-09: in-memory кеш (titlesCount у /api/health — 60 с TTL).
        builder.Services.AddMemoryCache();
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
        builder.Services.AddScoped<TmdbProvidersService>();
        builder.Services.AddScoped<YouTubeEnrichService>();
        // UX-05: незалежний трейлер-шлях — search+матч+верифікація → title_trailers
        builder.Services.AddHttpClient<TrailerVerifier>((sp, c) =>
        {
            // oEmbed не залежить від API-ключа і квоти (200=живий, 401=embed заборонено, 404=видалено)
            c.BaseAddress = new Uri("https://www.youtube.com/");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.AddScoped<TitleTrailerSyncService>();
        // FR-16/17/18: акаунти, профіль смаку, discover, онбординг
        builder.Services.AddScoped<TasteProfileService>();
        builder.Services.AddScoped<UserListService>();
        builder.Services.AddScoped<GuestProfileService>(); // MD-01: designated guest profile
        builder.Services.AddScoped<CollectionsService>();
        builder.Services.AddScoped<DiscoverService>();
        builder.Services.AddScoped<OnboardingPostersService>(); // FR-18: постери варіантів онбордингу
        builder.Services.AddScoped<BotIdentityService>(); // MD-05: telegram-ідентичність → профіль списків

        // Тестове середовище (WebApplicationFactory): фонові сервіси не потрібні — інакше
        // вони стартують синхронізації проти InMemory-БД. Прод-поведінка не змінюється.
        if (!builder.Environment.IsEnvironment("Testing"))
        {
            builder.Services.AddHostedService<EmbeddingBackgroundService>();
            builder.Services.AddHostedService<SyncBackgroundService>();
            builder.Services.AddHostedService<AnimeSyncBackgroundService>();
            builder.Services.AddHostedService<ImportBackgroundService>();
            builder.Services.AddHostedService<ImdbSyncBackgroundService>();
            builder.Services.AddHostedService<WatchSyncBackgroundService>();
            builder.Services.AddHostedService<ImdbAkasSyncBackgroundService>();
            builder.Services.AddHostedService<ImdbCrewSyncBackgroundService>();
            builder.Services.AddHostedService<WikidataNamesSyncBackgroundService>();
            builder.Services.AddHostedService<TitleSeriesSyncBackgroundService>();
            builder.Services.AddHostedService<YoutubeSyncBackgroundService>();
            builder.Services.AddHostedService<TitleTrailerSyncBackgroundService>();
            builder.Services.AddHostedService<JustWatchSyncBackgroundService>();
            builder.Services.AddHostedService<MegogoSyncBackgroundService>();
            builder.Services.AddHostedService<PlexSyncBackgroundService>();
            builder.Services.AddHostedService<WikipediaPosterSyncBackgroundService>();
            builder.Services.AddHostedService<PosterCleanupBackgroundService>();
            builder.Services.AddHostedService<PosterStorageSyncBackgroundService>();
            builder.Services.AddHostedService<PosterSourceSyncBackgroundService>();
            builder.Services.AddHostedService<WatchLinkVerificationBackgroundService>();
        }

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Глобальний обробник невідомих винятків → ProblemDetails-подібне тіло.
        // API-аудит 11.11: 500-ки йшли З ПОРОЖНІМ тілом (0 байт) — клієнт бачив
        // беззмістовний збій замість чесного «сервер не впорався».
        app.UseExceptionHandler(a => a.Run(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "internal_error",
                message = "На сервері сталася помилка. Спробуйте ще раз за мить.",
            });
        }));

        // Компресія найзовніша: OutputCache зберігає НЕстиснутий боді, стиснення per-запит на виході.
        app.UseResponseCompression();
        app.UseCors();
        // XFF → RemoteIpAddress ДО auth-лімітів (інакше спільний бакет на всіх).
        app.UseForwardedHeaders();
        // OutputCache: після UseCors (вимога docs), до auth/MapControllers; персональні зони поза політикою.
        app.UseOutputCache();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // Міграції + сід єдиного профілю «Головний». На design-time (dotnet ef) БД не чіпаємо.
        if (!EF.IsDesignTime)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
                if (app.Environment.IsEnvironment("Testing"))
                    await db.Database.EnsureCreatedAsync(); // InMemory у WAF-тестах — міграції не потрібні
                else
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
        Map("YOUTUBE_API_KEY", "Youtube:ApiKey");
        Map("OMDB_API_KEY", "Omdb:ApiKey");
        Map("OLLAMA_URL", "Ollama:Url");
        Map("OLLAMA_EMBED_MODEL", "Ollama:EmbedModel");
        Map("ADMIN_KEY", "Admin:Key");
        Map("JIKAN_URL", "Jikan:BaseUrl");
        Map("MAL_CLIENT_ID", "MalOfficial:ClientId");
        Map("BOT_API_KEY", "Bot:ApiKey");
    }
}
