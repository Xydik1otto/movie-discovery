using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MovieDiscovery.Core.Abstractions;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Tests;

/// <summary>Спільні хелпери тестів: InMemory-контекст, кеш, стаб-HTTP.</summary>
public static class TestDb
{
    public static MovieDiscoveryDbContext Create(string name) =>
        new(new DbContextOptionsBuilder<MovieDiscoveryDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    /// <summary>Кеш-стаб: містить заздалегідь задані значення (для 24-год кешу файлів).</summary>
    public sealed class CacheStub : ICacheStore
    {
        public Dictionary<string, string> Items { get; } = new();

        public Task<string?> GetAsync(string source, string cacheKey, CancellationToken ct = default) =>
            Task.FromResult(Items.TryGetValue($"{source}|{cacheKey}", out var v) ? v : null);

        public Task SetAsync(string source, string cacheKey, string payload, int ttlSeconds, CancellationToken ct = default)
        {
            Items[$"{source}|{cacheKey}"] = payload;
            return Task.CompletedTask;
        }
    }
}

/// <summary>HTTP-стаб: відповідь за предикатом URL (реальних HTTP-викликів нема).</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage?> responder) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri?.ToString() ?? "");
        var response = responder(request)
            ?? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("") };
        return Task.FromResult(response);
    }
}

public static class StubHttp
{
    public static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage?> responder) =>
        new(new StubHttpHandler(responder)) { BaseAddress = new Uri("https://test.local/") };

    public static HttpResponseMessage Html(string html) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Json(string json) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

/// <summary>Голий логер (без I/O).</summary>
public static class Logs
{
    public static ILogger<T> Null<T>() where T : class => NullLogger<T>.Instance;
}
