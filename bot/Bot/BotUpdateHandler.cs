using MovieDiscovery.Bot.Api;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MovieDiscovery.Bot;

/// <summary>
/// Обробник оновлень Telegram (long polling): команди, callback-кнопки, звичайний текст = пошуковий запит.
/// Жодної власної логіки даних — все через REST API з PLAN.md. Профіль користувача бекенд резолвить
/// із заголовків X-Bot-Key + X-Telegram-User-Id (MD-05) — бот не надсилає profileId і не знає його.
/// Старі/зламані callback payload'и обробляються контрольовано: дружня відповідь, без виключень.
/// </summary>
public sealed class BotUpdateHandler
{
    public const int PageSize = 10;
    public const int SimilarLimit = 10;

    /// <summary>«Короткий опис ~300 символів» (за ТЗ).</summary>
    private const int OverviewMax = 300;

    private readonly ITelegramSender _tg;
    private readonly ApiClient _api;
    private readonly ChatState _state;
    private readonly Action<string> _log;

    public BotUpdateHandler(ITelegramSender sender, ApiClient api, ChatState state, Action<string>? log = null)
    {
        _tg = sender;
        _api = api;
        _state = state;
        _log = log ?? (_ => { });
    }

    /// <summary>Конструктор для прод-використання з реальним Telegram-клієнтом.</summary>
    public BotUpdateHandler(ITelegramBotClient botClient, ApiClient api, ChatState state, Action<string>? log = null)
        : this(new TelegramSender(botClient), api, state, log)
    {
    }

    // ------------------------------------------------------------------ вхідні оновлення

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
    {
        try
        {
            switch (update)
            {
                case { Message: { } msg } when msg.Text is not null:
                    await HandleMessageAsync(msg, ct);
                    break;

                case { CallbackQuery: { } cb }:
                    await HandleCallbackAsync(cb, ct);
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
                    await _tg.AnswerCallbackQueryAsync(cbq.Id, cancellationToken: ct);
                else if (update.Message?.Chat is { } chat)
                    await _tg.SendMessageAsync(chat.Id, Strings.S(_state.GetLang(chat.Id), "err.generic"), null, ct);
            }
            catch
            {
                // не заважаємо циклу опитування
            }
        }
    }

    private async Task HandleMessageAsync(Message msg, CancellationToken ct)
    {
        var chatId = msg.Chat.Id;
        var userId = msg.From?.Id ?? 0;
        var lang = _state.GetLang(chatId);
        var text = msg.Text!.Trim();

        var cmd = CommandParser.Parse(text);
        if (cmd is null)
        {
            // текст без «/» — зручний пошук без команди
            await RunSearchAsync(chatId, userId, text, page: 1, editMessageId: null, filters: null, ct);
            return;
        }

        switch (cmd.Command)
        {
            case "start":
                await SendText(chatId, Strings.S(lang, "start.greeting"), Keyboards.MainMenu(lang), ct);
                break;

            case "help":
                await SendText(chatId, Strings.S(lang, "help.text"), null, ct);
                break;

            case "search":
                if (string.IsNullOrWhiteSpace(cmd.Args))
                    await SendText(chatId, Strings.S(lang, "search.usage"), null, ct);
                else
                    await RunSearchAsync(chatId, userId, cmd.Args.Trim(), page: 1, editMessageId: null, filters: null, ct);
                break;

            case "find":
                await StartFilterAsync(chatId, cmd.Args, lang, ct);
                break;

            case "similar":
                await RunSimilarAsync(chatId, userId, cmd.Args, lang, ct);
                break;

            case "text":
                await RunTextAsync(chatId, userId, cmd.Args, lang, ct);
                break;

            case "watchlist":
                await RunListAsync(chatId, userId, "watchlist", PageParser.Parse(cmd.Args), editMessageId: null, lang, ct);
                break;

            case "watched":
                await RunListAsync(chatId, userId, "watched", PageParser.Parse(cmd.Args), editMessageId: null, lang, ct);
                break;

            case "lang":
            {
                var newLang = LangParser.Parse(cmd.Args);
                if (newLang is null)
                {
                    await SendText(chatId,
                        Strings.S(lang, "lang.usage") + "\n" + Strings.S(lang, "lang.current"), null, ct);
                }
                else
                {
                    _state.SetLang(chatId, newLang);
                    await SendText(chatId, Strings.S(newLang, "lang.switched"), null, ct);
                }

                break;
            }

            case "tunnel":
                await RunTunnelAsync(chatId, lang, ct);
                break;

            default:
                await SendText(chatId, Strings.S(lang, "cmd.unknown"), null, ct);
                break;
        }
    }

