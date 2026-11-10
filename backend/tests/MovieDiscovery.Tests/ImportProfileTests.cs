using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Tests;

/// <summary>
/// Дефект хвилі-1 (1) + Codex IV-01 (P1 безпеки): імпорт пише у профіль, який СЕРВЕР
/// визначив сам — залогінений → власний профіль юзера (≠NULL, ≠гостьовий), анонім →
/// designated guest profile; profileId клієнта не є джерелом правди (чужий profileId →
/// 401/403, читання чужої джоби → 404). Реальних HTTP-викликів нема: Shikimori відповідає
/// через StubHttpHandler.
/// </summary>
public class ImportProfileTests
{
    private const string UserResolveJson = """{ "id": 12345, "nickname": "someuser" }""";

    // Мінімальний completed-rate за формою реальних відповідей Shikimori (див. ShikimoriMappingTests).
    private const string AnimeRatesJson = """
        [
          { "id": 1001, "target_id": 5114, "score": 10, "status": "completed",
            "anime": { "id": 5114, "name": "Hagane no Renkinjutsushi",
                       "russian": "Стальной алхимик",
                       "image": "/system/animes/original/5114.jpg",
                       "kind": "tv", "score": 9.1, "status": "released", "episodes": 64,
                       "aired_on": "2009-04-05",
                       "genres": [ { "id": 1, "name": "Action", "russian": "Экшен" } ],
                       "studios": [ { "name": "Bones" } ] } }
        ]
        """;

    private static (MovieDiscoveryDbContext Db, ImportService Importer) New(
        string name, string? animeRatesJson = null, string? mangaRatesJson = null)
    {
        var db = TestDb.Create(name);
        // Дефолтний (гостьовий) профіль присутній — джоба юзера НЕ має туди потрапити.
        db.Profiles.Add(new Profile { Id = 1, Name = "Guest", IsDefault = true });
        db.Profiles.Add(new Profile { Id = 2, Name = "Own", IsDefault = false });

        var config = new ConfigurationBuilder().Build(); // GUEST_PROFILE_ID не задано → дефолт 1
        var guest = new GuestProfileService(db, config, Logs.Null<GuestProfileService>());

        var shikimori = new ShikimoriClient(
            StubHttp.Client(r =>
            {
                var pq = r.RequestUri!.PathAndQuery;
                if (pq.StartsWith("/users/someuser")) return StubHttp.Json(UserResolveJson);
                if (pq.Contains("anime_rates")) return StubHttp.Json(animeRatesJson ?? AnimeRatesJson);
                if (pq.Contains("manga_rates")) return StubHttp.Json(mangaRatesJson ?? "[]");
                return null;
            }),
            new TestDb.CacheStub(),
            new ShikimoriThrottle(),
            Logs.Null<ShikimoriClient>());

        var jikan = new JikanClient(
            StubHttp.Client(_ => null), new TestDb.CacheStub(), new JikanThrottle(), Logs.Null<JikanClient>());
        var anilist = new AniListClient(
            StubHttp.Client(_ => null), new TestDb.CacheStub(), new AniListThrottle(), Logs.Null<AniListClient>());

        return (db, new ImportService(db, jikan, shikimori, anilist, guest, Logs.Null<ImportService>()));
    }

    private static ImportJob NewJob(int? profileId) => new()
    {
        Source = ImportSourceCatalog.Shikimori,
        Username = "someuser",
        ProfileId = profileId,
        Status = "queued",
    };

    // ─── Паритет статусів Shikimori (по одному rate на кожен статус) ───

