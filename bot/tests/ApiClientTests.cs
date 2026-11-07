using System.Net;
using System.Text;
using MovieDiscovery.Bot.Api;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

public class ApiClientTests
{
    private const string SearchJson =
        """{"items":[{"id":123,"tmdbId":456,"type":"series","title":"Друзі","originalTitle":"Friends","year":1994,"genres":["комедія"],"countries":["US"],"ratingTmdb":7.8,"ratingImdb":8.1,"posterUrl":"https://image.tmdb.org/x.jpg","runtimeMin":22,"seasons":10,"overview":"Ситком про друзів"}],"total":1234,"page":1,"pageSize":10}""";

    private const string SimilarJson =
        """{"items":[{"id":9,"tmdbId":999,"type":"movie","title":"Fargo","year":1996,"overview":"Кримінальна драма","score":0.83,"reasons":["спільні жанри: драма, трилер","спільний актор X"]}]}""";

    private const string ListsJson =
        """{"items":[{"id":9,"tmdbId":999,"type":"movie","title":"Dune","year":2021,"overview":"Пісок","rating":8,"createdAt":"2026-11-07T10:00:00Z"}]}""";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(int status, string body)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static ApiClient Create(StubHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") });

    // ---------------------------------------------------------------- пошук

    [Fact]
    public async Task SearchAsync_BuildsUrlWithEscapedQueryPagePageSizeLang()
    {
        var handler = new StubHandler(_ => Json(200, SearchJson));
        var client = Create(handler);

        var res = await client.SearchAsync("друзі & серіали", 2, 10, "uk");

        Assert.True(res.Ok);
        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal("/api/search", uri.AbsolutePath);
        Assert.Contains("q=" + Uri.EscapeDataString("друзі & серіали"), uri.Query);
        Assert.Contains("page=2", uri.Query);
        Assert.Contains("pageSize=10", uri.Query);
        Assert.Contains("lang=uk", uri.Query);
    }

    [Fact]
    public async Task SearchAsync_ParsesTitleCardCamelCase()
    {
        var client = Create(new StubHandler(_ => Json(200, SearchJson)));

        var res = await client.SearchAsync("Друзі");

        Assert.True(res.Ok);
        var item = Assert.Single(res.Value!.Items);
        Assert.Equal(456, item.TmdbId);
        Assert.Equal("series", item.Type);
        Assert.Equal("Друзі", item.Title);
        Assert.Equal(1994, item.Year);
        Assert.Equal(7.8, item.RatingTmdb);
        Assert.Equal(8.1, item.RatingImdb);
        Assert.Equal(10, item.Seasons);
        Assert.Equal(1234, res.Value!.Total);
    }

    [Fact]
    public async Task SearchAsync_EmptyItems_IsOk()
    {
        var client = Create(new StubHandler(_ => Json(200, """{"items":[],"total":0,"page":1,"pageSize":10}""")));

        var res = await client.SearchAsync("ньочого");

        Assert.True(res.Ok);
        Assert.Empty(res.Value!.Items);
        Assert.Equal(0, res.Value!.Total);
    }

    // ---------------------------------------------------------------- схожість

    [Fact]
    public async Task SimilarBySeedsAsync_PostsCamelCaseBodyToRightPath()
    {
        var handler = new StubHandler(_ => Json(200, SimilarJson));
        var client = Create(handler);

        var res = await client.SimilarBySeedsAsync([123, 456], 10, "uk");

        Assert.True(res.Ok);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/similar/by-seeds", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"tmdbIds\":[123,456]", handler.LastBody);
        Assert.Contains("\"limit\":10", handler.LastBody);
        Assert.Contains("\"lang\":\"uk\"", handler.LastBody);
    }

    [Fact]
    public async Task SimilarBySeedsAsync_ParsesScoreAndReasons()
    {
        var client = Create(new StubHandler(_ => Json(200, SimilarJson)));

        var res = await client.SimilarBySeedsAsync([123]);

        var item = Assert.Single(res.Value!);
        Assert.Equal(0.83, item.Score);
        Assert.Equal(2, item.Reasons!.Count);
        Assert.Equal("спільні жанри: драма, трилер", item.Reasons![0]);
    }