    // ------------------------------------------------------------------ callback-кнопки

    private async Task HandleCallbackAsync(CallbackQuery cb, CancellationToken ct)
    {
        var data = cb.Data ?? string.Empty;
        var msg = cb.Message;
        if (msg is null)
        {
            await _tg.AnswerCallbackQueryAsync(cb.Id, cancellationToken: ct);
            return;
        }

        var chatId = msg.Chat.Id;
        var userId = cb.From?.Id ?? 0;
        var lang = _state.GetLang(chatId);

        // wl:{id} / wd:{id} — додати в watchlist / watched (id — ВНУТРІШНІЙ id тайтла)
        if (data.StartsWith("wl:", StringComparison.Ordinal) || data.StartsWith("wd:", StringComparison.Ordinal))
        {
            var kind = data.StartsWith("wl:", StringComparison.Ordinal) ? "watchlist" : "watched";
            if (!int.TryParse(data[3..], out var titleId) || titleId <= 0)
            {
                // старий/зламаний/підроблений payload — дружня загальна відповідь, без дій
                await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "err.generic"), showAlert: true, cancellationToken: ct);
                return;
            }

            var res = await _api.AddToListAsync(kind, titleId, userId, ct);
            var answer = res.Ok
                ? Strings.S(lang, kind == "watchlist" ? "add.done.watchlist" : "add.done.watched")
                : res.Status == 409 ? Strings.S(lang, "add.already")
                : res.IsNotFound ? Strings.S(lang, "err.notfound")
                : res.Status == 403 ? Strings.S(lang, "err.api")
                : Strings.S(lang, "add.fail");

