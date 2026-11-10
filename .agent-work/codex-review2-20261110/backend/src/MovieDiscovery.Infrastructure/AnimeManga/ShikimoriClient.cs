using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Клієнт Shikimori API v1. ОБОВ'ЯЗКОВІ заголовки: User-Agent; X-Application-Name — якщо задано в опціях
/// (реєстрація застосунку; ставиться в Program.cs при реєстрації HttpClient). 403 → SourceAccessException.
/// Тротлінг 500 мс; user_rates не кешуємо (живі дані), резолв нікнейма → id — безстроково.
/// </summary>
public sealed class ShikimoriClient(
    HttpClient http,
    ICacheStore cache,
    ShikimoriThrottle throttle,
    ILogger<ShikimoriClient> log)
{
    public const string Source = "shikimori";
    private const int RatesPageSize = 500;

    /// <summary>Нікнейм або числовий id → профіль {id, nickname}. null — не знайдено (404).</summary>
    public async Task<ShikimoriUser?> UserResolveAsync(string nicknameOrId, CancellationToken ct = default)
    {
        var key = $"user:{nicknameOrId.Trim().ToLowerInvariant()}";
        var cached = await cache.GetAsync(Source, key, ct);
        if (cached is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<ShikimoriUser>(cached, TmdbJson.Options);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Пошкоджений кеш Shikimori ({Key}) — тягну свіжі дані", key);
            }
        }

        var json = await SendAsync($"users/{Uri.EscapeDataString(nicknameOrId.Trim())}", ct);
        if (json is null) return null;

        try
        {
            var user = JsonSerializer.Deserialize<ShikimoriUser>(json, TmdbJson.Options);
            if (user is { Id: > 0 })
                await cache.SetAsync(Source, key, json, 0, ct); // нікнейм → id стабільний: кеш безстроковий
            return user;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Не вдалося розібрати відповідь Shikimori users/{Nick}", nicknameOrId);
            return null;
        }
    }

    /// <summary>Аніме-лист користувача: ?limit=500&amp;page=N (без status → всі rates, фільтр по status на місці).</summary>
    public Task<List<ShikimoriRate>?> AnimeRatesAsync(long userId, int page, string? status = null, CancellationToken ct = default) =>
        RatesAsync($"users/{userId}/anime_rates", page, status, ct);

    /// <summary>Лист манги користувача: ?limit=500&amp;page=N (без status → всі rates, фільтр по status на місці).</summary>
    public Task<List<ShikimoriRate>?> MangaRatesAsync(long userId, int page, string? status = null, CancellationToken ct = default) =>
        RatesAsync($"users/{userId}/manga_rates", page, status, ct);

    private async Task<List<ShikimoriRate>?> RatesAsync(string path, int page, string? status, CancellationToken ct)
    {
        var query = $"limit={RatesPageSize}&page={Math.Max(1, page)}";
        if (!string.IsNullOrWhiteSpace(status)) query += $"&status={Uri.EscapeDataString(status)}";
        var json = await SendAsync($"{path}?{query}", ct);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<List<ShikimoriRate>>(json, TmdbJson.Options);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Не вдалося розібрати відповідь Shikimori {Path}", path);
            return null;
        }
    }

    private Task<string?> SendAsync(string pathAndQuery, CancellationToken ct) =>
        throttle.RunAsync(async () =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var resp = await http.GetAsync(pathAndQuery, ct);
                    if (resp.IsSuccessStatusCode)
                        return await resp.Content.ReadAsStringAsync(ct);

                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                        throw new SourceAccessException(
                            "Shikimori вимагає реєстрації застосунку (потрібен заголовок X-Application-Name з реєстрації) — імпорт з Shikimori недоступний");

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        log.LogInformation("Shikimori 404: {Path}", pathAndQuery);
                        return null;
                    }

                    if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                    {
                        log.LogWarning("Shikimori {Code} на {Path} — без ретраїв", (int)resp.StatusCode, pathAndQuery);
                        return null;
                    }

                    if (attempt >= 3)
                    {
                        log.LogWarning("Shikimori {Code} на {Path} — ліміт ретраїв вичерпано", (int)resp.StatusCode, pathAndQuery);
                        return null;
                    }

                    var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    log.LogInformation("Shikimori {Code} на {Path} — ретрай через {Delay}с", (int)resp.StatusCode, pathAndQuery, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt >= 3)
                    {
                        log.LogWarning(ex, "Shikimori недоступний ({Path}) — здаюся після ретраїв", pathAndQuery);
                        return null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }
        }, ct);
}