    [Fact]
    public async Task SimilarByTextAsync_PostsText()
    {
        var handler = new StubHandler(_ => Json(200, SimilarJson));
        var client = Create(handler);

        var res = await client.SimilarByTextAsync("легкий ситком з детективом", 10, "en");

        Assert.True(res.Ok);
        Assert.Equal("/api/similar/by-text", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"text\":\"легкий ситком з детективом\"", handler.LastBody);
        Assert.Contains("\"lang\":\"en\"", handler.LastBody);
    }

    // ---------------------------------------------------------------- списки

    [Fact]
    public async Task GetListsAsync_BuildsUrlAndParsesUserRating()
    {
        var handler = new StubHandler(_ => Json(200, ListsJson));
        var client = Create(handler);

        var res = await client.GetListsAsync(1, "watched", "uk");

        Assert.True(res.Ok);
        Assert.Equal("/api/lists", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("profileId=1", handler.LastRequest.RequestUri!.Query);
        Assert.Contains("kind=watched", handler.LastRequest.RequestUri!.Query);
        var item = Assert.Single(res.Value!);
        Assert.Equal(8, item.Rating);
        Assert.NotNull(item.CreatedAt);
    }

    [Fact]
    public async Task AddToListAsync_PostsProfileKindTmdbId()
    {
        var handler = new StubHandler(_ => Json(201, "{}"));
        var client = Create(handler);

        var res = await client.AddToListAsync(1, "watchlist", 456);

        Assert.True(res.Ok);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/api/lists", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("\"profileId\":1", handler.LastBody);
        Assert.Contains("\"kind\":\"watchlist\"", handler.LastBody);
        Assert.Contains("\"tmdbId\":456", handler.LastBody);
    }

    [Fact]
    public async Task AddToListAsync_MapsConflictAndNotFound()
    {
        var conflict = Create(new StubHandler(_ => Json(409, "{}")));
        var conflictRes = await conflict.AddToListAsync(1, "watchlist", 456);
        Assert.False(conflictRes.Ok);
        Assert.Equal(409, conflictRes.Status);

        var notFound = Create(new StubHandler(_ => Json(404, "{}")));
        var notFoundRes = await notFound.AddToListAsync(1, "watchlist", 456);
        Assert.True(notFoundRes.IsNotFound);
    }

    // ---------------------------------------------------------------- деградація

    [Fact]
    public async Task ErrorStatuses_AreClassified()
    {
        var notFound = Create(new StubHandler(_ => Json(404, "{}")));
        Assert.True((await notFound.SearchAsync("x")).IsNotFound);

        var sema = Create(new StubHandler(_ => Json(503, "{}")));
        var res503 = await sema.SimilarByTextAsync("текст");
        Assert.False(res503.Ok);
        Assert.True(res503.IsServiceUnavailable);

        var server = Create(new StubHandler(_ => Json(500, "{}")));
        var res500 = await server.SearchAsync("x");
        Assert.False(res500.Ok);
        Assert.Equal(500, res500.Status);
    }

    [Fact]
    public async Task NetworkFailure_IsUnreachable()
    {
        var client = Create(new StubHandler(_ => throw new HttpRequestException("connection refused")));
        var res = await client.SearchAsync("x");
        Assert.False(res.Ok);
        Assert.True(res.IsUnreachable);
    }

    [Fact]
    public async Task CancellationByTimeout_IsTimeout()
    {
        var client = Create(new StubHandler(_ => throw new TaskCanceledException()));
        var res = await client.SearchAsync("x"); // ct не скасовано користувачем
        Assert.False(res.Ok);
        Assert.True(res.IsTimeout);
    }

    [Fact]
    public async Task BrokenJson_IsDeserializationError()
    {
        var client = Create(new StubHandler(_ => Json(200, "{не-jсон")));
        var res = await client.SearchAsync("x");
        Assert.False(res.Ok);
        Assert.Equal("deserialization", res.Error);
    }
}
