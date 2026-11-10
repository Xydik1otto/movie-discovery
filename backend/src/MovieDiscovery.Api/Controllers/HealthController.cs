using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Api.Controllers;

/// <summary>MD-07: чесне здоров'я. GET /api/health — ЗАВЖДИ 200 (liveness, фронтенд на ньому живе),
/// але status чесний: «ok» при живій БД, «degraded» при недоступній; ollama=false чесно означає
/// вимкнену семантику. GET /api/health/ready — readiness: 200 при живій БД, 503 при недоступній.
/// MD-09: liveness-проба БД — дешевий SELECT 1 (CountAsync по 2.8 млн рядків давало ~2.6 с
/// на кожен виклик health, а фронтенд викликає його на кожному завантаженні сторінки).
/// Швидкість (живий замір prod: перший виклик після паузи &gt;60 с — ~6 с, наступні — ~25 мс):
/// повільним «перший health після паузи» робив холодний COUNT titlesCount (кеш 60 с не
/// допомагає при поодиноких ручних перевірках), а не ollama (вона відвалюється швидко).
/// Тому тут: ollama-проба з жорстким капом 1 с (linked CTS CancelAfter + WhenAny-таймер —
/// навіть якщо реалізація ігнорує токен) і кеш статусу 30 с; проби паралельно, крім
/// DbContext (db-проба окремо, з нею паралелиться лише ollama); titlesCount — свіже
/// значення (≤60 с) одразу, прострочене — stale миттєво + фоновий перерахунок, холодний
/// кеш — синхронний COUNT із бюджетом 0.75 с (не встигли → 0 «ще не міряли» + фон).</summary>
[ApiController]
[Route("api/health")]
public sealed class HealthController(
    MovieDiscoveryDbContext db,
    IEmbedder embedder,
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory) : ControllerBase
{
    /// <summary>Свіжість titlesCount: у межах вікна значення вважається точним і не перераховується.</summary>
    internal const int CountFreshSeconds = 60;

    /// <summary>Ретенція останнього відомого titlesCount для stale-віддачі (статистика, не liveness).</summary>
    private static readonly TimeSpan CountRetention = TimeSpan.FromHours(24);

    /// <summary>Бюджет синхронного COUNT на холодному кеші: health не гальмуємо — добере фон.</summary>
    private static readonly TimeSpan ColdCountBudget = TimeSpan.FromMilliseconds(750);

    /// <summary>Жорсткий кап ollama-проби: зависла/мертва ollama не має робити health повільним.</summary>
    private static readonly TimeSpan OllamaProbeTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Ollama-статус кешується 30 с: проба не на кожен health-запит.</summary>
    internal const int OllamaTtlSeconds = 30;

    internal const string CountCacheKey = "health:titles-count";
    internal const string OllamaCacheKey = "health:ollama";

    /// <summary>Один фоновий перерахунок titlesCount за раз (static: контролер живе один запит).</summary>
    private static int _countRefreshRunning;

    /// <summary>Кешоване значення лічильника з моментом свіжості (stale-while-revalidate).</summary>
    internal sealed record TitlesCountValue(int Value, DateTimeOffset RefreshedAt);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        // DbContext НЕ паралелимо: db-проба окремо (CanConnect → COUNT — той самий контекст,
        // послідовно); ollama (без DbContext) — паралельно з db-пробою.
        var dbTask = ProbeDbAsync(ct);
        var ollamaTask = ProbeOllamaAsync(ct);
        await Task.WhenAll(dbTask, ollamaTask);
        var dbOk = dbTask.Result;
        var titlesCount = await TitlesCountAsync(dbOk, ct);
        return Ok(new { status = Status(dbOk), db = dbOk, ollama = ollamaTask.Result, titlesCount });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken ct)
    {
        var dbTask = ProbeDbAsync(ct);
        var ollamaTask = ProbeOllamaAsync(ct);
        await Task.WhenAll(dbTask, ollamaTask);
        var dbOk = dbTask.Result;
        return dbOk
            ? Ok(new { status = Status(true), db = true, ollama = ollamaTask.Result, titlesCount = await TitlesCountAsync(true, ct) })
            : StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { status = Status(false), db = false, ollama = ollamaTask.Result, titlesCount = 0 });
    }

    /// <summary>MD-07: status=ok лише при живій БД; db=false → чесний degraded (не «ok»).</summary>
    public static string Status(bool dbOk) => dbOk ? "ok" : "degraded";

    /// <summary>Liveness-проба: дешева перевірка з'єднання (не COUNT по каталозі).</summary>
    private async Task<bool> ProbeDbAsync(CancellationToken ct)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ollama-проба з ЖОРСТКИМ капом 1 с: linked CTS + CancelAfter (для embedder'ів, що
    /// честь токен) І Task.WhenAny із таймером (навіть якщо реалізація токен ігнорує —
    /// health не чекатиме довше 1 с). Статус кешується 30 с: мертва/зависла ollama
    /// не робить кожен health-дзвінок повільним.
    /// </summary>
    private Task<bool> ProbeOllamaAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync(OllamaCacheKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(OllamaTtlSeconds);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(OllamaProbeTimeout);
                var probe = embedder.IsAvailableAsync(cts.Token);
                var budget = Task.Delay(OllamaProbeTimeout, CancellationToken.None);
                var winner = await Task.WhenAny(probe, budget);
                if (winner != probe)
                    return false; // таймаут: проба не відповіла вчасно
                return await probe.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        });

    /// <summary>
    /// titlesCount — статистика для UI, а не проба живості; НІКОЛИ не блокує наддовго:
    /// • свіже значення (≤60 с) — сервимо одразу;
    /// • прострочене, але наявне — stale миттєво + фоновий перерахунок (власний DI-scope:
    ///   request-scoped DbContext диспозиться разом із запитом, у фоні він мертвий);
    /// • холодний кеш (старт процесу) — синхронний COUNT із бюджетом 0.75 с: встигли →
    ///   точне значення; ні → 0 («ще не міряли», як і db=false-гілка) + фоновий добір.
    /// </summary>
    internal async Task<int> TitlesCountAsync(bool dbOk, CancellationToken ct)
    {
        if (!dbOk) return 0;
        if (cache.TryGetValue(CountCacheKey, out TitlesCountValue? held) && held is not null)
        {
            if (DateTimeOffset.UtcNow - held.RefreshedAt <= TimeSpan.FromSeconds(CountFreshSeconds))
                return held.Value;
            // stale-while-revalidate: останнє відоме віддаємо ЗРАЗУ, точність добирає фон.
            ScheduleCountRefresh();
            return held.Value;
        }

        var value = await ComputeCountCappedAsync(ct);
        if (value is { } v)
        {
            cache.Set(CountCacheKey, new TitlesCountValue(v, DateTimeOffset.UtcNow), CountRetention);
            return v;
        }

        ScheduleCountRefresh();
        return 0;
    }

    /// <summary>Синхронний COUNT із бюджетом (0.75 с): перевищив — null (добере фоновий
    /// перерахунок). Викликається ЛИШЕ після завершення ProbeDbAsync — DbContext не паралелиться.</summary>
    private async Task<int?> ComputeCountCappedAsync(CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ColdCountBudget);
            var count = db.Titles.CountAsync(timeoutCts.Token);
            var budget = Task.Delay(ColdCountBudget, CancellationToken.None);
            var winner = await Task.WhenAny(count, budget);
            if (winner != count) return null;
            return await count.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Фоновий перерахунок titlesCount: ВЛАСНИЙ DI-scope (IServiceScopeFactory —
    /// singleton, переживає запит). Ідемпотентний: дозаписує кеш свіжим значенням;
    /// невдача (БД недоступна) — stale лишається, наступний health повторить спробу.</summary>
    private void ScheduleCountRefresh()
    {
        if (Interlocked.CompareExchange(ref _countRefreshRunning, 1, 0) != 0)
            return; // перерахунок уже летить
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
                var value = await scopedDb.Titles.CountAsync();
                cache.Set(CountCacheKey, new TitlesCountValue(value, DateTimeOffset.UtcNow), CountRetention);
            }
            catch
            {
                // чесний stale: кеш не перезаписуємо помилкою; спроба повториться на наступному health.
            }
            finally
            {
                Interlocked.Exchange(ref _countRefreshRunning, 0);
            }
        });
    }
}