    private const string AllStatusesAnimeJson = """
        [
          { "id": 2001, "target_id": 5114, "score": 10, "status": "completed",
            "anime": { "id": 5114, "name": "Hagane no Renkinjutsushi", "russian": "Стальной алхимик",
                       "image": "/system/animes/original/5114.jpg", "kind": "tv", "score": 9.1,
                       "status": "released", "episodes": 64, "aired_on": "2009-04-05",
                       "genres": [ { "id": 1, "name": "Action", "russian": "Экшен" } ],
                       "studios": [ { "name": "Bones" } ] } },
          { "id": 2002, "target_id": 1535, "score": 0, "status": "watching",
            "anime": { "id": 1535, "name": "Steins;Gate", "image": "/system/animes/original/1535.jpg",
                       "kind": "tv", "score": 9.2, "status": "released", "episodes": 24,
                       "aired_on": "2011-04-06",
                       "genres": [ { "id": 8, "name": "Sci-Fi", "russian": "Фантастика" } ] } },
          { "id": 2003, "target_id": 16498, "score": 8, "status": "on_hold",
            "anime": { "id": 16498, "name": "Shingeki no Kyojin", "image": "/system/animes/original/16498.jpg",
                       "kind": "tv", "score": 8.6, "status": "released", "episodes": 25,
                       "aired_on": "2013-04-07",
                       "genres": [ { "id": 1, "name": "Action", "russian": "Экшен" } ] } },
          { "id": 2004, "target_id": 21, "score": 7, "status": "dropped",
            "anime": { "id": 21, "name": "One Piece", "image": "/system/animes/original/21.jpg",
                       "kind": "tv", "score": 8.7, "status": "ongoing", "episodes": 0,
                       "aired_on": "1999-10-20",
                       "genres": [ { "id": 1, "name": "Action", "russian": "Экшен" } ] } },
          { "id": 2005, "target_id": 9253, "score": 0, "status": "planned",
            "anime": { "id": 9253, "name": "Gintama", "image": "/system/animes/original/9253.jpg",
                       "kind": "tv", "score": 9.0, "status": "released", "episodes": 201,
                       "aired_on": "2006-04-04",
                       "genres": [ { "id": 4, "name": "Comedy", "russian": "Комедия" } ] } }
        ]
        """;

    private const string AllStatusesMangaJson = """
        [
          { "id": 3001, "target_id": 100, "score": 9, "status": "completed",
            "manga": { "id": 100, "name": "Monster", "russian": "Монстр",
                       "image": "/system/mangas/original/100.jpg", "kind": "manga", "score": 9.1,
                       "status": "released", "chapters": 162, "volumes": 18,
                       "genres": [ { "id": 2, "name": "Drama", "russian": "Драма" } ] } },
          { "id": 3002, "target_id": 101, "score": 0, "status": "reading",
            "manga": { "id": 101, "name": "Vinland Saga", "image": "/system/mangas/original/101.jpg",
                       "kind": "manga", "score": 9.0, "status": "ongoing", "chapters": 200, "volumes": 26,
                       "genres": [ { "id": 1, "name": "Action", "russian": "Экшен" } ] } },
          { "id": 3003, "target_id": 102, "score": 0, "status": "planned",
            "manga": { "id": 102, "name": "Berserk", "image": "/system/mangas/original/102.jpg",
                       "kind": "manga", "score": 9.3, "status": "ongoing", "chapters": 370, "volumes": 42,
                       "genres": [ { "id": 2, "name": "Drama", "russian": "Драма" } ] } },
          { "id": 3004, "target_id": 103, "score": 6, "status": "on_hold",
            "manga": { "id": 103, "name": "Vagabond", "image": "/system/mangas/original/103.jpg",
                       "kind": "manga", "score": 9.1, "status": "ongoing", "chapters": 300, "volumes": 37,
                       "genres": [ { "id": 1, "name": "Action", "russian": "Экшен" } ] } },
          { "id": 3005, "target_id": 104, "score": 4, "status": "dropped",
            "manga": { "id": 104, "name": "Nisekoi", "image": "/system/mangas/original/104.jpg",
                       "kind": "manga", "score": 7.5, "status": "released", "chapters": 229, "volumes": 25,
                       "genres": [ { "id": 4, "name": "Comedy", "russian": "Комедия" } ] } }
        ]
        """;

    // Лише один watching-rate — для перевірки перенесення watchlist → watching.
    private const string WatchingAnimeJson = """
        [
          { "id": 2002, "target_id": 1535, "score": 0, "status": "watching",
            "anime": { "id": 1535, "name": "Steins;Gate", "image": "/system/animes/original/1535.jpg",
                       "kind": "tv", "score": 9.2, "status": "released", "episodes": 24,
                       "aired_on": "2011-04-06",
                       "genres": [ { "id": 8, "name": "Sci-Fi", "russian": "Фантастика" } ] } }
        ]
        """;

