using Microsoft.Extensions.Caching.Memory;
using MovieDiscovery.Api.Controllers;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Api;

/// <summary>
/// Прогрів і рефреш метадати фільтрів (/api/filters). Холодний агрегат по 2.86 млн
/// тайтлів коштує десятки секунд (замір 11.11: 'all' ~53с, dorama ~17с, серіальні
/// категорії >30с), а TTL-кеш контролера експайрив → перший відвідувач категорії
/// платив повну вартість (і ще й змагався за IO з рештою запитів).
/// Warmup на старті + періодичний рефреш (інтервал < TTL) тримають кеш ВІЧНИ теплим:
/// користувач ніколи не бачить холодного обчислення, а свіжість чисел після синків
/// каталогу обмежена лише інтервалом рефрешу.
/// </summary>
public sealed class FiltersWarmupService(
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory,
    ILogger<FiltersWarmupService> logger) : BackgroundService
{
    /// <summary>Рефреш-інтервал має бути МЕНШИМ за FiltersController.CacheTtl (60 хв),
    /// щоб кеш не встигав експайрити між циклами.</summary>
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(45);

    /// <summary>Пауза після старту: міграції/фонові синхи осідають, не змагаємось
    /// за IO у перші секунди життя API.</summary>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var category in new[] { "all" }.Concat(FiltersController.Categories.Where(c => c != "all")))
                {
                    if (ct.IsCancellationRequested)
                        break;
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var response = await FiltersController.BuildResponse(db, category, ct);
                        cache.Set(FiltersController.CacheKey(category), response, FiltersController.CacheTtl);
                        logger.LogInformation(
                            "Filters warmup: {Category} прогріто за {Ms} мс", category, sw.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Збій однієї категорії не валить цикл — наступний рефреш спробує знову.
                        logger.LogWarning(
                            ex, "Filters warmup: категорія {Category} не прогрілась", category);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Delay(RefreshInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
