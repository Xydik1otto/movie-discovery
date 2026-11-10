using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MovieDiscovery.Core.Abstractions;

namespace MovieDiscovery.Infrastructure.Tmdb;

public sealed class OmdbOptions
{
    public string ApiKey { get; set; } = "";
}

public sealed record OmdbResult(string? ImdbId, double? ImdbRating, int? ImdbVotes);

/// <summary>
/// OMDb-клієнт (опційний): IMDb рейтинг/голоси за назвою+роком. Free-ключ — 1000 запитів/день,
/// тому кожен виклик кешується безстроково (ttl=0).
/// </summary>
public sealed class OmdbClient(HttpClient http, ICacheStore cache, IOptions<OmdbOptions> options, ILogger<OmdbClient> log)
{
    private readonly OmdbOptions _opt = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ApiKey);

    public async Task<OmdbResult?> GetByTitleAsync(string title, int? year, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        var cacheKey = TmdbCacheKeys.Omdb(title, year);
        var cached = await cache.GetAsync(TmdbCacheKeys.OmdbSource, cacheKey, ct);
        if (cached is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<OmdbResult>(cached, TmdbJson.Options);
            }
            catch (JsonException)
            {
                // перекачаємо
            }
        }

        try
        {
            var query = $"?apikey={_opt.ApiKey}&t={Uri.EscapeDataString(title.Trim())}" +
                        (year.HasValue ? $"&y={year.Value}" : "");
            using var resp = await http.GetAsync(query, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Response", out var respProp)
                || !string.Equals(respProp.GetString(), "True", StringComparison.OrdinalIgnoreCase))
            {
                // негативну відповідь теж кешуємо на добу, щоб не палити квоту
                await cache.SetAsync(TmdbCacheKeys.OmdbSource, cacheKey,
                    JsonSerializer.Serialize(new OmdbResult(null, null, null)), 86400, ct);
                return null;
            }

            var result = new OmdbResult(
                root.TryGetProperty("imdbID", out var idEl) ? idEl.GetString() : null,
                ParseRating(root),
                ParseVotes(root));

            await cache.SetAsync(TmdbCacheKeys.OmdbSource, cacheKey, JsonSerializer.Serialize(result), 0, ct);
            return result;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "OMDb запит не вдався ({Title})", title);
            return null;
        }
    }

    private static double? ParseRating(JsonElement root) =>
        root.TryGetProperty("imdbRating", out var r)
        && double.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    private static int? ParseVotes(JsonElement root)
    {
        if (!root.TryGetProperty("imdbVotes", out var v)) return null;
        var s = v.GetString()?.Replace(",", "", StringComparison.Ordinal);
        return int.TryParse(s, out var n) ? n : null;
    }
}