    [Fact]
    public async Task Import_Writes_All_Shikimori_Statuses_As_List_Kinds()
    {
        var (db, importer) = New("import-all-statuses", AllStatusesAnimeJson, AllStatusesMangaJson);
        var job = NewJob(profileId: 2);
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();

        Assert.True(await importer.RunAsync(job.Id));

        var rows = await db.ListEntries.Where(l => l.ProfileId == 2).ToListAsync();
        Assert.Equal(10, rows.Count);
        Assert.Equal(1, rows.Count(l => l.Kind == ListKinds.Watched));   // completed (аніме)
        Assert.Equal(1, rows.Count(l => l.Kind == ListKinds.Watching));  // watching
        Assert.Equal(1, rows.Count(l => l.Kind == ListKinds.Read));      // completed (манга)
        Assert.Equal(1, rows.Count(l => l.Kind == ListKinds.Reading));   // reading (лише манга)
        Assert.Equal(2, rows.Count(l => l.Kind == ListKinds.OnHold));    // on_hold аніме + манга
        Assert.Equal(2, rows.Count(l => l.Kind == ListKinds.Dropped));   // dropped аніме + манга
        Assert.Equal(2, rows.Count(l => l.Kind == ListKinds.Watchlist)); // planned аніме + манга

        var stored = await db.ImportJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal("done", stored.Status);
        var s = stored.Stats!.RootElement;
        Assert.Equal(1, s.GetProperty("animeWatched").GetInt32());
        Assert.Equal(1, s.GetProperty("mangaRead").GetInt32());
        Assert.Equal(1, s.GetProperty("watchingAdded").GetInt32());
        Assert.Equal(1, s.GetProperty("readingAdded").GetInt32());
        Assert.Equal(2, s.GetProperty("onHoldAdded").GetInt32());
        Assert.Equal(2, s.GetProperty("droppedAdded").GetInt32());
        Assert.Equal(2, s.GetProperty("watchlistAdded").GetInt32());
        Assert.Equal(0, s.GetProperty("skipped").GetInt32());
    }

    [Fact]
    public async Task Reimport_With_New_Mapping_Is_Idempotent_No_Kind_Duplicates()
    {
        var (db, importer) = New("import-reimport-all-statuses", AllStatusesAnimeJson, AllStatusesMangaJson);
        var first = NewJob(profileId: 2);
        db.ImportJobs.Add(first);
        await db.SaveChangesAsync();
        Assert.True(await importer.RunAsync(first.Id));
        var afterFirst = await db.ListEntries.ToListAsync();
        Assert.Equal(10, afterFirst.Count);

        // Повторний прогін (нова джоба, той самий профіль) — 0 задублів, kind не змінились.
        var second = NewJob(profileId: 2);
        db.ImportJobs.Add(second);
        await db.SaveChangesAsync();
        Assert.True(await importer.RunAsync(second.Id));

        var afterSecond = await db.ListEntries.ToListAsync();
        Assert.Equal(afterFirst.Count, afterSecond.Count);
        Assert.Equal(1, afterSecond.Count(l => l.Kind == ListKinds.Watching));
        Assert.Equal(1, afterSecond.Count(l => l.Kind == ListKinds.Reading));
        Assert.Equal(2, afterSecond.Count(l => l.Kind == ListKinds.OnHold));
        Assert.Equal(2, afterSecond.Count(l => l.Kind == ListKinds.Dropped));
        Assert.Equal("done", (await db.ImportJobs.SingleAsync(j => j.Id == second.Id)).Status);
    }