            await _tg.AnswerCallbackQueryAsync(cb.Id, text: answer, showAlert: false, cancellationToken: ct);
            return;
        }

        // sr:prev / sr:next / sr:{page} — пагінація останнього пошуку
        if (data.StartsWith("sr:", StringComparison.Ordinal))
        {
            var state = _state.GetSearch(chatId);
            if (state is null)
            {
                await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "search.needFirst"), showAlert: false, cancellationToken: ct);
                return;
            }

            var arg = data[3..];
            var newPage = arg switch
            {
                "prev" => Math.Max(1, state.Page - 1),
                "next" => state.Page + 1,
                _ => int.TryParse(arg, out var p) ? p : state.Page,
            };

            await _tg.AnswerCallbackQueryAsync(cb.Id, cancellationToken: ct);
            await RunSearchAsync(chatId, userId, state.Query, newPage, msg.MessageId, state.Filters, ct);
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
                await _tg.AnswerCallbackQueryAsync(cb.Id, cancellationToken: ct);
                await RunListAsync(chatId, userId, kind, page, msg.MessageId, lang, ct);
                return;
            }

            await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "err.generic"), cancellationToken: ct);
            return;
        }

        // menu:{key} — підказки з головного меню
        if (data.StartsWith("menu:", StringComparison.Ordinal))
        {
            var key = data[5..];
            await _tg.AnswerCallbackQueryAsync(cb.Id, cancellationToken: ct);
            var textKey = key switch
            {
                "search" => "menu.search",
                "similar" => "menu.similar",
                "text" => "menu.text",
                "find" => "menu.find",
                "lists" => "menu.lists",
                "lang" => "menu.lang",
                _ => "help.text",
            };
            await SendText(chatId, Strings.S(lang, textKey), null, ct);
            return;
        }

        // fi:* — конструктор фільтрів (MD-06)
        if (data.StartsWith("fi:", StringComparison.Ordinal))
        {
            await HandleFilterCallbackAsync(cb, chatId, userId, msg, data, lang, ct);
            return;
        }

        // невідомий/застарілий payload — дружня відповідь, жодних дій
        await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "err.generic"), showAlert: true, cancellationToken: ct);
    }

    // ------------------------------------------------------------------ фільтри (MD-06)

    private async Task StartFilterAsync(long chatId, string? args, string lang, CancellationToken ct)
    {
        var query = (args ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            await SendText(chatId, Strings.S(lang, "find.usage"), null, ct);
            return;
        }

        _state.SetFilter(chatId, new FilterDraft(query));
        await SendText(chatId, FilterBuilderText(chatId, lang), Keyboards.FilterBuilder(_state.GetFilter(chatId)!, lang), ct);
    }

    private async Task HandleFilterCallbackAsync(
        CallbackQuery cb, long chatId, long userId, Message msg, string data, string lang, CancellationToken ct)
    {
        var draft = _state.GetFilter(chatId);
        var parts = data.Split(':', 3);
        if (draft is null || parts.Length < 2)
        {
            await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "find.needFirst"), showAlert: true, cancellationToken: ct);
            return;
        }

        switch (parts[1])
        {
            case "type":
                draft = draft with { Type = parts.Length > 2 && parts[2] != "off" ? parts[2] : null };
                break;

            case "genre" when parts.Length > 2:
                draft = draft.ToggleGenre(parts[2]);
                break;

            case "year" when parts.Length > 2:
            {
                var (from, to, ok) = ParseYearRange(parts[2]);
                if (!ok)
                {
                    await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "err.generic"), showAlert: true, cancellationToken: ct);
                    return;
                }

                draft = draft with { YearFrom = from, YearTo = to };
                break;
            }

            case "rating":
                draft = parts.Length > 2 && int.TryParse(parts[2], out var r) && r is >= 1 and <= 10
                    ? draft with { MinRating = draft.MinRating == r ? null : r }
                    : draft with { MinRating = null };
                break;

            case "go":
                // чернетку спожито; пошук іде з фільтрами, пагінація збереже їх у SearchState
                _state.ClearFilter(chatId);
                await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "find.searching"), showAlert: false, cancellationToken: ct);
                await RunSearchAsync(chatId, userId, draft.Query, 1, editMessageId: null, filters: draft, ct);
                return;

            case "reset":
                draft = new FilterDraft(draft.Query);
                break;

            case "cancel":
                _state.ClearFilter(chatId);
                await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "find.cancelled"), cancellationToken: ct);
                await TryEditBuilder(chatId, msg.MessageId, Strings.S(lang, "find.cancelled"), null, ct);
                return;

            default:
                await _tg.AnswerCallbackQueryAsync(cb.Id, text: Strings.S(lang, "err.generic"), showAlert: true, cancellationToken: ct);
                return;
        }

        _state.SetFilter(chatId, draft);
        await _tg.AnswerCallbackQueryAsync(cb.Id, cancellationToken: ct);
        await TryEditBuilder(chatId, msg.MessageId, FilterBuilderText(chatId, lang), Keyboards.FilterBuilder(draft, lang), ct);
    }

    private static (int? From, int? To, bool Ok) ParseYearRange(string raw)
    {
        var bounds = raw.Split('-');
        if (bounds.Length != 2
            || !int.TryParse(bounds[0], out var from)
            || !int.TryParse(bounds[1], out var to)
            || from < 0 || to < 0)
            return (null, null, false);

        return (from == 0 ? null : from, to == 0 ? null : to, true);
    }

    private string FilterBuilderText(long chatId, string lang)
    {
        var draft = _state.GetFilter(chatId)!;
        var summary = new List<string>();
        if (draft.Type is { } type)
            summary.Add(Strings.S(lang, $"fi.type.{(Keyboards.FilterTypes.Contains(type) ? type : "any")}"));
        if (draft.Genres.Count > 0)
            summary.Add(string.Join(", ", draft.Genres.Select(g => Strings.S(lang, $"fi.genre.{g}"))));
        if (draft.YearFrom is { } yf || draft.YearTo is { } yt)
        {
            var from = draft.YearFrom?.ToString() ?? "…";
            var to = draft.YearTo?.ToString() ?? "…";
            summary.Add(Strings.F(lang, "fi.yearsSummary", from, to));
        }

        if (draft.MinRating is { } mr)
            summary.Add(Strings.F(lang, "fi.ratingSummary", mr));

        var state = summary.Count == 0
            ? Strings.S(lang, "find.state.none")
            : string.Join(" · ", summary);
        return Strings.F(lang, "find.state", TextFormatter.Escape(draft.Query), state);
    }

    private async Task TryEditBuilder(long chatId, int messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        try
        {
            await _tg.EditMessageTextAsync(chatId, messageId, text, markup, ct);
        }
        catch (Exception)
        {
            await SendText(chatId, text, markup, ct); // повідомлення редагувати не вдалося — надішлемо нове
        }
    }

    // ------------------------------------------------------------------ сценарії

    private async Task RunSearchAsync(
        long chatId, long userId, string query, int page, long? editMessageId, FilterDraft? filters, CancellationToken ct)
    {
        var lang = _state.GetLang(chatId);
        if (editMessageId is null)
            await SafeChatActionAsync(chatId, ct);

        var res = await _api.SearchAsync(query, userId, filters?.ToApiFilters(), page, PageSize, lang, ct);
        if (!res.Ok)
        {
            await ReplyErrorAsync(chatId, editMessageId, DescribeApiError(res, lang), ct);
            return;
        }

        var data = res.Value!;
        var pageSize = Math.Max(data.PageSize, 1);
        var totalPages = Math.Max(1, (int)Math.Ceiling(data.Total / (double)pageSize));
        _state.SetSearch(chatId, new SearchState(query, page, data.Total, pageSize, filters));

        if (data.Items.Count == 0)
        {
            var key = page > 1 ? "search.pageEnd" : "search.none";
            await ReplyTextAsync(chatId, editMessageId, Strings.F(lang, key, TextFormatter.Escape(query)), null, ct);
            return;
        }

        var firstNumber = (page - 1) * PageSize + 1;
        var blocks = data.Items.Select((t, i) => Renderer.FormatTitleBlock(firstNumber + i, t, lang, OverviewMax)).ToList();
        var header = Renderer.FormatSearchHeader(query, page, totalPages, data.Total, lang)
                     + "\n" + Strings.S(lang, "search.legend");
        var (chunks, lastFirst) = Renderer.ChunkBlocks(header, blocks, firstNumber);
        var lastItems = data.Items.Skip(lastFirst - firstNumber).ToList();
        var markup = Keyboards.ResultsActions(lastItems, lastFirst, hasPrev: page > 1, hasNext: page < totalPages, lang);

        await SendOrEditAsync(chatId, editMessageId, chunks, markup, ct);
    }

    /// <summary>/similar Назва1; Назва2; ... — кожну назву резолвимо через /api/search?pageSize=1,
    /// потім GET /api/similar/by-seeds по ВНУТРІШНІХ id (MD-05: tmdbId у більшості каталогу null).</summary>
    private async Task RunSimilarAsync(long chatId, long userId, string? args, string lang, CancellationToken ct)
    {
        var seeds = SimilarSeedsParser.Parse(args);
        if (seeds.Count == 0)
        {
            await SendText(chatId, Strings.S(lang, "similar.usage"), null, ct);
            return;
        }

        if (seeds.Count > SimilarLimit)
        {
            await SendText(chatId, Strings.F(lang, "similar.tooMany", seeds.Count), null, ct);
            return;
        }

        await SafeChatActionAsync(chatId, ct);

        var ids = new List<int>(seeds.Count);
        var missing = new List<string>();
        foreach (var seed in seeds)
        {
            var res = await _api.SearchAsync(seed, userId, filters: null, page: 1, pageSize: 1, lang, ct);
            if (!res.Ok)
            {
                await ReplyErrorAsync(chatId, null, DescribeApiError(res, lang), ct);
                return;
            }

            var first = res.Value!.Items.FirstOrDefault();
            if (first is null)
                missing.Add(seed);
            else if (first.Id > 0 && !ids.Contains(first.Id))
                ids.Add(first.Id);
        }

        if (missing.Count > 0)
        {
            await SendText(chatId,
                Strings.F(lang, "similar.missing", TextFormatter.Escape(string.Join(", ", missing))), null, ct);
            return;
        }

        var similar = await _api.SimilarBySeedsAsync(ids, userId, SimilarLimit, lang, ct);
        if (!similar.Ok)
        {
            await ReplyErrorAsync(chatId, null, DescribeApiError(similar, lang), ct);
            return;
        }

        await SendSimilarResultsAsync(chatId,
            Strings.F(lang, "similar.header", TextFormatter.Truncate(TextFormatter.Escape(string.Join(", ", seeds)), 200)),
            similar.Value!, lang, ct);
    }

    /// <summary>/text опис побажання — POST /api/similar/by-text (503 → дружнє повідомлення про семантику).</summary>
    private async Task RunTextAsync(long chatId, long userId, string? args, string lang, CancellationToken ct)
    {
        var text = (args ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            await SendText(chatId, Strings.S(lang, "text.usage"), null, ct);
            return;
        }

        await SafeChatActionAsync(chatId, ct);

        var res = await _api.SimilarByTextAsync(text, userId, SimilarLimit, lang, ct);
        if (!res.Ok)
        {
            await ReplyErrorAsync(chatId, null, DescribeApiError(res, lang), ct);
            return;
        }

        await SendSimilarResultsAsync(chatId,
            Strings.F(lang, "text.header", TextFormatter.Truncate(TextFormatter.Escape(text), 120)),
            res.Value!, lang, ct);
    }

    /// <summary>/watchlist [сторінка] | /watched [сторінка] — GET /api/lists (профіль з заголовків), наскрізна нумерація.</summary>
    private async Task RunListAsync(
        long chatId, long userId, string kind, int page, long? editMessageId, string lang, CancellationToken ct)
    {
        var res = await _api.GetListsAsync(kind, userId, lang, ct);
        if (!res.Ok)
        {
            await ReplyErrorAsync(chatId, editMessageId, DescribeApiError(res, lang), ct);
            return;
        }

        var items = res.Value!;
        if (items.Count == 0)
        {
            await ReplyTextAsync(chatId, editMessageId, Strings.S(lang, "lists.empty"), null, ct);
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

        await SendOrEditAsync(chatId, editMessageId, chunks, markup, ct);
    }

    private async Task SendSimilarResultsAsync(
        long chatId, string header, IReadOnlyList<TitleCard> items, string lang, CancellationToken ct)
    {
        var fullHeader = header + "\n" + Strings.S(lang, "search.legend");
        var blocks = items.Select((t, i) => Renderer.FormatTitleBlock(i + 1, t, lang, OverviewMax)).ToList();
        var (chunks, lastFirst) = Renderer.ChunkBlocks(fullHeader, blocks, 1);
        var lastItems = items.Skip(lastFirst - 1).ToList();
        var markup = Keyboards.ResultsActions(lastItems, lastFirst, hasPrev: false, hasNext: false, lang);

        await SendOrEditAsync(chatId, null, chunks, markup, ct);
    }

    // ------------------------------------------------------------------ /tunnel

    /// <summary>
    /// /tunnel: свіжий публічний URL quick-тунелю (особистий застосунок — доступно будь-якому
    /// юзеру бота, без адмін-перевірки). URL беремо з логу тунелю, живість перевіряємо через
    /// /api/health; мертвий/нема URL → docker restart контейнера tunnel і повторна спроба,
    /// інакше — чесне повідомлення про збій.
    /// </summary>
    private async Task RunTunnelAsync(long chatId, string lang, CancellationToken ct)
    {
        var tunnel = new TunnelService();
        var url = tunnel.ReadUrl();
        if (url is not null && await tunnel.IsHealthyAsync(url, ct))
        {
            await SendText(chatId, Strings.F(lang, "tunnel.ok", url), null, ct);
            return;
        }

        _log("Tunnel: URL missing or unhealthy — restarting " + TunnelService.ContainerName);
        await SendText(chatId, Strings.S(lang, "tunnel.restarting"), null, ct);

        if (!tunnel.RestartTunnel())
        {
            _log("Tunnel: docker restart failed");
            await SendText(chatId, Strings.S(lang, "tunnel.fail"), null, ct);
            return;
        }

        // після рестарту tee обнуляє лог і новий cloudflared пише туди вже новий URL
        await Task.Delay(TunnelService.RestartSettle, ct);
        var fresh = tunnel.ReadUrl();
        if (fresh is not null && await tunnel.IsHealthyAsync(fresh, ct))
        {
            await SendText(chatId, Strings.F(lang, "tunnel.ok", fresh), null, ct);
            return;
        }

        _log("Tunnel: still no healthy URL after restart");
        await SendText(chatId, Strings.S(lang, "tunnel.fail"), null, ct);
    }

    // ------------------------------------------------------------------ помилки API

    private static string DescribeApiError<T>(ApiResult<T> res, string lang)
        => res.IsNotFound ? Strings.S(lang, "err.notfound")
            : res.IsServiceUnavailable ? Strings.S(lang, "err.semantic")
            : res.IsTimeout ? Strings.S(lang, "err.timeout")
            : Strings.S(lang, "err.api");

    // ------------------------------------------------------------------ надсилання/редагування

    private async Task SendText(long chatId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
        => await _tg.SendMessageAsync(chatId, text, markup, ct);

    private async Task<bool> TryEdit(long chatId, long messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        try
        {
            await _tg.EditMessageTextAsync(chatId, (int)messageId, text, markup, ct);
            return true;
        }
        catch (Exception ex) when (IsNotModified(ex))
        {
            return true; // та сама сторінка — це ок
        }
        catch (Exception)
        {
            return false; // напр. «message to edit not found» — надішлемо нове повідомлення
        }
    }

    private static bool IsNotModified(Exception ex)
        => ex.Message?.Contains("message is not modified", StringComparison.OrdinalIgnoreCase) == true;

    private async Task ReplyTextAsync(
        long chatId, long? editMessageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        if (editMessageId is { } mid && await TryEdit(chatId, mid, text, markup, ct))
            return;
        await SendText(chatId, text, markup, ct);
    }

    private async Task SendOrEditAsync(
        long chatId, long? editMessageId, IReadOnlyList<string> chunks, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        if (editMessageId is null)
        {
            for (var i = 0; i < chunks.Count; i++)
                await SendText(chatId, chunks[i], i == chunks.Count - 1 ? markup : null, ct);
            return;
        }

        // редагуємо перший чанк у місці старого повідомлення, решту — новими повідомленнями
        if (!await TryEdit(chatId, editMessageId.Value, chunks[0], chunks.Count == 1 ? markup : null, ct))
        {
            for (var i = 0; i < chunks.Count; i++)
                await SendText(chatId, chunks[i], i == chunks.Count - 1 ? markup : null, ct);
            return;
        }

        for (var i = 1; i < chunks.Count; i++)
            await SendText(chatId, chunks[i], i == chunks.Count - 1 ? markup : null, ct);
    }

    private async Task ReplyErrorAsync(
        long chatId, long? editMessageId, string text, CancellationToken ct)
        => await ReplyTextAsync(chatId, editMessageId, text, null, ct);

    private async Task SafeChatActionAsync(long chatId, CancellationToken ct)
    {
        try
        {
            await _tg.SendChatActionAsync(chatId, ChatAction.Typing, ct);
        }
        catch (Exception ex)
        {
            _log($"chat action failed: {ex.Message}");
        }
    }
}
