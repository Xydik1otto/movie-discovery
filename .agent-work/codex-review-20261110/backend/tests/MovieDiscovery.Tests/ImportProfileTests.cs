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

namespace MovieDiscovery.Tests;

/// <summary>
/// Дефект хвилі-1 (1): імпорт залогіненого юзера мусить писати у ВЛАСНИЙ профіль
/// (import_jobs.profile_id ≠ NULL, lists належать профілю юзера), а не у спільний
/// гостьовий/дефолтний профіль (id=1), де імпортовані списки стають невидимими юзеру.
/// Гостьовий профіль лишається лише для анонімного legacy-шляху. Реальних HTTP-викликів
/// нема: Shikimori відповідає через StubHttpHandler.
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

    private static (MovieDiscoveryDbContext Db, ImportService Importer) New(string name)
    {
        var db = TestDb.Create(name);
        // Дефолтний (гостьовий) профіль присутній — джоба юзера НЕ має туди потрапити.
        db.Profiles.Add(new Profile { Id = 1, Name = "Guest", IsDefault = true });
        db.Profiles.Add(new Profile { Id = 2, Name = "Own", IsDefault = false });

        var shikimori = new ShikimoriClient(
            StubHttp.Client(r =>
            {
                var pq = r.RequestUri!.PathAndQuery;
                if (pq.StartsWith("/users/someuser")) return StubHttp.Json(UserResolveJson);
                if (pq.Contains("anime_rates")) return StubHttp.Json(AnimeRatesJson);
                if (pq.Contains("manga_rates")) return StubHttp.Json("[]");
                return null;
            }),
            new TestDb.CacheStub(),
            new ShikimoriThrottle(),
            Logs.Null<ShikimoriClient>());

        var jikan = new JikanClient(
            StubHttp.Client(_ => null), new TestDb.CacheStub(), new JikanThrottle(), Logs.Null<JikanClient>());
        var anilist = new AniListClient(
            StubHttp.Client(_ => null), new TestDb.CacheStub(), new AniListThrottle(), Logs.Null<AniListClient>());

        return (db, new ImportService(db, jikan, shikimori, anilist, Logs.Null<ImportService>()));
    }

    private static ImportJob NewJob(int? profileId) => new()
    {
        Source = ImportSourceCatalog.Shikimori,
        Username = "someuser",
        ProfileId = profileId,
        Status = "queued",
    };

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
    public async Task Import_Without_ProfileId_Falls_Back_To_Default_Guest_Legacy()
    {
        // Анонімний legacy-шлях НЕ змінено: null → дефолтний (гостьовий) профіль.
        var (db, importer) = New("import-profile-guest-legacy");
        var job = NewJob(profileId: null);
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync();

        Assert.True(await importer.RunAsync(job.Id));

        var rows = await db.ListEntries.ToListAsync();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(1, r.ProfileId)); // дефолтний гостьовий профіль
    }
}

/// <summary>
/// Інтеграційний рівень (дефект хвилі-1 (1), контролер): POST /api/import/jobs під
/// cookie-юзером мусить проставити джобі ВЛАСНИЙ profile_id юзера (не 1, не NULL);
/// анонімний запит лишає legacy-поведінку (profile_id = явний id клієнта або NULL).
/// Фоновий worker імпорту в тестах вимкнено (реальних HTTP-викликів нема) — джоба
/// лишається queued, profile_id перевіряємо прямо в БД.
/// </summary>
public class ImportControllerProfileTests
{
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

    [Fact]
    public async Task CreateJob_Authenticated_User_Stores_Own_ProfileId()
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
    }

    [Fact]
    public async Task CreateJob_Authenticated_Ignores_Foreign_ProfileId_From_Client()
    {
        var factory = new NoImportWorkerFactory();
        await factory.SeedAsync();
        var client = await factory.NewUserClientAsync("imp2@t.ua", "impUser2", "Passw0rdX");

        // profileId клієнта (гостьовий 1) для cookie-юзера ігнорується — як у списках (MD-01).
        var resp = await ApiTestFactory.PostJson(
            client, "/api/import/jobs", new { source = "shikimori", username = "Big_Pig", profileId = 1 });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var identityId = db.Users.First(u => u.UserName == "impUser2").Id;
        var ownProfileId = db.UserProfiles.First(x => x.UserId == identityId).ProfileId;
        var job = db.ImportJobs.AsNoTracking().Single();
        Assert.Equal(ownProfileId, job.ProfileId);
        Assert.NotEqual(1, job.ProfileId);
    }

    [Fact]
    public async Task CreateJob_Anonymous_Keeps_Legacy_Null_ProfileId()
    {
        var factory = new NoImportWorkerFactory();
        await factory.SeedAsync();
        var client = factory.CreateDefaultClient(new CookieHandler());

        var resp = await ApiTestFactory.PostJson(
            client, "/api/import/jobs", new { source = "shikimori", username = "some_nick" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
        var job = db.ImportJobs.AsNoTracking().Single();
        Assert.Null(job.ProfileId); // legacy: NULL → ImportService падає на дефолтний (гостьовий) профіль
    }

    [Fact]
    public async Task Codex_Anonymous_Must_Not_Target_Another_Users_Profile()
    {
        using var factory = new NoImportWorkerFactory();
        await factory.SeedAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Profiles.Add(new Profile { Id=9876, Name="Victim profile", IsDefault=false });
            await db.SaveChangesAsync();
        }
        using var anonymous = factory.CreateClient();
        var response = await ApiTestFactory.PostJson(anonymous,"/api/import/jobs",
            new { source="shikimori", username="some_nick", profileId=9876 });
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Anonymous request targeted foreign profile9876: HTTP {(int)response.StatusCode}");
    }
}