    [Fact]
    public async Task Reimport_Moves_Watchlist_Entry_To_Watching_Not_Duplicates()
    {
        // Стара мапінг-хвиля поклала watching-статус у watchlist; повторний імпорт
        // має ПЕРЕНЕСТИ запис у watching (UNIQUE profile+kind+title), а не задублювати.
        var (db, importer) = New("import-move-watchlist-to-watching", WatchingAnimeJson);
        var title = db.Titles.Add(new Title
        {
            ShikimoriId = 1535, Type = "anime", TitleEn = "Steins;Gate", Year = 2011,
        }).Entity;
        await db.SaveChangesAsync();
        db.ListEntries.Add(new ListEntry
        {
            ProfileId = 2, Kind = ListKinds.Watchlist, TitleId = title.Id,
            Rating = 7, CreatedAt = DateTime.UtcNow.AddDays(-30),
        });
        await db.SaveChangesAsync();

        var job = NewJob(profileId: 2);
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();
        Assert.True(await importer.RunAsync(job.Id));

        Assert.Equal(1, await db.ListEntries.CountAsync()); // НЕ задублювало
        var row = await db.ListEntries.SingleAsync();
        Assert.Equal(ListKinds.Watching, row.Kind); // перенесено watchlist → watching
        Assert.Equal(7, row.Rating);                // наявну оцінку не затерто (score=0 → rating null)
        Assert.Null(row.WatchedAt);
        Assert.Equal(1, await db.Titles.CountAsync()); // тайтл замерджений, дубля нема
    }

    [Fact]
    public async Task Import_With_User_ProfileId_Writes_To_Own_Profile_Never_Guest()
    {
        var (db, importer) = New("import-profile-own");
        var job = NewJob(profileId: 2);
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();

        var ok = await importer.RunAsync(job.Id);

        Assert.True(ok);
        var stored = await db.ImportJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal("done", stored.Status);
        Assert.Equal(2, stored.ProfileId); // profile_id джоби ≠ NULL і ≠ гостьовий 1

        var rows = await db.ListEntries.ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(2, r.ProfileId)); // жодного запису в гостя (профіль 1)
        Assert.Equal(1, rows.Count(l => l.Kind == ListKinds.Watched)); // completed → watched
        Assert.Equal(10, rows.Single(l => l.Kind == ListKinds.Watched).Rating);
        Assert.Equal(1, await db.Titles.CountAsync(t => t.ShikimoriId == 5114));
    }

    [Fact]
    public async Task Reimport_Same_User_Profile_Is_Idempotent()
    {
        var (db, importer) = New("import-profile-idempotent");
        var first = NewJob(profileId: 2);
        db.ImportJobs.Add(first);
        await db.SaveChangesAsync();
        Assert.True(await importer.RunAsync(first.Id));
        var afterFirst = await db.ListEntries.CountAsync();
        Assert.Equal(1, afterFirst);

        // Повторний прогін (нова джоба, той самий профіль) — без дублів, лише оновлення rating.
        var second = NewJob(profileId: 2);
        db.ImportJobs.Add(second);
        await db.SaveChangesAsync();
        Assert.True(await importer.RunAsync(second.Id));

        Assert.Equal(afterFirst, await db.ListEntries.CountAsync());
        Assert.Equal(1, await db.Titles.CountAsync());
        Assert.Equal("done", (await db.ImportJobs.SingleAsync(j => j.Id == second.Id)).Status);
    }

    [Fact]
    public async Task Import_Without_ProfileId_Resolves_Designated_Guest_Legacy()
    {
        // Legacy анонім: NULL → GuestProfileService (designated guest profile; MD-01 —
        // приріплений до акаунта профіль гостьовим не вважається).
        var (db, importer) = New("import-profile-guest-legacy");
        var job = NewJob(profileId: null);
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();

        Assert.True(await importer.RunAsync(job.Id));

        var rows = await db.ListEntries.ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(1, r.ProfileId)); // designated guest profile
    }

    [Fact]
    public async Task Import_With_Unknown_ProfileId_Fails_Job_Honestly()
    {
        var (db, importer) = New("import-profile-unknown");
        var job = NewJob(profileId: 42);
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();

        Assert.True(await importer.RunAsync(job.Id));

        var stored = await db.ImportJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal("failed", stored.Status);
        Assert.Equal(0, await db.ListEntries.CountAsync()); // нічого не записано
    }
}

/// <summary>
/// Контролер-рівень (Codex IV-01): POST /api/import/jobs і GET /api/import/jobs/{id}
/// під реальним хостом (InMemory БД). Фоновий worker імпорту в тестах вимкнено
/// (реальних HTTP-викликів нема) — джоба лишається queued, profile_id перевіряємо в БД.
/// </summary>
public class ImportControllerProfileTests
{
    private const int ForeignProfileId = 9876;

