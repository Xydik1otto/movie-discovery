using System.Net;
using System.Text;
using MovieDiscovery.Bot.Api;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

public class ApiClientTests
{
    private const string SearchJson =
        """{"items":[{"id":123,"tmdbId":456,"type":"series","title":"Друзі","originalTitle":"Friends","year":1994,"genres":["комедія"],"countries":["US"],"ratingTmdb":7.8,"ratingImdb":8.1,"posterUrl":"https://image.tmdb.org/x.jpg","runtimeMin":22,"seasons":10,"overview":"Ситком про друзів"}],"total":1234,"page":1,"pageSize":10}""";

    private const string CatalogJson =
        // каталог-рядок БЕЗ tmdbId (IMDb/аніме/манґа) — десеріалізація не повинна падати
        """{"items":[{"id":777001,"tmdbId":null,"type":"movie","title":"Ніч на Землі","year":1991,"genres":["драма"],"overview":"Таксі вночі"}],"total":1,"page":1,"pageSize":10}""";

    private const string SimilarJson =
        """{"items":[{"id":9,"tmdbId":999,"type":"movie","title":"Fargo","year":1996,"overview":"Кримінальна драма","score":0.83,"reasons":["спільні жанри: драма, трилер","спільний актор X"]}]}""";

    private const string ListsJson =
        """{"items":[{"id":9,"tmdbId":null,"type":"movie","title":"Dune","year":2021,"overview":"Пісок","rating":8,"createdAt":"2026-11-07T10:00:00Z"}]}""";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        public HttpRequestMessage? LastRequest => Requests.Count > 0 ? Requests[^1] : null;
        public string? LastBody => Bodies.Count > 0 ? Bodies[^1] : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            else
                Bodies.Add(string.Empty);
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(int status, string body)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static ApiClient Create(StubHandler handler, string? botApiKey = "test-key")
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") }, botApiKey);

    // ---------------------------------------------------------------- заголовки авторизації (MD-05)

    [Fact]
    public async Task EveryRequest_CarriesBotKeyAndTelegramUserIdHeaders()
    {
        var handler = new StubHandler(_ => Json(200, SearchJson));
        var client = Create(handler, botApiKey: "secret-key-42");

        await client.SearchAsync("друзі", telegramUserId: 777);
        await client.SimilarByTextAsync("текст", telegramUserId: 777);
        await client.GetListsAsync("watchlist", telegramUserId: 777);
        await client.AddToListAsync("watchlist", 123, telegramUserId: 777);

        Assert.Equal(4, handler.Requests.Count);
        foreach (var req in handler.Requests)
        {
            Assert.Equal("secret-key-42", req.Headers.GetValues("X-Bot-Key").Single());
            Assert.Equal("777", req.Headers.GetValues("X-Telegram-User-Id").Single());
        }
    }

    [Fact]
    public async Task DifferentTelegramUsers_ProduceDifferentHeaders()
    {
        var handler = new StubHandler(_ => Json(200, SearchJson));
        var client = Create(handler);

        await client.SearchAsync("a", telegramUserId: 111);
        await client.SearchAsync("b", telegramUserId: 222);

        Assert.Equal("111", handler.Requests[0].Headers.GetValues("X-Telegram-User-Id").Single());
        Assert.Equal("222", handler.Requests[1].Headers.GetValues("X-Telegram-User-Id").Single());
    }

    [Fact]
    public async Task EmptyBotApiKey_HeaderIsOmitted_ButUserIdStillSent()
    {
        var handler = new StubHandler(_ => Json(200, SearchJson));
        var client = Create(handler, botApiKey: "");

        await client.SearchAsync("x", telegramUserId: 555);

        Assert.False(handler.LastRequest!.Headers.Contains("X-Bot-Key"));
        Assert.Equal("555", handler.LastRequest.Headers.GetValues("X-Telegram-User-Id").Single());
    }

