using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Embeddings; // IHttpClientFactoryAccessor — без нових реєстрацій у Program.cs
using MovieDiscovery.Infrastructure.Tmdb; // TmdbJson — спільні JsonSerializerOptions

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Покриття постерів аніме/манги, яких TMDB не знає (tmdb_id NULL): за наявним зовнішнім id
/// тайтла тягнемо картинку з живого джерела — Shikimori (обов'язковий User-Agent, тротлінг
/// спільний ShikimoriThrottle) або Jikan (дзеркало MAL; dormant-джерело — вмикається env
/// JIKAN_URL, тому гілка має circuit-breaker: 3 підряд невдачі → вимкнена до кінця прогону).
/// Джерела-клієнти (ShikimoriClient/JikanClient) не мають per-id методів — тут власні легкі
/// GET через IHttpClientFactoryAccessor (без нових реєстрацій у Program.cs). Результат —
/// ГОТОВИЙ абсолютний URL (як ShikimoriMapper/JikanMappers пишуть у poster_path).
/// </summary>
public sealed class PosterCoverageService(
    IHttpClientFactoryAccessor httpFactory,
    IOptions<ShikimoriOptions> shikimoriOptions,
    IOptions<JikanOptions> jikanOptions,
    ShikimoriThrottle shikimoriThrottle,
    JikanThrottle jikanThrottle,
    ILogger<PosterCoverageService> log)
{
    public const string Source = "poster-coverage";

    private const string ShikimoriApiBase = "https://shikimori.one/api/"; // той самий хост, що в Program.cs для ShikimoriClient
    private const int RequestTimeoutSeconds = 15;
    private const int JikanFailureThreshold = 3;

    private readonly ShikimoriOptions _shikimori = shikimoriOptions.Value;
    private readonly JikanOptions _jikan = jikanOptions.Value;
    private int _jikanConsecutiveFailures;
    private bool _jikanDisabled;

    /// <summary>Постер для аніме/манги без TMDB: shikimori_id → Shikimori, інакше mal_id → Jikan. Інакше null.</summary>
    public async Task<string?> ResolvePosterAsync(Title title, CancellationToken ct = default)
    {
        if (title.TmdbId is not null) return null; // TMDB-шлях — TmdbImagesService
        if (title.Type is not ("anime" or "manga")) return null;
        var isManga = title.Type == "manga";

        if (title.ShikimoriId is > 0)
            return await ResolveShikimoriAsync(title.ShikimoriId.Value, isManga, ct);
        if (title.MalId is > 0)
            return await ResolveJikanAsync(title.MalId.Value, isManga, ct);

        return null; // немає жодного зовнішнього id — джерело встановити не можна
    }

    /// <summary>GET shikimori/api/{animes|mangas}/{id} → image (шлях-строка або {original|preview}) → абсолютний URL.</summary>
    public async Task<string?> ResolveShikimoriAsync(long shikimoriId, bool isManga, CancellationToken ct = default)
    {
        var path = $"{(isManga ? "mangas" : "animes")}/{shikimoriId}";
        var json = await shikimoriThrottle.RunAsync(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ShikimoriApiBase + path);
                req.Headers.UserAgent.ParseAdd(_shikimori.UserAgent); // ОБОВ'ЯЗКОВИЙ User-Agent Shikimori
                return await SendAsync(req, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "poster-coverage: Shikimori недоступний ({Path})", path);
                return null;
            }
        }, ct);
        if (json is null) return null;

        try
        {
            var media = JsonSerializer.Deserialize<ShikimoriMedia>(json, TmdbJson.Options);
            return ShikimoriPosterUrl(media?.Image);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "poster-coverage: не вдалося розібрати відповідь Shikimori ({Path})", path);
            return null;
        }
    }

    /// <summary>
    /// GET jikan/v4/{anime|manga}/{mal_id} → data.images.jpg.large_image_url. Джерело dormant
    /// (EOL/мережева блокировка): circuit-breaker — JikanFailureThreshold підряд невдач
    /// вимикають гілку до кінця життя сервісу (один прогін джоби = один scope).
    /// </summary>
    public async Task<string?> ResolveJikanAsync(long malId, bool isManga, CancellationToken ct = default)
    {
        if (_jikanDisabled) return null;
        var url = $"{_jikan.BaseUrl.TrimEnd('/')}/{(isManga ? "manga" : "anime")}/{malId}";
        string? json;
        try
        {
            json = await jikanThrottle.RunAsync(async () =>
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
                    using var resp = await httpFactory.CreateClient(Source).GetAsync(url, timeout.Token);
                    if (resp.IsSuccessStatusCode)
                        return await resp.Content.ReadAsStringAsync(timeout.Token);
                    if ((int)resp.StatusCode == 404)
                    {
                        _jikanConsecutiveFailures = 0; // джерело живе — просто в цього тайтла нема картинки
                        log.LogInformation("poster-coverage: Jikan 404 ({Url}) — картинки нема", url);
                        return null;
                    }
                    NoteJikanFailure((int)resp.StatusCode, url);
                    return null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "poster-coverage: Jikan недоступний ({Url})", url);
                    NoteJikanFailure(null, url);
                    return null;
                }
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        if (json is null) return null;
        _jikanConsecutiveFailures = 0;

        try
        {
            var single = JsonSerializer.Deserialize<JikanSingle<JikanMedia>>(json, TmdbJson.Options);
            return JikanPosterUrl(single?.Data);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "poster-coverage: не вдалося розібрати відповідь Jikan ({Url})", url);
            return null;
        }
    }

    /// <summary>«Джерело неживе» (5xx/429/403/таймаут): лічильник підряд → circuit-breaker до кінця прогону.</summary>
    private void NoteJikanFailure(int? statusCode, string url)
    {
        _jikanConsecutiveFailures++;
        if (_jikanDisabled) return;
        if (_jikanConsecutiveFailures < JikanFailureThreshold)
        {
            log.LogWarning("poster-coverage: Jikan {Status} на {Url} (невдач підряд: {N})",
                statusCode?.ToString() ?? "мережа", url, _jikanConsecutiveFailures);
            return;
        }
        _jikanDisabled = true;
        log.LogWarning("poster-coverage: Jikan не відповів {N} разів підряд — гілку вимкнено до кінця прогону",
            JikanFailureThreshold);
    }

    private async Task<string?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));
        using var resp = await httpFactory.CreateClient(Source).SendAsync(req, timeout.Token);
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadAsStringAsync(timeout.Token);
        if ((int)resp.StatusCode == 404)
            log.LogInformation("poster-coverage: Shikimori 404 на {Url} — картинки нема", req.RequestUri);
        else
            log.LogWarning("poster-coverage: Shikimori {Code} на {Url}", (int)resp.StatusCode, req.RequestUri);
        return null;
    }

    /// <summary>images.jpg.large_image_url → image_url (семантика JikanMappers).</summary>
    public static string? JikanPosterUrl(JikanMedia? media) =>
        media?.Images?.Jpg?.LargeImageUrl ?? media?.Images?.Jpg?.ImageUrl;

    /// <summary>Image у Shikimori — шлях-строка («/system/...») або об'єкт {original|preview|url} → повний URL (семантика ShikimoriMapper).</summary>
    public static string? ShikimoriPosterUrl(JsonElement? image)
    {
        if (image is not { } el) return null;
        string? path = el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Object => el.TryGetProperty("original", out var orig) ? orig.GetString()
                : el.TryGetProperty("preview", out var prev) ? prev.GetString()
                : el.TryGetProperty("url", out var url) ? url.GetString()
                : null,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"https://shikimori.one{path}";
    }

    /// <summary>Обгортка одиночної відповіді Jikan v4: {"data": {...}}.</summary>
    private sealed record JikanSingle<T>([property: JsonPropertyName("data")] T? Data);
}
