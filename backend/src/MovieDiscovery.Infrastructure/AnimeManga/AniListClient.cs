using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Infrastructure.Tmdb;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>
/// Клієнт AniList GraphQL (https://graphql.anilist.co). Тротлінг ~700 мс (ліміт 90 req/min).
/// User(name) → id кешується безстроково; MediaListCollection — короткий TTL.
/// </summary>
public sealed class AniListClient(
    HttpClient http,
    ICacheStore cache,
    AniListThrottle throttle,
    ILogger<AniListClient> log)
{
    public const string Source = "anilist";
    private const int MaxRetries = 3;
    private const int ListsTtlSeconds = 600;

    public const string UserQuery = """
        query ($name: String) {
          User(name: $name) { id }
        }
        """;

    public const string MediaListQuery = """
        query ($userId: Int, $type: MediaType) {
          MediaListCollection(userId: $userId, type: $type) {
            lists {
              name
              entries {
                status
                score(format: POINT_10)
                media {
                  id
                  title { romaji english ukrainian }
                  coverImage { large }
                  episodes
                  chapters
                  volumes
                  format
                  genres
                  studios { nodes { name } }
                  startDate { year }
                  averageScore
                }
              }
            }
          }
        }
        """;

    /// <summary>Резерв без title.ukrainian — якщо поле ще немає в прод-схемі AniList (GraphQL-валідація відхилить).</summary>
    public const string MediaListQueryFallback = """
        query ($userId: Int, $type: MediaType) {
          MediaListCollection(userId: $userId, type: $type) {
            lists {
              name
              entries {
                status
                score(format: POINT_10)
                media {
                  id
                  title { romaji english }
                  coverImage { large }
                  episodes
                  chapters
                  volumes
                  format
                  genres
                  studios { nodes { name } }
                  startDate { year }
                  averageScore
                }
              }
            }
          }
        }
        """;

    /// <summary>Нікнейм → внутрішній id AniList. null — профіль не знайдено/приватний.</summary>
    public async Task<int?> UserIdAsync(string username, CancellationToken ct = default)
    {
        var key = $"user:{username.Trim().ToLowerInvariant()}";
        var cached = await cache.GetAsync(Source, key, ct);
        if (cached is not null)
        {
            try
            {
                var cachedUser = JsonSerializer.Deserialize<AniListResponse<AniListUserData>>(cached, TmdbJson.Options);
                if (cachedUser?.Data?.User is { Id: > 0 } u) return u.Id;
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Пошкоджений кеш AniList ({Key}) — тягну свіжі дані", key);
            }
        }

        var json = await PostAsync(UserQuery, new Dictionary<string, object?> { ["name"] = username.Trim() }, ct);
        if (json is null) return null;
        try
        {
            var resp = JsonSerializer.Deserialize<AniListResponse<AniListUserData>>(json, TmdbJson.Options);
            var id = resp?.Data?.User?.Id;
            if (id is not ({ } value and > 0))
            {
                log.LogInformation("AniList: профіль «{User}» не знайдено ({Errors})", username,
                    string.Join("; ", resp?.Errors?.Select(e => e.Message) ?? []));
                return null;
            }
            await cache.SetAsync(Source, key, json, 0, ct); // нікнейм → id стабільний
            return value;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Не вдалося розібрати відповідь AniList User({User})", username);
            return null;
        }
    }

    /// <summary>MediaListCollection(userId, type: ANIME|MANGA) — усі списки користувача з entries.</summary>
    public async Task<AniListCollection?> MediaListCollectionAsync(int userId, string type, CancellationToken ct = default)
    {
        var key = $"user:{userId}:lists:{type.ToLowerInvariant()}";
        var cached = await cache.GetAsync(Source, key, ct);
        if (cached is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(cached, TmdbJson.Options)?.Data?.MediaListCollection;
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Пошкоджений кеш AniList ({Key}) — тягну свіжі дані", key);
            }
        }

        var variables = new Dictionary<string, object?>
        {
            ["userId"] = userId,
            ["type"] = type, // ANIME | MANGA
        };
        var json = await PostAsync(MediaListQuery, variables, ct)
                   ?? await PostAsync(MediaListQueryFallback, variables, ct);
        if (json is null) return null;

        try
        {
            var resp = JsonSerializer.Deserialize<AniListResponse<AniListCollectionData>>(json, TmdbJson.Options);
            if (resp?.Errors is { Count: > 0 })
                log.LogWarning("AniList GraphQL errors: {Errors}", string.Join("; ", resp.Errors.Select(e => e.Message)));
            await cache.SetAsync(Source, key, json, ListsTtlSeconds, ct);
            return resp?.Data?.MediaListCollection;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Не вдалося розібрати відповідь AniList MediaListCollection({UserId}, {Type})", userId, type);
            return null;
        }
    }

    private Task<string?> PostAsync(string query, Dictionary<string, object?> variables, CancellationToken ct) =>
        throttle.RunAsync(async () =>
        {
            var payload = JsonSerializer.Serialize(new { query, variables });
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var resp = await http.PostAsync(string.Empty, content, ct);
                    if (resp.IsSuccessStatusCode)
                        return await resp.Content.ReadAsStringAsync(ct);

                    if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct);
                        log.LogWarning("AniList {Code} — без ретраїв: {Body}",
                            (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
                        return null;
                    }

                    if (attempt >= MaxRetries)
                    {
                        log.LogWarning("AniList {Code} — ліміт ретраїв вичерпано", (int)resp.StatusCode);
                        return null;
                    }

                    var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    log.LogInformation("AniList {Code} — ретрай через {Delay}с", (int)resp.StatusCode, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt >= MaxRetries)
                    {
                        log.LogWarning(ex, "AniList недоступний — здаюся після ретраїв");
                        return null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
            }
        }, ct);
}
