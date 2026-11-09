using System.Net;
using System.Text;
using MovieDiscovery.Bot;
using MovieDiscovery.Bot.Api;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

namespace MovieDiscovery.Bot.Tests;

/// <summary>
/// Тести обробника (MD-05/MD-06): внутрішні id у callbacks і API-викликах, заголовки
/// X-Telegram-User-Id/X-Bot-Key, стійкість до старих/підроблених payload'ів, фільтри /find.
/// Telegram SDK — фейк ITelegramSender; HTTP — StubHandler (без живого бекенда).
/// </summary>
public class BotUpdateHandlerTests
{
    private const long ChatId = 100500;
    private const long UserA = 111111;
    private const long UserB = 222222;

    private const string CatalogSearchJson =
        """{"items":[{"id":777001,"tmdbId":null,"type":"movie","title":"Ніч на Землі","year":1991,"genres":["драма"],"overview":"Таксі вночі"}],"total":1,"page":1,"pageSize":10}""";

    private const string SimilarJson =
        """{"items":[{"id":9,"tmdbId":null,"type":"movie","title":"Fargo","year":1996,"overview":"Кримінал","score":0.8}]}""";

    private const string ListsJson =
        """{"items":[{"id":9,"tmdbId":null,"type":"movie","title":"Dune","year":2021,"rating":7}]}""";

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
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return _respond(request);
        }
    }

    private sealed class FakeSender : ITelegramSender
    {
        public List<(long ChatId, string Text, InlineKeyboardMarkup? Markup)> Sent { get; } = [];
        public List<(long ChatId, int MessageId, string Text, InlineKeyboardMarkup? Markup)> Edits { get; } = [];
        public List<(string Id, string? Text, bool Alert)> CallbackAnswers { get; } = [];

        public Task<Message> SendMessageAsync(long chatId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
        {
            Sent.Add((chatId, text, markup));
            return Task.FromResult(new Message { Chat = new Chat { Id = chatId } });
        }

        public Task EditMessageTextAsync(long chatId, int messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
        {
            Edits.Add((chatId, messageId, text, markup));
            return Task.CompletedTask;
        }

        public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, CancellationToken cancellationToken = default)
        {
            CallbackAnswers.Add((callbackQueryId, text, showAlert));
            return Task.CompletedTask;
        }

        public Task SendChatActionAsync(long chatId, ChatAction action, CancellationToken ct) => Task.CompletedTask;
    }

    private static HttpResponseMessage Json(int status, string body)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static (BotUpdateHandler Handler, FakeSender Sender, StubHandler Http) Create(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var http = new StubHandler(respond ?? (_ => Json(200, CatalogSearchJson)));
        var api = new ApiClient(new HttpClient(http) { BaseAddress = new Uri("http://api.test/") }, "test-key");
        var sender = new FakeSender();
        var handler = new BotUpdateHandler(sender, api, new ChatState());
        return (handler, sender, http);
    }

    private static Update TextUpdate(string text, long userId = UserA)
        => new()
        {
            Message = new Message
            {
                Chat = new Chat { Id = ChatId },
                From = new User { Id = userId, IsBot = false, FirstName = "U" },
                Text = text,
            },
        };

    private static Update CallbackUpdate(string data, long userId = UserA)
        => new()
        {
            CallbackQuery = new CallbackQuery
            {
                Id = "cb" + Guid.NewGuid().ToString("N")[..8],
                Data = data,
                From = new User { Id = userId, IsBot = false, FirstName = "U" },
                Message = new Message { Chat = new Chat { Id = ChatId } },
            },
        };

    private static IEnumerable<string> CallbackData(InlineKeyboardMarkup? markup)
        => markup?.InlineKeyboard.SelectMany(row => row).Select(b => b.CallbackData ?? string.Empty) ?? [];

    // ---------------------------------------------------------------- пошук → внутрішні id у кнопках

    [Fact]
    public async Task Search_CatalogRowWithoutTmdbId_ButtonsUseInternalId()
    {
        var (handler, sender, _) = Create();

        await handler.HandleUpdateAsync(null!, TextUpdate("ніч на землі"), CancellationToken.None);

        var data = CallbackData(sender.Sent[^1].Markup).ToList();
        Assert.Contains("wl:777001", data);
        Assert.Contains("wd:777001", data);
        Assert.DoesNotContain("wl:0", data);
        Assert.DoesNotContain("wd:0", data);
        Assert.All(data, d => Assert.True(Encoding.UTF8.GetByteCount(d) <= 64, $"callback > 64 bytes: {d}"));
    }

    // ---------------------------------------------------------------- добавлення в список

    [Fact]
    public async Task AddToListCallback_PostsInternalTitleIdWithUserHeader()
    {
        var (handler, sender, http) = Create(_ => Json(201, "{}"));

        await handler.HandleUpdateAsync(null!, CallbackUpdate("wl:777001"), CancellationToken.None);

        var req = http.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/lists/watchlist", req.RequestUri!.AbsolutePath);
        Assert.Equal("{\"titleId\":777001}", http.LastBody);
        Assert.Equal("111111", req.Headers.GetValues("X-Telegram-User-Id").Single());
        Assert.Equal("test-key", req.Headers.GetValues("X-Bot-Key").Single());
        var answer = Assert.Single(sender.CallbackAnswers);
        Assert.Contains("Хочу подивитись", answer.Text);
    }

    [Fact]
    public async Task AddToList_TwoDifferentUsers_ProduceDifferentHeaders()
    {
        var (handler, _, http) = Create(_ => Json(201, "{}"));

        await handler.HandleUpdateAsync(null!, CallbackUpdate("wl:777001", UserA), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("wl:777001", UserB), CancellationToken.None);

        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(UserA.ToString(), http.Requests[0].Headers.GetValues("X-Telegram-User-Id").Single());
        Assert.Equal(UserB.ToString(), http.Requests[1].Headers.GetValues("X-Telegram-User-Id").Single());
    }

    // ---------------------------------------------------------------- старі/зламані/підроблені callbacks

    [Theory]
    [InlineData("wl:abc")]
    [InlineData("wl:0")]
    [InlineData("wl:-5")]
    [InlineData("wd:")]
    [InlineData("garbage")]
    [InlineData("zz:1:2")]
    [InlineData("sr:")]
    public async Task ForgedOrStaleCallbacks_AnsweredFriendly_NoApiCall(string data)
    {
        var (handler, sender, http) = Create(_ => throw new InvalidOperationException("API не має викликатися"));

        var ex = await Record.ExceptionAsync(
            () => handler.HandleUpdateAsync(null!, CallbackUpdate(data), CancellationToken.None));

        Assert.Null(ex);
        Assert.NotEmpty(sender.CallbackAnswers);
        Assert.All(sender.CallbackAnswers, a => Assert.False(string.IsNullOrWhiteSpace(a.Text)));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NullCallbackData_AnsweredFriendly_NoCrash()
    {
        var (handler, sender, http) = Create();
        var update = CallbackUpdate("whatever");
        update.CallbackQuery!.Data = null;

        var ex = await Record.ExceptionAsync(() => handler.HandleUpdateAsync(null!, update, CancellationToken.None));

        Assert.Null(ex);
        Assert.NotEmpty(sender.CallbackAnswers);
        Assert.Empty(http.Requests);
    }

    // ---------------------------------------------------------------- /similar по внутрішніх id

    [Fact]
    public async Task Similar_ResolvesSeedToInternalId_AndCallsGetBySeeds()
    {
        var (handler, sender, http) = Create(req =>
            req.RequestUri!.AbsolutePath == "/api/search"
                ? Json(200, CatalogSearchJson)
                : Json(200, SimilarJson));

        await handler.HandleUpdateAsync(null!, TextUpdate("/similar Ніч на Землі"), CancellationToken.None);

        var seeds = http.Requests
            .Where(r => r.RequestUri!.AbsolutePath == "/api/similar/by-seeds")
            .ToList();
        var seedReq = Assert.Single(seeds);
        Assert.Equal(HttpMethod.Get, seedReq.Method);
        Assert.Contains("ids=777001", seedReq.RequestUri!.Query);
        Assert.DoesNotContain("tmdb", seedReq.RequestUri.Query);

        var last = sender.Sent[^1];
        var data = CallbackData(last.Markup).ToList();
        Assert.Contains("wl:9", data); // результати similar теж мають внутрішні id у кнопках
    }

    // ---------------------------------------------------------------- /watchlist

    [Fact]
    public async Task Watchlist_ReadsWithoutProfileId_ShowsRating()
    {
        var (handler, sender, http) = Create(_ => Json(200, ListsJson));

        await handler.HandleUpdateAsync(null!, TextUpdate("/watchlist"), CancellationToken.None);

        var req = Assert.Single(http.Requests);
        Assert.Equal("/api/lists", req.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("profileId", req.RequestUri.Query);
        Assert.Contains("kind=watchlist", req.RequestUri.Query);
        Assert.Equal(UserA.ToString(), req.Headers.GetValues("X-Telegram-User-Id").Single());
        Assert.Contains("моя оцінка 7/10", sender.Sent[^1].Text);
    }

    // ---------------------------------------------------------------- MD-06: /find — фільтри

    [Fact]
    public async Task Find_WithoutArgs_ShowsUsage_NoApiCall()
    {
        var (handler, sender, http) = Create();

        await handler.HandleUpdateAsync(null!, TextUpdate("/find"), CancellationToken.None);

        Assert.Contains("Використання: /find", sender.Sent[^1].Text);
        Assert.Null(sender.Sent[^1].Markup);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Find_StartsBuilder_WithFilterButtons()
    {
        var (handler, sender, http) = Create();

        await handler.HandleUpdateAsync(null!, TextUpdate("/find дюна"), CancellationToken.None);

        var data = CallbackData(sender.Sent[^1].Markup).ToList();
        Assert.Contains("fi:type:movie", data);
        Assert.Contains("fi:type:off", data);
        Assert.Contains("fi:genre:comedy", data);
        Assert.Contains("fi:genre:science fiction", data);
        Assert.Contains("fi:year:2000-2009", data);
        Assert.Contains("fi:rating:7", data);
        Assert.Contains("fi:go", data);
        Assert.Contains("fi:reset", data);
        Assert.Contains("fi:cancel", data);
        Assert.All(data, d => Assert.True(Encoding.UTF8.GetByteCount(d) <= 64, $"callback > 64 bytes: {d}"));
        Assert.Contains("дюна", sender.Sent[^1].Text);
        Assert.Empty(http.Requests); // конструктор сам нічого не шукає
    }

    [Fact]
    public async Task FilterCallbacks_ToggleAndGo_SendEquivalentApiQuery()
    {
        var (handler, sender, http) = Create();

        await handler.HandleUpdateAsync(null!, TextUpdate("/find дюна"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:type:movie"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:genre:comedy"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:year:2000-2009"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:rating:7"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:go"), CancellationToken.None);

        // кожен toggle редагує builder-повідомлення на місці
        Assert.Equal(4, sender.Edits.Count);
        Assert.All(sender.Edits, e => Assert.NotEmpty(CallbackData(e.Markup)));

        var search = Assert.Single(http.Requests, r => r.RequestUri!.AbsolutePath == "/api/search");
        var q = search.RequestUri!.Query;
        Assert.Contains("q=" + Uri.EscapeDataString("дюна"), q);
        Assert.Contains("type=movie", q);
        Assert.Contains("genres=comedy", q);
        Assert.Contains("yearFrom=2000", q);
        Assert.Contains("yearTo=2009", q);
        Assert.Contains("minRating=7", q);
    }

    [Fact]
    public async Task FilterGo_ThenPagination_KeepsFilters()
    {
        var (handler, sender, http) = Create();

        await handler.HandleUpdateAsync(null!, TextUpdate("/find дюна"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:type:series"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:go"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("sr:next"), CancellationToken.None);

        var searches = http.Requests.Where(r => r.RequestUri!.AbsolutePath == "/api/search").ToList();
        Assert.Equal(2, searches.Count);
        Assert.All(searches, s => Assert.Contains("type=series", s.RequestUri!.Query));
        Assert.Contains("page=2", searches[1].RequestUri!.Query);
    }

    [Fact]
    public async Task FilterGenreToggle_SecondTap_RemovesGenre()
    {
        var (handler, _, http) = Create();

        await handler.HandleUpdateAsync(null!, TextUpdate("/find дюна"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:genre:comedy"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:genre:comedy"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:go"), CancellationToken.None);

        var search = Assert.Single(http.Requests, r => r.RequestUri!.AbsolutePath == "/api/search");
        Assert.DoesNotContain("genres", search.RequestUri!.Query);
    }

    [Fact]
    public async Task FilterResetAndCancel_BehaveCleanly()
    {
        var (handler, sender, http) = Create();

        await handler.HandleUpdateAsync(null!, TextUpdate("/find дюна"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:type:movie"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:reset"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:go"), CancellationToken.None);

        var search = Assert.Single(http.Requests, r => r.RequestUri!.AbsolutePath == "/api/search");
        Assert.DoesNotContain("type=", search.RequestUri!.Query);

        // після fi:go чернетка спожита — новий /find, потім cancel дає дружню відповідь
        await handler.HandleUpdateAsync(null!, TextUpdate("/find інша"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:cancel"), CancellationToken.None);
        Assert.Contains("скасовано", sender.CallbackAnswers[^1].Text ?? string.Empty);
    }

    [Fact]
    public async Task FilterCallbacks_WithoutDraft_AnswerFriendly_NoApiCall()
    {
        var (handler, sender, http) = Create(_ => throw new InvalidOperationException("API не має викликатися"));

        var ex = await Record.ExceptionAsync(
            () => handler.HandleUpdateAsync(null!, CallbackUpdate("fi:go"), CancellationToken.None));

        Assert.Null(ex);
        var answer = Assert.Single(sender.CallbackAnswers);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        Assert.True(answer.Alert); // дружня підказка видно юзеру
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task FilterForgedValues_AnsweredFriendly()
    {
        var (handler, sender, http) = Create();

        await handler.HandleUpdateAsync(null!, TextUpdate("/find дюна"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:year:not-a-range"), CancellationToken.None);
        await handler.HandleUpdateAsync(null!, CallbackUpdate("fi:whatever"), CancellationToken.None);

        Assert.Equal(2, sender.CallbackAnswers.Count);
        Assert.All(sender.CallbackAnswers, a => Assert.False(string.IsNullOrWhiteSpace(a.Text)));
        Assert.Empty(http.Requests);
    }
}
