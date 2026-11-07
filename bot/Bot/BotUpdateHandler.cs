using MovieDiscovery.Bot.Api;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MovieDiscovery.Bot;

/// <summary>
/// Обробник оновлень Telegram (long polling): команди, callback-кнопки, звичайний текст = пошуковий запит.
/// Жодної власної логіки даних — все через REST API з PLAN.md. Профіль Е0/Е1 — єдиний, id = 1.
/// </summary>
public sealed class BotUpdateHandler
{
    public const int PageSize = 10;
    public const int SimilarLimit = 10;

    /// <summary>Е0/Е1: єдиний локальний профіль «Головний» (перший засіданий профіль, id = 1).</summary>
    public const int ProfileId = 1;

    /// <summary>«Короткий опис ~300 символів» (за ТЗ).</summary>
    private const int OverviewMax = 300;

    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    private readonly ApiClient _api;
    private readonly ChatState _state;
    private readonly Action<string> _log;

    public BotUpdateHandler(ApiClient api, ChatState state, Action<string>? log = null)
    {
        _api = api;
        _state = state;
        _log = log ?? (_ => { });
    }

    // ------------------------------------------------------------------ вхідні оновлення

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        try
        {
            switch (update)
            {
                case { Message: { } msg } when msg.Text is not null:
                    await HandleMessageAsync(botClient, msg, ct);
                    break;

                case { CallbackQuery: { } cb }:
                    await HandleCallbackAsync(botClient, cb, ct);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // штатне завершення бота
        }
        catch (Exception ex)
        {
            _log($"Update error: {ex.GetType().Name}: {ex.Message}");
            try
            {
                // принаймні зняти «годинник» / відповісти щось дружнє
                if (update.CallbackQuery is { } cbq)
                    await botClient.AnswerCallbackQuery(cbq.Id, cancellationToken: ct);
                else if (update.Message?.Chat is { } chat)
                    await botClient.SendMessage(chat.Id, Strings.S(_state.GetLang(chat.Id), "err.generic"), cancellationToken: ct);
            }
            catch
            {
                // не заважаємо циклу опитування
            }
        }
    }

    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message msg, CancellationToken ct)
    {
        var chatId = msg.Chat.Id;
        var lang = _state.GetLang(chatId);
        var text = msg.Text!.Trim();

        var cmd = CommandParser.Parse(text);
        if (cmd is null)
        {
            // текст без «/» — зручний пошук без команди
            await RunSearchAsync(botClient, chatId, text, page: 1, editMessageId: null, ct);
            return;
        }

        switch (cmd.Command)
        {
            case "start":
                await SendText(botClient, chatId, Strings.S(lang, "start.greeting"), Keyboards.MainMenu(lang), ct);
                break;

            case "help":
                await SendText(botClient, chatId, Strings.S(lang, "help.text"), null, ct);
                break;

            case "search":
                if (string.IsNullOrWhiteSpace(cmd.Args))
                    await SendText(botClient, chatId, Strings.S(lang, "search.usage"), null, ct);
                else
                    await RunSearchAsync(botClient, chatId, cmd.Args.Trim(), page: 1, editMessageId: null, ct);
                break;

            case "similar":
                await RunSimilarAsync(botClient, chatId, cmd.Args, lang, ct);
                break;

            case "text":
                await RunTextAsync(botClient, chatId, cmd.Args, lang, ct);
                break;

            case "watchlist":
                await RunListAsync(botClient, chatId, "watchlist", PageParser.Parse(cmd.Args), editMessageId: null, lang, ct);
                break;

            case "watched":
                await RunListAsync(botClient, chatId, "watched", PageParser.Parse(cmd.Args), editMessageId: null, lang, ct);
                break;

            case "lang":
            {
                var newLang = LangParser.Parse(cmd.Args);
                if (newLang is null)
                {
                    await SendText(botClient, chatId,
                        Strings.S(lang, "lang.usage") + "\n" + Strings.S(lang, "lang.current"), null, ct);
                }
                else
                {
                    _state.SetLang(chatId, newLang);
                    await SendText(botClient, chatId, Strings.S(newLang, "lang.switched"), null, ct);
                }

                break;
            }

            default:
                await SendText(botClient, chatId, Strings.S(lang, "cmd.unknown"), null, ct);
                break;
        }
    }

    // ------------------------------------------------------------------ callback-кнопки

    private async Task HandleCallbackAsync(ITelegramBotClient botClient, CallbackQuery cb, CancellationToken ct)
    {
        var data = cb.Data ?? string.Empty;
        var msg = cb.Message;
        if (msg is null)
        {
            await botClient.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
            return;
        }

        var chatId = msg.Chat.Id;
        var lang = _state.GetLang(chatId);

        // wl:{tmdbId} / wd:{tmdbId} — додати в watchlist / watched
        if (data.StartsWith("wl:", StringComparison.Ordinal) || data.StartsWith("wd:", StringComparison.Ordinal))
        {
            var kind = data.StartsWith("wl:", StringComparison.Ordinal) ? "watchlist" : "watched";
            if (!int.TryParse(data[3..], out var tmdbId) || tmdbId <= 0)
            {
                await botClient.AnswerCallbackQuery(cb.Id, text: Strings.S(lang, "err.generic"), cancellationToken: ct);
                return;
            }

            var res = await _api.AddToListAsync(ProfileId, kind, tmdbId, ct);
            var answer = res.Ok
                ? Strings.S(lang, kind == "watchlist" ? "add.done.watchlist" : "add.done.watched")
                : res.Status == 409 ? Strings.S(lang, "add.already")
                : res.IsNotFound ? Strings.S(lang, "err.notfound")
                : Strings.S(lang, "add.fail");

            await botClient.AnswerCallbackQuery(cb.Id, text: answer, showAlert: false, cancellationToken: ct);
            return;
        }

        // sr:prev / sr:next / sr:{page} — пагінація останнього пошуку
        if (data.StartsWith("sr:", StringComparison.Ordinal))
        {
            var state = _state.GetSearch(chatId);
            if (state is null)
            {
                await botClient.AnswerCallbackQuery(cb.Id, text: Strings.S(lang, "search.needFirst"), showAlert: false, cancellationToken: ct);
                return;
            }

            var arg = data[3..];
            var newPage = arg switch
            {
                "prev" => Math.Max(1, state.Page - 1),
                "next" => state.Page + 1,
                _ => int.TryParse(arg, out var p) ? p : state.Page,
            };

            await botClient.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
            await RunSearchAsync(botClient, chatId, state.Query, newPage, msg.MessageId, ct);
            return;
        }

        // lp:w:2 / lp:d:3 — пагінація списків
        if (data.StartsWith("lp:", StringComparison.Ordinal))
        {
            var parts = data.Split(':');
            if (parts.Length >= 3
                && (parts[1] == "w" || parts[1] == "d")
                && int.TryParse(parts[2], out var page))
            {
                var kind = parts[1] == "w" ? "watchlist" : "watched";
                await botClient.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                await RunListAsync(botClient, chatId, kind, page, msg.MessageId, lang, ct);
                return;
            }

            await botClient.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
            return;
        }

        // menu:{key} — підказки з головного меню
        if (data.StartsWith("menu:", StringComparison.Ordinal))
        {
            var key = data[5..];
            await botClient.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
            var textKey = key switch
            {
                "search" => "menu.search",
                "similar" => "menu.similar",
                "text" => "menu.text",
                "lists" => "menu.lists",
                "lang" => "menu.lang",
                _ => "help.text",
            };
            await SendText(botClient, chatId, Strings.S(lang, textKey), null, ct);
            return;
        }

        await botClient.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
    }

    // ------------------------------------------------------------------ сценарії

    private async Task RunSearchAsync(
        ITelegramBotClient botClient, long chatId, string query, int page, long? editMessageId, CancellationToken ct)
    {
        var lang = _state.GetLang(chatId);
        if (editMessageId is null)
            await SafeChatActionAsync(botClient, chatId, ct);

        var res = await _api.SearchAsync(query, page, PageSize, lang, ct);
        if (!res.Ok)
        {
            await ReplyErrorAsync(botClient, chatId, editMessageId, DescribeApiError(res, lang), ct);
            return;
        }

        var data = res.Value!;
        var pageSize = Math.Max(data.PageSize, 1);
        var totalPages = Math.Max(1, (int)Math.Ceiling(data.Total / (double)pageSize));
        _state.SetSearch(chatId, new SearchState(query, page, data.Total, pageSize));

        if (data.Items.Count == 0)
        {
            var key = page > 1 ? "search.pageEnd" : "search.none";
            await ReplyTextAsync(botClient, chatId, editMessageId, Strings.F(lang, key, TextFormatter.Escape(query)), null, ct);
            return;
        }

        var firstNumber = (page - 1) * PageSize + 1;
        var blocks = data.Items.Select((t, i) => Renderer.FormatTitleBlock(firstNumber + i, t, lang, OverviewMax)).ToList();
        var header = Renderer.FormatSearchHeader(query, page, totalPages, data.Total, lang)
                     + "\n" + Strings.S(lang, "search.legend");
        var (chunks, lastFirst) = Renderer.ChunkBlocks(header, blocks, firstNumber);
        var lastItems = data.Items.Skip(lastFirst - firstNumber).ToList();
        var markup = Keyboards.ResultsActions(lastItems, lastFirst, hasPrev: page > 1, hasNext: page < totalPages, lang);

        await SendOrEditAsync(botClient, chatId, editMessageId, chunks, markup, ct);
    }

    /// <summary>/similar Назва1; Назва2; ... — кожну назву резолвимо через /api/search?pageSize=1, потім by-seeds.</summary>
    private async Task RunSimilarAsync(ITelegramBotClient botClient, long chatId, string args, string lang, CancellationToken ct)
    {
        var seeds = SimilarSeedsParser.Parse(args);
        if (seeds.Count == 0)
        {
            await SendText(botClient, chatId, Strings.S(lang, "similar.usage"), null, ct);
            return;
        }

        if (seeds.Count > SimilarLimit)
        {
            await SendText(botClient, chatId, Strings.F(lang, "similar.tooMany", seeds.Count), null, ct);
            return;
        }

        await SafeChatActionAsync(botClient, chatId, ct);

        var tmdbIds = new List<int>(seeds.Count);
        var missing = new List<string>();
        foreach (var seed in seeds)
        {
            var res = await _api.SearchAsync(seed, page: 1, pageSize: 1, lang, ct);
            if (!res.Ok)
            {
                await ReplyErrorAsync(botClient, chatId, null, DescribeApiError(res, lang), ct);
                return;
            }

            var first = res.Value!.Items.FirstOrDefault();
            if (first is null)
                missing.Add(seed);
            else if (!tmdbIds.Contains(first.TmdbId))
                tmdbIds.Add(first.TmdbId);
        }

        if (missing.Count > 0)
        {
            await SendText(botClient, chatId,
                Strings.F(lang, "similar.missing", TextFormatter.Escape(string.Join(", ", missing))), null, ct);
            return;
        }

        var similar = await _api.SimilarBySeedsAsync(tmdbIds, SimilarLimit, lang, ct);
        if (!similar.Ok)
        {
            await ReplyErrorAsync(botClient, chatId, null, DescribeApiError(similar, lang), ct);
            return;
        }

        await SendSimilarResultsAsync(botClient, chatId,
            Strings.F(lang, "similar.header", TextFormatter.Truncate(TextFormatter.Escape(string.Join(", ", seeds)), 200)),
            similar.Value!, lang, ct);
    }

    /// <summary>/text опис побажання — POST /api/similar/by-text (503 → дружнє повідомлення про семантику).</summary>
    private async Task RunTextAsync(ITelegramBotClient botClient, long chatId, string args, string lang, CancellationToken ct)
    {
        var text = args.Trim();
        if (text.Length == 0)
        {
            await SendText(botClient, chatId, Strings.S(lang, "text.usage"), null, ct);
            return;
        }

        await SafeChatActionAsync(botClient, chatId, ct);

        var res = await _api.SimilarByTextAsync(text, SimilarLimit, lang, ct);
        if (!res.Ok)
        {
            await ReplyErrorAsync(botClient, chatId, null, DescribeApiError(res, lang), ct);
            return;
        }

        await SendSimilarResultsAsync(botClient, chatId,
            Strings.F(lang, "text.header", TextFormatter.Truncate(TextFormatter.Escape(text), 120)),
            res.Value!, lang, ct);
    }

    /// <summary>/watchlist [сторінка] | /watched [сторінка] — GET /api/lists (профіль 1), наскрізна нумерація.</summary>
    private async Task RunListAsync(
        ITelegramBotClient botClient, long chatId, string kind, int page, long? editMessageId, string lang, CancellationToken ct)
    {
        var res = await _api.GetListsAsync(ProfileId, kind, lang, ct);
        if (!res.Ok)
        {
            await ReplyErrorAsync(botClient, chatId, editMessageId, DescribeApiError(res, lang), ct);
            return;
        }

        var items = res.Value!;
        if (items.Count == 0)
        {
            await ReplyTextAsync(botClient, chatId, editMessageId, Strings.S(lang, "lists.empty"), null, ct);
            return;
        }

        var totalPages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);
        var slice = items.Skip((page - 1) * PageSize).Take(PageSize).ToList();

        var header = TextFormatter.KindEmoji(kind) + " "
                     + Strings.F(lang, kind == "watchlist" ? "lists.watchlist.header" : "lists.watched.header",
                         page, totalPages, items.Count);

        var firstNumber = (page - 1) * PageSize + 1;
        var blocks = slice.Select((t, i) =>
            Renderer.FormatTitleBlock(firstNumber + i, t, lang, OverviewMax, includeOverview: false, userRating: t.Rating)).ToList();
        var (chunks, _) = Renderer.ChunkBlocks(header, blocks, firstNumber);
        var markup = Keyboards.ListPages(kind, page, totalPages, lang);

        await SendOrEditAsync(botClient, chatId, editMessageId, chunks, markup, ct);
    }

    private async Task SendSimilarResultsAsync(
        ITelegramBotClient botClient, long chatId, string header, IReadOnlyList<TitleCard> items, string lang, CancellationToken ct)
    {
        var fullHeader = header + "\n" + Strings.S(lang, "search.legend");
        var blocks = items.Select((t, i) => Renderer.FormatTitleBlock(i + 1, t, lang, OverviewMax)).ToList();
        var (chunks, lastFirst) = Renderer.ChunkBlocks(fullHeader, blocks, 1);
        var lastItems = items.Skip(lastFirst - 1).ToList();
        var markup = Keyboards.ResultsActions(lastItems, lastFirst, hasPrev: false, hasNext: false, lang);

        await SendOrEditAsync(botClient, chatId, null, chunks, markup, ct);
    }

    // ------------------------------------------------------------------ помилки API

    private static string DescribeApiError<T>(ApiResult<T> res, string lang)
        => res.IsNotFound ? Strings.S(lang, "err.notfound")
            : res.IsServiceUnavailable ? Strings.S(lang, "err.semantic")
            : res.IsTimeout ? Strings.S(lang, "err.timeout")
            : Strings.S(lang, "err.api");

    // ------------------------------------------------------------------ надсилання/редагування

    private async Task SendText(
        ITelegramBotClient botClient, long chatId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
        => await botClient.SendMessage(chatId, text,
            parseMode: ParseMode.Html, linkPreviewOptions: NoPreview, replyMarkup: markup, cancellationToken: ct);

    private async Task<bool> TryEdit(
        ITelegramBotClient botClient, long chatId, long messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        try
        {
            await botClient.EditMessageText(chatId, (int)messageId, text,
                parseMode: ParseMode.Html, linkPreviewOptions: NoPreview, replyMarkup: markup, cancellationToken: ct);
            return true;
        }
        catch (ApiRequestException ex) when (ex.Message?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true; // та сама сторінка — це ок
        }
        catch (ApiRequestException)
        {
            return false; // напр. «message to edit not found» — надішлемо нове повідомлення
        }
    }

    private async Task ReplyTextAsync(
        ITelegramBotClient botClient, long chatId, long? editMessageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        if (editMessageId is { } mid && await TryEdit(botClient, chatId, mid, text, markup, ct))
            return;
        await SendText(botClient, chatId, text, markup, ct);
    }

    private async Task SendOrEditAsync(
        ITelegramBotClient botClient, long chatId, long? editMessageId, IReadOnlyList<string> chunks, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        if (editMessageId is null)
        {
            for (var i = 0; i < chunks.Count; i++)
                await SendText(botClient, chatId, chunks[i], i == chunks.Count - 1 ? markup : null, ct);
            return;
        }

        // редагуємо перший чанк у місці старого повідомлення, решту — новими повідомленнями
        if (!await TryEdit(botClient, chatId, editMessageId.Value, chunks[0], chunks.Count == 1 ? markup : null, ct))
        {
            for (var i = 0; i < chunks.Count; i++)
                await SendText(botClient, chatId, chunks[i], i == chunks.Count - 1 ? markup : null, ct);
            return;
        }

        for (var i = 1; i < chunks.Count; i++)
            await SendText(botClient, chatId, chunks[i], i == chunks.Count - 1 ? markup : null, ct);
    }

    private async Task ReplyErrorAsync(
        ITelegramBotClient botClient, long chatId, long? editMessageId, string text, CancellationToken ct)
        => await ReplyTextAsync(botClient, chatId, editMessageId, text, null, ct);

    private async Task SafeChatActionAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        try
        {
            await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log($"chat action failed: {ex.Message}");
        }
    }
}