    private sealed class NoImportWorkerFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = $"import-api-{Guid.NewGuid():N}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GUEST_PROFILE_ID"] = "1",
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<MovieDiscoveryDbContext>>();
                services.AddDbContext<MovieDiscoveryDbContext>(o =>
                    o.UseInMemoryDatabase(DbName));
                // Лишаємо всі hosted-сервіси, КРІМІ імпорт-воркера (мережі в тестах нема):
                // GenericWebHostService та інші workers лишаються, як у ApiTestFactory.
                var importWorker = services
                    .Where(d => d.ImplementationType == typeof(ImportBackgroundService))
                    .ToList();
                foreach (var d in importWorker) services.Remove(d);
            });
        }

        public async Task SeedAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Profiles.Add(new Core.Domain.Profile { Id = 1, Name = "Guest", IsDefault = true });
            db.Profiles.Add(new Core.Domain.Profile { Id = ForeignProfileId, Name = "Victim", IsDefault = false });
            await db.SaveChangesAsync();
        }

        public async Task<HttpClient> NewUserClientAsync(string email, string username, string password)
        {
            var client = CreateDefaultClient(new CookieHandler());
            var reg = await ApiTestFactory.PostJson(
                client, "/api/auth/register", new { email, username, password });
            if ((int)reg.StatusCode is 409 or 400)
                await ApiTestFactory.PostJson(client, "/api/auth/login", new { email, password });
            return client;
        }
    }

    // ─── Позитивні регресії ───

    [Fact]
    public async Task CreateJob_Authenticated_Stores_Own_ProfileId()
    {
        var factory = new NoImportWorkerFactory();
        await factory.SeedAsync();
        var client = await factory.NewUserClientAsync("imp@t.ua", "impUser1", "Passw0rdX");

        var resp = await ApiTestFactory.PostJson(
            client, "/api/import/jobs", new { source = "shikimori", username = "Big_Pig" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var identityId = db.Users.First(u => u.UserName == "impUser1").Id;
        var ownProfileId = db.UserProfiles.First(x => x.UserId == identityId).ProfileId;

        var job = db.ImportJobs.AsNoTracking().Single();
        Assert.NotNull(job.ProfileId);      // profile_id імпорт-джоби ≠ NULL
        Assert.NotEqual(1, job.ProfileId);  // НЕ спільний гостьовий профіль
        Assert.Equal(ownProfileId, job.ProfileId); // власний профіль залогіненого юзера

        // Власник читає СВОЮ джобу — 200.
        var ownGet = await client.GetAsync($"/api/import/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.OK, ownGet.StatusCode);
    }

    [Fact]
    public async Task CreateJob_Anonymous_Guest_Path_Still_Works()
    {
        var factory = new NoImportWorkerFactory();
        await factory.SeedAsync();
        var anonymous = factory.CreateDefaultClient(new CookieHandler());

        // Анонім БЕЗ profileId → legacy guest-path (designated guest profile), 202.
        var resp = await ApiTestFactory.PostJson(
            anonymous, "/api/import/jobs", new { source = "shikimori", username = "some_nick" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var job = db.ImportJobs.AsNoTracking().Single();
        Assert.Equal(1, job.ProfileId); // чесний гостьовий profile_id (не NULL, не чужий)

        // Анонім-власник читає гостьову джобу — 200.
        var guestGet = await anonymous.GetAsync($"/api/import/jobs/{job.Id}");
        Assert.Equal(HttpStatusCode.OK, guestGet.StatusCode);
    }

    // ─── Негативні (Codex IV-01, P1 безпеки) ───

    [Fact]
    public async Task Anonymous_Cannot_Target_Another_Users_Profile()
    {
        var factory = new NoImportWorkerFactory();
        await factory.SeedAsync();
        var anonymous = factory.CreateDefaultClient(new CookieHandler());

        // Експлойт хвилі-1: анонім + чужий ІСНУЮЧИЙ profileId → був 202, має бути 401/403.
        var foreign = await ApiTestFactory.PostJson(anonymous, "/api/import/jobs",
            new { source = "shikimori", username = "some_nick", profileId = ForeignProfileId });
        Assert.True(foreign.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Anonymous request targeted foreign profile {ForeignProfileId}: HTTP {(int)foreign.StatusCode}");

        // І невідомий profileId теж не ціль (ані 202, ані 404-підказка про існування).
        var unknown = await ApiTestFactory.PostJson(anonymous, "/api/import/jobs",
            new { source = "shikimori", username = "some_nick", profileId = 999999 });
        Assert.True(unknown.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Anonymous request with unknown profileId: HTTP {(int)unknown.StatusCode}");

        Assert.Equal(0, await CountJobsAsync(factory));
    }

    [Fact]
    public async Task Authenticated_User_Targeting_Another_Profile_Is_Forbidden()
    {
        var factory = new NoImportWorkerFactory();
        await factory.SeedAsync();
        var clientA = await factory.NewUserClientAsync("imp2@t.ua", "impUser2", "Passw0rdX");

        // Юзер A → чужий профіль 9876 → 403 (чесна відмова, не мовчазне ігнорування).
        var foreign = await ApiTestFactory.PostJson(clientA, "/api/import/jobs",
            new { source = "shikimori", username = "Big_Pig", profileId = ForeignProfileId });
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        // Гостьовий профіль 1 — теж не ціль для залогіненого.
        var guest = await ApiTestFactory.PostJson(clientA, "/api/import/jobs",
            new { source = "shikimori", username = "Big_Pig", profileId = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, guest.StatusCode);

        // А ВЛАСНИЙ profileId (якщо клієнт його знає) — валідний.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var identityId = db.Users.First(u => u.UserName == "impUser2").Id;
        var ownProfileId = db.UserProfiles.First(x => x.UserId == identityId).ProfileId;
        var own = await ApiTestFactory.PostJson(clientA, "/api/import/jobs",
            new { source = "shikimori", username = "Big_Pig", profileId = ownProfileId });
        Assert.Equal(HttpStatusCode.Accepted, own.StatusCode);
        var queuedJob = db.ImportJobs.AsNoTracking().Single();
        Assert.Equal(ownProfileId, queuedJob.ProfileId);
        Assert.NotEqual(1, queuedJob.ProfileId);
    }

    [Fact]
    public async Task GetJob_Foreign_Owner_Gets_NotFound()
    {
        var factory = new NoImportWorkerFactory();
        await factory.SeedAsync();
        var clientA = await factory.NewUserClientAsync("imp3@t.ua", "impUser3", "Passw0rdX");
        var clientB = await factory.NewUserClientAsync("imp4@t.ua", "impUser4", "Passw0rdX");
        var anonymous = factory.CreateDefaultClient(new CookieHandler());

        var resp = await ApiTestFactory.PostJson(
            clientA, "/api/import/jobs", new { source = "shikimori", username = "Big_Pig" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var jobId = (await ReadJsonAsync(resp))!.RootElement.GetProperty("jobId").GetInt32();

        // Власник — 200; інший юзер і анонім — 404 (існування не розкриваємо).
        Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync($"/api/import/jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/import/jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/import/jobs/{jobId}")).StatusCode);

        // Дзеркально: анонімна гостьова джоба — гість бачить, юзери — ні.
        var guestJob = await ApiTestFactory.PostJson(
            anonymous, "/api/import/jobs", new { source = "mal", username = "legacy_nick" });
        Assert.Equal(HttpStatusCode.Accepted, guestJob.StatusCode);
        var guestJobId = (await ReadJsonAsync(guestJob))!.RootElement.GetProperty("jobId").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/import/jobs/{guestJobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clientA.GetAsync($"/api/import/jobs/{guestJobId}")).StatusCode);
    }

    private static async Task<int> CountJobsAsync(NoImportWorkerFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        return await db.ImportJobs.AsNoTracking().CountAsync();
    }

    private static async Task<System.Text.Json.JsonDocument?> ReadJsonAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return text.Length == 0 ? null : System.Text.Json.JsonDocument.Parse(text);
    }
}
