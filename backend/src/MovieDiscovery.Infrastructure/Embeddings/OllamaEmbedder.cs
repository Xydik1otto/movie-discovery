using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MovieDiscovery.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MovieDiscovery.Infrastructure.Embeddings;

public sealed class OllamaOptions
{
    public string Url { get; set; } = "http://localhost:11434";
    public string EmbedModel { get; set; } = "bge-m3";
    public int ExpectedDim { get; set; } = 1024;
}

/// <summary>
/// Ембедер Ollama: POST {OLLAMA_URL}/api/embed {model, input} → float[1024].
/// Недоступність сервісу/моделі НЕ валить застосунок: повертаємо null і логуємо.
/// </summary>
public sealed class OllamaEmbedder(IHttpClientFactoryAccessor http, IOptions<OllamaOptions> options, ILogger<OllamaEmbedder> log)
    : IEmbedder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly OllamaOptions _opt = options.Value;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var client = http.CreateClient("ollama");
            var json = JsonSerializer.Serialize(new EmbedRequest(_opt.EmbedModel, text), JsonOpts);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync("/api/embed", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Ollama /api/embed повернув {Code} — повертаю null (семантика вимкнена)", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var body = await JsonSerializer.DeserializeAsync<EmbedResponse>(stream, JsonOpts, ct);
            var vec = body?.Embeddings is { Count: > 0 } list ? list[0] : null;
            if (vec is null || vec.Count == 0)
            {
                log.LogWarning("Ollama повернув порожній вектор — повертаю null");
                return null;
            }
            var arr = vec.Select(v => (float)v).ToArray();
            if (arr.Length != _opt.ExpectedDim)
            {
                log.LogWarning("Ollama повернув вектор розмірності {Dim}, очікувалось {Expected} — повертаю null",
                    arr.Length, _opt.ExpectedDim);
                return null;
            }
            return arr;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Ollama недоступна ({Url}) — повертаю null (семантика вимкнена)", _opt.Url);
            return null;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var client = http.CreateClient("ollama");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var resp = await client.GetAsync("/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed record EmbedRequest(string Model, string Input);

    private sealed class EmbedResponse
    {
        [JsonPropertyName("embeddings")] public List<List<double>>? Embeddings { get; set; }
    }
}

/// <summary>Тонка обгортка над IHttpClientFactory (щоб не тягнути Http у конструктори тестів).</summary>
public interface IHttpClientFactoryAccessor
{
    HttpClient CreateClient(string name);
}

public sealed class HttpClientFactoryAccessor(IHttpClientFactory factory) : IHttpClientFactoryAccessor
{
    public HttpClient CreateClient(string name) => factory.CreateClient(name);
}
