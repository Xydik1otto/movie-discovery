using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MovieDiscovery.Bot.Api;

/// <summary>
/// HTTP-клієнт до REST API (PLAN.md). HttpClient + System.Text.Json, camelCase.
/// КОЖЕН запит несе заголовки X-Bot-Key (env BOT_API_KEY; якщо порожній — не додається)
/// та X-Telegram-User-Id (id юзера Telegram) — бекенд резолвить профіль із них;
/// параметр profileId ніде не надсилається (MD-05).
/// Мережеві помилки не викидає назовні, а повертає як <see cref="ApiResult{T}"/>:
/// 404/403/409/503 → Status; таймаут → Error "timeout"; недоступність → "unreachable".
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
    private const string BotKeyHeader = "X-Bot-Key";
    private const string TelegramUserHeader = "X-Telegram-User-Id";

    private readonly HttpClient _http;
    private readonly string? _botApiKey;

    /// <summary>CR-01 fail-closed: персональні операції (списки юзера) без налаштованого
    /// BOT_API_KEY відмовляються локально (без HTTP) — інакше API обробив би їх як анонімні
    /// і записав у СПІЛЬНИЙ гостьовий профіль (усі telegram-юзери в одному профілі).</summary>
    public const string BotKeyNotConfiguredError = "bot_key_not_configured";

    public ApiClient(HttpClient http, string? botApiKey = null)
    {
        _http = http;
        _botApiKey = string.IsNullOrWhiteSpace(botApiKey) ? null : botApiKey.Trim();
    }

    /// <summary>true, якщо X-Bot-Key налаштований (персональні операції дозволені).</summary>
    public bool HasBotKey => _botApiKey is not null;

    public ApiClient(string baseUrl, string? botApiKey = null)
        : this(new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
        }, botApiKey)
    {
    }

    /// <summary>GET /api/search?q=&amp;type=&amp;genres=&amp;yearFrom=&amp;yearTo=&amp;minRating=&amp;page=&amp;pageSize=&amp;lang=</summary>
    public Task<ApiResult<SearchResponse>> SearchAsync(
        string q, long telegramUserId, SearchFilters? filters = null,
        int page = 1, int pageSize = 10, string lang = "uk", CancellationToken ct = default)
        => SendAsync<SearchResponse>(HttpMethod.Get, BuildSearchUrl(q, filters, page, pageSize, lang), null, telegramUserId, ct);

    private static string BuildSearchUrl(string q, SearchFilters? filters, int page, int pageSize, string lang)
    {
        var sb = new StringBuilder("api/search?q=");
        sb.Append(Uri.EscapeDataString(q));
        if (filters is not null)
        {
            if (!string.IsNullOrWhiteSpace(filters.Type))
                sb.Append("&type=").Append(Uri.EscapeDataString(filters.Type));
            if (filters.Genres is { Count: > 0 })
                sb.Append("&genres=").Append(Uri.EscapeDataString(string.Join(',', filters.Genres)));
            if (filters.YearFrom is { } yf)
                sb.Append("&yearFrom=").Append(yf.ToString(CultureInfo.InvariantCulture));
            if (filters.YearTo is { } yt)
                sb.Append("&yearTo=").Append(yt.ToString(CultureInfo.InvariantCulture));
            if (filters.MinRating is { } mr)
                sb.Append("&minRating=").Append(mr.ToString(CultureInfo.InvariantCulture));
            // CR-06: паритет фільтрів з web (країна/тривалість/статус)
            if (!string.IsNullOrWhiteSpace(filters.Country))
                sb.Append("&country=").Append(Uri.EscapeDataString(filters.Country));
            if (filters.MinRuntime is { } mn)
                sb.Append("&minRuntime=").Append(mn.ToString(CultureInfo.InvariantCulture));
            if (filters.MaxRuntime is { } mx)
                sb.Append("&maxRuntime=").Append(mx.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(filters.Status))
                sb.Append("&status=").Append(Uri.EscapeDataString(filters.Status));
        }

        sb.Append("&page=").Append(page)
          .Append("&pageSize=").Append(pageSize)
          .Append("&lang=").Append(Uri.EscapeDataString(lang));
        return sb.ToString();
    }

    /// <summary>GET /api/similar/by-seeds?ids=…&amp;limit=&amp;lang= — ids ВНУТРІШНІ id тайтлів (1–10).</summary>
    public async Task<ApiResult<IReadOnlyList<TitleCard>>> SimilarBySeedsAsync(
        IReadOnlyList<int> ids, long telegramUserId, int limit = 10, string lang = "uk", CancellationToken ct = default)
    {
        var url = $"api/similar/by-seeds?ids={Uri.EscapeDataString(string.Join(',', ids))}" +
                  $"&limit={limit.ToString(CultureInfo.InvariantCulture)}&lang={Uri.EscapeDataString(lang)}";
        var res = await SendAsync<ItemsResponse>(HttpMethod.Get, url, null, telegramUserId, ct);
        return Unwrap(res);
    }

    /// <summary>POST /api/similar/by-text { text, limit, lang }. 503, якщо семантичний індекс недоступний.</summary>
    public async Task<ApiResult<IReadOnlyList<TitleCard>>> SimilarByTextAsync(
        string text, long telegramUserId, int limit = 10, string lang = "uk", CancellationToken ct = default)
    {
        var res = await SendAsync<ItemsResponse>(HttpMethod.Post, "api/similar/by-text", new { text, limit, lang }, telegramUserId, ct);
        return Unwrap(res);
    }

    /// <summary>GET /api/lists?kind=watchlist|watched&amp;lang= — профіль резолвиться на бекенді з заголовків.
    /// CR-01 fail-closed: без BOT_API_KEY — локальна відмова (персональні дані не чіпаємо).</summary>
    public async Task<ApiResult<IReadOnlyList<TitleCard>>> GetListsAsync(
        string kind, long telegramUserId, string lang = "uk", CancellationToken ct = default)
    {
        if (_botApiKey is null)
            return ApiResult<IReadOnlyList<TitleCard>>.Failure(null, BotKeyNotConfiguredError);
        var res = await SendAsync<ItemsResponse>(HttpMethod.Get,
            $"api/lists?kind={Uri.EscapeDataString(kind)}&lang={Uri.EscapeDataString(lang)}", null, telegramUserId, ct);
        return Unwrap(res);
    }

    /// <summary>
    /// POST /api/lists/{kind} { titleId } — ідемпотентний upsert за ВНУТРІШНІМ id тайтла.
    /// 201 успіх; 409 already_in_list; 404 title_not_found. Тіло відповіді не читаємо — важливий лише статус.
    /// CR-01 fail-closed: без BOT_API_KEY — локальна відмова (без HTTP; інакше запис пішов би
    /// у спільний гостьовий профіль).
    /// </summary>
    public async Task<ApiResult<bool>> AddToListAsync(string kind, int titleId, long telegramUserId, CancellationToken ct = default)
    {
        if (_botApiKey is null)
            return ApiResult<bool>.Failure(null, BotKeyNotConfiguredError);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"api/lists/{Uri.EscapeDataString(kind)}");
            ApplyHeaders(req, telegramUserId);
            var payload = JsonSerializer.Serialize(new { titleId }, JsonOptions);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
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

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, long telegramUserId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            ApplyHeaders(req, telegramUserId);
            if (body is not null)
            {
                var payload = JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            using var resp = await _http.SendAsync(req, ct);
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

    private void ApplyHeaders(HttpRequestMessage req, long telegramUserId)
    {
        if (_botApiKey is not null)
            req.Headers.Add(BotKeyHeader, _botApiKey);
        req.Headers.Add(TelegramUserHeader, telegramUserId.ToString(CultureInfo.InvariantCulture));
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
