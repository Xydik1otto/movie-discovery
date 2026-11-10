using System.Globalization;
using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Memory;

namespace MovieDiscovery.Api.Extensions;

/// <summary>
/// Бекенд-перф хвиля (11.11): стиснення відповідей + output-cache «каталожних» GET-видач
/// + опційні розміри пам'яті кешів (Perf:* конфіг).
///
/// САМОДОСТАТНІЙ: реєструє лише сервіси; middleware у пайплайні додає Program.cs:
/// <code>
///   app.UseResponseCompression();   // якомога раніше, до UseCors
///   app.UseCors();
///   ...
///   app.UseForwardedHeaders();
///   app.UseOutputCache();           // після UseCors/UseForwardedHeaders, до UseAuthentication/MapControllers
/// </code>
///
/// ПОРЯДОК КРИТИЧНИЙ:
/// - ResponseCompression має бути ЗОВНІ (before) OutputCache: кеш зберігає НЕстиснутий
///   боді, gzip/brotli застосовується per-запит на виході. Навпаки — у кеш потрапили б
///   стиснуті байти, і клієнт без Accept-Encoding отримав би бінарний шлунок.
/// - UseOutputCache ПІСЛЯ UseCors (вимога docs OutputCache) і після UseForwardedHeaders.
///
/// Політика catalog (base /api/search | /api/similar | /api/title/, GET, 60 с, vary "*"):
/// - персональні зони (lists/user/auth/admin/profiles/bot) СВІДОМО поза базами —
///   не кешуються взагалі;
/// - префікс /api/title/ (зі слешем) навмисно не чіпає /api/titles/{id}/watch і
///   /api/titles/{id}/trailers (tri-state доступності/трейлери мають власну свіжість);
/// - vary "*" покриває lang та будь-які інші query-параметри;
/// - дефолтна політика OutputCache додатково страхує: зберігаються лише анонімні
///   GET/HEAD зі статусом 200 без Set-Cookie — куки-юзери не читають і не наповнюють кеш.
/// </summary>
public static class PerfExtensions
{
    /// <summary>Ім'я каталожної політики (документаційне: політика висить як base-policy).</summary>
    public const string CatalogPolicyName = "catalog";

    /// <summary>Base-префікси каталожних видань (case-insensitive; /api/title/ — зі слешем).</summary>
    internal static readonly string[] CatalogBasePaths = ["/api/search", "/api/similar", "/api/title/"];

    /// <summary>TTL output-cache каталожних видань, с.</summary>
    internal const int CatalogDurationSeconds = 60;

    public static IServiceCollection AddPerfOptimizations(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Ідемпотентність: тест-фабрики (та майбутній Program.cs) реєструють поверх
        // вже-зареєстрованих сервісів — повторні AddBasePolicy додавали б дублікати
        // політик у BasePolicies, повторний AddResponseCompression — зайві options-конфіги.
        if (services.Any(d => d.ImplementationType == typeof(PerfMarker)))
            return services;
        services.AddSingleton<PerfMarker>();

        var perf = configuration.GetSection("Perf");

        // ── (a) Response compression: brotli + gzip, рівень Optimal, HTTPS вимкнено ──
        // Внутрішній контур: TLS термінується зовні (тунель/проксі), тому стиснення
        // по HTTPS лишається вимкненим (дефолт EnableForHttps=false зафіксовано явно).
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = false;
            // Wildcard text/* підтримується провайдером (ShouldCompressPartial) +
            // application/json для API-відповідей. Список свідомо вузький: стискаємо
            // JSON/текст, бінарники (постери тощо) не чіпаємо.
            o.MimeTypes = new[] { "text/*", "application/json" };
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);

        // ── (b) Output cache: каталожні GET-видання на 60 с ──
        services.AddOutputCache(o =>
        {
            // Base-policy (а не іменована + [OutputCache]-атрибут на контролерах):
            // hot-path контролери не змінюються. Політика гейтується предикатом
            // шлях+метод; для решти запитів middleware — чистий passthrough.
            o.AddBasePolicy(b => b
                .With(ctx => IsCatalogGet(ctx))
                .Expire(TimeSpan.FromSeconds(CatalogDurationSeconds))
                // "*" — варіювання за ВСІМА query-ключами: покриває lang і фільтри.
                .SetVaryByQuery(["*"]));

            // (c) Розміри output-cache — опційно з конфігу (Perf:OutputCache:*).
            if (long.TryParse(perf["OutputCache:SizeLimitBytes"], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var sizeLimit) && sizeLimit > 0)
                o.SizeLimit = sizeLimit;
            if (long.TryParse(perf["OutputCache:MaximumBodySizeBytes"], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var maxBody) && maxBody > 0)
                o.MaximumBodySize = maxBody;
        });

        // (c') Гігієна IMemoryCache (TTL-кеш SearchController / titlesCount у health) —
        // опційно. SizeLimit СВІДОМО не виставляємо: наявні cache.Set(...) не задають
        // Size і на SizeLimit-кеші кидали б InvalidOperationException.
        if (double.TryParse(perf["MemoryCache:CompactionPercentage"], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var compaction) && compaction is > 0 and <= 1)
            services.Configure<MemoryCacheOptions>(m => m.CompactionPercentage = compaction);
        if (double.TryParse(perf["MemoryCache:ExpirationScanFrequencySeconds"], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var scan) && scan > 0)
            services.Configure<MemoryCacheOptions>(m => m.ExpirationScanFrequency = TimeSpan.FromSeconds(scan));

        return services;
    }

    /// <summary>Предикат політики: лише GET за каталожними префіксами (case-insensitive).</summary>
    internal static bool IsCatalogGet(OutputCacheContext ctx)
    {
        var request = ctx.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method)) return false;
        var path = request.Path.Value ?? string.Empty;
        foreach (var basePath in CatalogBasePaths)
            if (path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Маркер ідемпотентності реєстрації (див. guard у AddPerfOptimizations).</summary>
    private sealed class PerfMarker
    {
    }
}