    [Fact]
    public async Task NoRequestEverCarriesProfileId()
    {
        var handler = new StubHandler(_ => Json(200, ListsJson));
        var client = Create(handler);

        await client.SearchAsync("дюна", 900001);
        await client.SimilarBySeedsAsync([1, 2], 900001);
        await client.GetListsAsync("watched", 900001);
        await client.AddToListAsync("watchlist", 5, 900001);

        foreach (var req in handler.Requests)
        {
            var url = req.RequestUri!.ToString().ToLowerInvariant();
            Assert.DoesNotContain("profileid", url);
        }

        foreach (var body in handler.Bodies)
            Assert.DoesNotContain("profileId", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- пошук

    [Fact]
    public async Task SearchAsync_BuildsUrlWithEscapedQueryPagePageSizeLang()
    {
        var handler = new StubHandler(_ => Json(200, SearchJson));
        var client = Create(handler);

        var res = await client.SearchAsync("друзі & серіали", 42, page: 2, pageSize: 10, lang: "uk");

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

        var res = await client.SearchAsync("Друзі", 1);

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
    public async Task SearchAsync_NullTmdbIdCatalogRow_ParsesWithoutError()
    {
        var client = Create(new StubHandler(_ => Json(200, CatalogJson)));

        var res = await client.SearchAsync("ніч на землі", 1);

        Assert.True(res.Ok);
        var item = Assert.Single(res.Value!.Items);
        Assert.Null(item.TmdbId);
        Assert.Equal(777001, item.Id); // внутрішній id наявний — саме він іде в callbacks/API
    }

    [Fact]
    public async Task SearchAsync_EmptyItems_IsOk()
    {
        var client = Create(new StubHandler(_ => Json(200, """{"items":[],"total":0,"page":1,"pageSize":10}""")));

        var res = await client.SearchAsync("ньочого", 1);

        Assert.True(res.Ok);
        Assert.Empty(res.Value!.Items);
        Assert.Equal(0, res.Value!.Total);
    }

    [Fact]
    public async Task SearchAsync_WithFilters_BuildsEquivalentQuery()
    {
        var handler = new StubHandler(_ => Json(200, SearchJson));
        var client = Create(handler);

        var filters = new SearchFilters
        {
            Type = "series",
            Genres = ["comedy", "drama"],
            YearFrom = 1990,
            YearTo = 1999,
            MinRating = 7,
        };
        await client.SearchAsync("друзі", 1, filters);

        var q = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("type=series", q);
        Assert.Contains("genres=comedy%2Cdrama", q);
        Assert.Contains("yearFrom=1990", q);
        Assert.Contains("yearTo=1999", q);
        Assert.Contains("minRating=7", q);
    }

    [Fact]
    public async Task SearchAsync_EmptyFilters_OmitFilterParams()
    {
        var handler = new StubHandler(_ => Json(200, SearchJson));
        var client = Create(handler);

        await client.SearchAsync("дюна", 1, new SearchFilters());

        var q = handler.LastRequest!.RequestUri!.Query;
        Assert.DoesNotContain("type", q);
        Assert.DoesNotContain("genres", q);
        Assert.DoesNotContain("yearFrom", q);
        Assert.DoesNotContain("yearTo", q);
        Assert.DoesNotContain("minRating", q);
    }

    // ---------------------------------------------------------------- схожість

    [Fact]
    public async Task SimilarBySeedsAsync_UsesGetFormWithInternalIds()
    {
        var handler = new StubHandler(_ => Json(200, SimilarJson));
        var client = Create(handler);

        var res = await client.SimilarBySeedsAsync([123, 456], 42, 10, "uk");

        Assert.True(res.Ok);
        var req = handler.LastRequest!;
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/api/similar/by-seeds", req.RequestUri!.AbsolutePath);
        Assert.Contains("ids=123%2C456", req.RequestUri.Query);
        Assert.Contains("limit=10", req.RequestUri.Query);
        Assert.Contains("lang=uk", req.RequestUri.Query);
        var item = Assert.Single(res.Value!);
        Assert.Equal(0.83, item.Score);
    }

    [Fact]
    public async Task SimilarByTextAsync_PostsText()
    {
        var handler = new StubHandler(_ => Json(200, SimilarJson));
        var client = Create(handler);

        var res = await client.SimilarByTextAsync("легкий ситком з детективом", 42, 10, "en");

        Assert.True(res.Ok);
        Assert.Equal("/api/similar/by-text", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("\"text\":\"легкий ситком з детективом\"", handler.LastBody);
        Assert.Contains("\"lang\":\"en\"", handler.LastBody);
    }

    // ---------------------------------------------------------------- списки

    [Fact]
    public async Task GetListsAsync_BuildsUrlWithoutProfileId_AndParsesUserRating()
    {
        var handler = new StubHandler(_ => Json(200, ListsJson));
        var client = Create(handler);

        var res = await client.GetListsAsync("watched", 900001, "uk");

        Assert.True(res.Ok);
        var req = handler.LastRequest!;
        Assert.Equal("/api/lists", req.RequestUri!.AbsolutePath);
        Assert.Contains("kind=watched", req.RequestUri.Query);
        Assert.DoesNotContain("profileId", req.RequestUri.Query);
        var item = Assert.Single(res.Value!);
        Assert.Equal(8, item.Rating);
        Assert.NotNull(item.CreatedAt);
        Assert.Null(item.TmdbId); // рядок списку без tmdbId читається без помилок
    }

    [Fact]
    public async Task AddToListAsync_PostsTitleIdToKindEndpoint_WithHeaders()
    {
        var handler = new StubHandler(_ => Json(201, "{}"));
        var client = Create(handler);

        var res = await client.AddToListAsync("watchlist", 456, 900001);

        Assert.True(res.Ok);
        var req = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/lists/watchlist", req.RequestUri!.AbsolutePath);
        Assert.Equal("900001", req.Headers.GetValues("X-Telegram-User-Id").Single());
        Assert.DoesNotContain("profileId", handler.LastBody);
        Assert.DoesNotContain("tmdbId", handler.LastBody);
        Assert.Contains("\"titleId\":456", handler.LastBody);
    }

    [Fact]
    public async Task AddToListAsync_MapsConflictAndNotFound()
    {
        var conflict = Create(new StubHandler(_ => Json(409, "{}")));
        var conflictRes = await conflict.AddToListAsync("watchlist", 456, 1);
        Assert.False(conflictRes.Ok);
        Assert.Equal(409, conflictRes.Status);

        var notFound = Create(new StubHandler(_ => Json(404, "{}")));
        var notFoundRes = await notFound.AddToListAsync("watched", 456, 1);
        Assert.True(notFoundRes.IsNotFound);
    }

    // ---------------------------------------------------------------- деградація

    [Fact]
    public async Task ErrorStatuses_AreClassified()
    {
        var notFound = Create(new StubHandler(_ => Json(404, "{}")));
        Assert.True((await notFound.SearchAsync("x", 1)).IsNotFound);

        var sema = Create(new StubHandler(_ => Json(503, "{}")));
        var res503 = await sema.SimilarByTextAsync("текст", 1);
        Assert.False(res503.Ok);
        Assert.True(res503.IsServiceUnavailable);

        var server = Create(new StubHandler(_ => Json(500, "{}")));
        var res500 = await server.SearchAsync("x", 1);
        Assert.False(res500.Ok);
        Assert.Equal(500, res500.Status);
    }

    [Fact]
    public async Task NetworkFailure_IsUnreachable()
    {
        var client = Create(new StubHandler(_ => throw new HttpRequestException("connection refused")));
        var res = await client.SearchAsync("x", 1);
        Assert.False(res.Ok);
        Assert.True(res.IsUnreachable);
    }

    [Fact]
    public async Task CancellationByTimeout_IsTimeout()
    {
        var client = Create(new StubHandler(_ => throw new TaskCanceledException()));
        var res = await client.SearchAsync("x", 1); // ct не скасовано користувачем
        Assert.False(res.Ok);
        Assert.True(res.IsTimeout);
    }

    [Fact]
    public async Task BrokenJson_IsDeserializationError()
    {
        var client = Create(new StubHandler(_ => Json(200, "{не-jсон")));
        var res = await client.SearchAsync("x", 1);
        Assert.False(res.Ok);
        Assert.Equal("deserialization", res.Error);
    }
}
