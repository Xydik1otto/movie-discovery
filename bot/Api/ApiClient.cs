using System.Text;
using System.Text.Json;

namespace MovieDiscovery.Bot.Api;

/// <summary>
/// HTTP-клієнт до REST API (PLAN.md). HttpClient + System.Text.Json, camelCase.
/// Мережеві помилки не викидає назовні, а повертає як <see cref="ApiResult{T}"/>:
/// 404/503 → Status; таймаут → Error "timeout"; недоступність → "unreachable".
/// </summary>
public sealed class ApiClient
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // повністю валідний JSON з читабельними uk/en символами (без \uXXXX-екранування)
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const int TimeoutSeconds = 30;

    private readonly HttpClient _http;

    public ApiClient(HttpClient http) => _http = http;

    public ApiClient(string baseUrl)
        : this(new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
        })
    {
    }

    /// <summary>GET /api/search?q=&amp;type=&amp;...&amp;page=&amp;pageSize=&amp;lang=</summary>
    public Task<ApiResult<SearchResponse>> SearchAsync(
        string q, int page = 1, int pageSize = 10, string lang = "uk", CancellationToken ct = default)
        => GetAsync<SearchResponse>(
            $"api/search?q={Uri.EscapeDataString(q)}&page={page}&pageSize={pageSize}&lang={Uri.EscapeDataString(lang)}", ct);

    /// <summary>POST /api/similar/by-seeds { tmdbIds, limit, lang }. tmdbIds 1–10.</summary>
    public async Task<ApiResult<IReadOnlyList<TitleCard>>> SimilarBySeedsAsync(
        IReadOnlyList<int> tmdbIds, int limit = 10, string lang = "uk", CancellationToken ct = default)
    {
        var res = await PostAsync<ItemsResponse>("api/similar/by-seeds", new { tmdbIds, limit, lang }, ct);
        return Unwrap(res);
    }

    /// <summary>POST /api/similar/by-text { text, limit, lang }. 503, якщо семантичний індекс недоступний.</summary>
    public async Task<ApiResult<IReadOnlyList<TitleCard>>> SimilarByTextAsync(
        string text, int limit = 10, string lang = "uk", CancellationToken ct = default)
    {
        var res = await PostAsync<ItemsResponse>("api/similar/by-text", new { text, limit, lang }, ct);
        return Unwrap(res);
    }

    /// <summary>GET /api/lists?profileId=&amp;kind=watchlist|watched&amp;lang=</summary>
    public async Task<ApiResult<IReadOnlyList<TitleCard>>> GetListsAsync(
        int profileId, string kind, string lang = "uk", CancellationToken ct = default)
    {
        var res = await GetAsync<ItemsResponse>(
            $"api/lists?profileId={profileId}&kind={Uri.EscapeDataString(kind)}&lang={Uri.EscapeDataString(lang)}", ct);
        return Unwrap(res);
    }

    /// <summary>
    /// POST /api/lists { profileId, kind, tmdbId } — 201 успіх; 404 невідомий tmdbId; 409 вже у списку.
    /// Тіло відповіді не читаємо — важливий лише статус.
    /// </summary>
    public async Task<ApiResult<bool>> AddToListAsync(int profileId, string kind, int tmdbId, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { profileId, kind, tmdbId }, JsonOptions);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/lists", content, ct);
            return resp.IsSuccessStatusCode
                ? ApiResult<bool>.Success(true)
                : ApiResult<bool>.Failure((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResult<bool>.Failure(null, "timeout");
        }
        catch (HttpRequestException)
        {
            return ApiResult<bool>.Failure(null, "unreachable");
        }
    }

    private static ApiResult<IReadOnlyList<TitleCard>> Unwrap(ApiResult<ItemsResponse> res)
        => res.Ok
            ? ApiResult<IReadOnlyList<TitleCard>>.Success(res.Value!.Items)
            : ApiResult<IReadOnlyList<TitleCard>>.Failure(res.Status, res.Error ?? "unknown");

    private async Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            return await ReadAsync<T>(resp, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResult<T>.Failure(null, "timeout");
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Failure(null, "unreachable");
        }
    }

    private async Task<ApiResult<T>> PostAsync<T>(string url, object body, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct);
            return await ReadAsync<T>(resp, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResult<T>.Failure(null, "timeout");
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Failure(null, "unreachable");
        }
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode)
            return ApiResult<T>.Failure((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}");

        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
            return value is null
                ? ApiResult<T>.Failure((int)resp.StatusCode, "empty")
                : ApiResult<T>.Success(value);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Failure((int)resp.StatusCode, "deserialization");
        }
    }
}
