using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Infrastructure.AnimeManga;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.Cinemeta;

/// <summary>
/// Джерело постерів/бекдропів без ключа: Cinemeta (метадата-адон Stremio, публічний CDN
/// images.metahub.space). GET /meta/{movie|series}/{imdb_id}.json → poster/background —
/// абсолютні URL. Жодної реєстрації/ключа. Захист від фейкових збігів (Cinemeta на
/// неіснуючий imdb_id повертає рандомний тайтл): валідація назви проти очікуваної.
/// </summary>
public interface ICinemetaImagesService
{
    /// <summary>Cinemeta вмиканий (env CINEMETA_ENABLED=false вимикає чесно).</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Постер/бекдроп за imdb_id. expectedTitle — назва з нашого каталогу (TitleEn):
    /// без неї запит НЕ робиться (неможливо відсіяти фейковий збіг); назви не збіглися
    /// (нормалізовано, регістр/розділові ігноровано) → null. TmdbId у результаті = 0
    /// (Cinemata не дає tmdb_id — бекфіл не робимо).
    /// </summary>
    Task<TmdbImdbResolved?> ResolveByImdbAsync(string imdbId, string type, string? expectedTitle, CancellationToken ct = default);
}

/// <summary>Cinemata — публічний CDN: чемний інтервал ≥300 мс між запитами.</summary>
public sealed class CinemetaThrottle() : IntervalThrottle(TimeSpan.FromMilliseconds(300));

public sealed class CinemetaImagesService(
    HttpClient http,
    CinemetaThrottle throttle,
    IConfiguration cfg,
    ILogger<CinemetaImagesService> log) : ICinemetaImagesService
{
    public bool IsConfigured => cfg["CINEMETA_ENABLED"] != "false";

    /// <summary>Нормалізація назви для порівняння: lowercase, лише букви/цифри (кирилицю теж).</summary>
    public static string? Normalize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return new string(title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    public async Task<TmdbImdbResolved?> ResolveByImdbAsync(string imdbId, string type, string? expectedTitle, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        var expected = Normalize(expectedTitle);
        if (expected is null) return null; // нема з чим звірити → не ризикуємо фейковим постером

        var kind = type == "movie" ? "movie" : "series";
        var json = await throttle.RunAsync(async () => await FetchAsync($"/meta/{kind}/{imdbId}.json", ct), ct);
        if (json is null) return null;

        try
        {
            return Parse(json, expected);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Не вдалося розібрати відповідь Cinemata ({ImdbId})", imdbId);
            return null;
        }
    }

    /// <summary>
    /// Парсер відповіді Cinemata: meta.imdb_id має збігатися із запитаним, назва — з очікуваною;
    /// інакше (фейковий збіг на неіснуючий id) → null. poster /small/ → /original/,
    /// background лишається /medium/.
    /// </summary>
    public static TmdbImdbResolved? Parse(string json, string expectedNormalized)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            return null;
        var id = meta.TryGetProperty("imdb_id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
        var name = meta.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString() : null;
        var poster = meta.TryGetProperty("poster", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
        var background = meta.TryGetProperty("background", out var bp) && bp.ValueKind == JsonValueKind.String ? bp.GetString() : null;
        if (poster is null || name is null) return null;
        if (!string.Equals(Normalize(name), expectedNormalized, StringComparison.Ordinal)) return null;
        poster = poster.Replace("/small/", "/original/", StringComparison.Ordinal);
        return new TmdbImdbResolved(TmdbId: 0, PosterPath: poster, BackdropPath: background);
    }

    /// <summary>GET з ретраями: 429 — Retry-After (кап 3с), 502/503/504 — 1с backoff; інакше null.</summary>
    private async Task<string?> FetchAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                resp = await http.SendAsync(req, ct);
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // таймаут запиту
            }

            if ((int)resp.StatusCode == 200)
            {
                return await resp.Content.ReadAsStringAsync(ct);
            }
            if ((int)resp.StatusCode == 429 && attempt < 2)
            {
                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                resp.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 0.2, 3)), ct);
                continue;
            }
            if ((int)resp.StatusCode is 502 or 503 or 504 && attempt < 2)
            {
                resp.Dispose();
                await Task.Delay(1000, ct);
                continue;
            }
            resp.Dispose();
            return null;
        }
    }
}
