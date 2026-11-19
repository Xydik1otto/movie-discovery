using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Core.Domain;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Watchability;

namespace MovieDiscovery.Tests;

public sealed class WebWatchDiscoveryTests
{
    [Fact]
    public async Task Search_keeps_regional_title_pages_and_limits_unknown_domains()
    {
        var handler = new SearchHandler();
        using var http = new HttpClient(handler);
        var search = new WebWatchSearch(http, Options.Create(new WatchWebOptions { ApiKey = "fixture" }));
        var title = new Title { Id = 21, Type = "movie", TitleEn = "The Matrix", TitleUk = "Матриця", Year = 1999 };

        var results = await search.FindAsync(title, "UA", CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Contains(results, row => row.ProviderName == "MEGOGO" && row.Kind == WebWatchSearch.CandidateKind &&
            row.Url == "https://megogo.net/ua/view/1234-matrix" && row.AudioLangs is null && row.Region == "UA");
        Assert.Equal(new[] { "candidate-one.com", "candidate-two.com" }, results
            .Where(row => row.Kind == WebWatchSearch.PendingKind).Select(row => row.ProviderName));
    }

    [Fact]
    public async Task Admin_review_publishes_only_the_reviewed_title_link_in_its_country()
    {
        await using var fixture = new ApiTestFactory();
        await using var app = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Admin:Key"] = "fixture-review-key" }));
        });
        var candidate = new Provider
        {
            ProviderName = "candidate-one.com", Kind = WebWatchSearch.PendingKind, Source = WebWatchSearch.Source,
            Region = "UA", Url = "https://candidate-one.com/movie/the-matrix", TitleId = 21,
        };
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MovieDiscoveryDbContext>();
            db.Titles.Add(new Title { Id = 21, TitleEn = "The Matrix", Year = 1999 });
            db.Providers.Add(candidate);
            await db.SaveChangesAsync();
        }
        using var client = app.CreateClient();
        const string watchPath = "/api/titles/21/watch";
        const string sourcesPath = "/api/admin/watch/sources";
        var reviewPath = $"{sourcesPath}/{candidate.Id}/review";

        var before = await client.GetFromJsonAsync<TitleWatchResponse>($"{watchPath}?region=UA");
        Assert.DoesNotContain(before!.SearchLinks, row => row.Source == WebWatchSearch.Source);
        Assert.Empty(before.Providers);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(sourcesPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(reviewPath, new { approve = true, candidate.Url })).StatusCode);

        client.DefaultRequestHeaders.Add("X-Admin-Key", "fixture-review-key");
        Assert.Contains(candidate.Url!, await client.GetStringAsync(sourcesPath));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(reviewPath, new { approve = true, url = "https://another.com/movie/the-matrix" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(reviewPath, new { approve = true, candidate.Url })).StatusCode);

        var local = await client.GetFromJsonAsync<TitleWatchResponse>($"{watchPath}?region=UA");
        Assert.Contains(local!.SearchLinks, row => row.Url == candidate.Url && row.Source == WebWatchSearch.Source);
        Assert.Empty(local.Providers);
        var abroad = await client.GetFromJsonAsync<TitleWatchResponse>($"{watchPath}?region=FR");
        Assert.DoesNotContain(abroad!.SearchLinks, row => row.Source == WebWatchSearch.Source);
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.search.brave.com", request.RequestUri!.Host);
            Assert.Equal("UA", Assert.Single(request.Headers.GetValues("X-Loc-Country")));
            Assert.Contains("country=ALL&search_lang=uk", request.RequestUri.Query);
            Assert.Contains("матриця", Uri.UnescapeDataString(request.RequestUri.Query));
            var results = new[]
            {
                new { title = "Матр иця (1999)", url = "https://other-one.com/movie/incorrect-title" },
                new { title = "Матриця (2021)", url = "https://other-two.com/movie/incorrect-release" },
                new { title = "Матриця (1999)", url = "https://127.0.0.1/movie/local-address" },
                new { title = "Матриця (1999)", url = "https://candidate-zero.com/catalog/search" },
                new { title = "The Matrix (1999) | MEGOGO", url = "https://megogo.net/ua/view/1234-matrix?tracking=1" },
                new { title = "Матриця (1999)", url = "https://candidate-one.com/movie/the-matrix" },
                new { title = "The Matrix (1999)", url = "https://candidate-two.com/movie/the-matrix" },
                new { title = "The Matrix (1999)", url = "https://candidate-three.com/movie/the-matrix" },
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { web = new { results } }) });
        }
    }
}
