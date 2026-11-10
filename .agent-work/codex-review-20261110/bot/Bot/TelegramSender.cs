using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MovieDiscovery.Bot;

/// <summary>
/// Вузька абстракція над ITelegramBotClient: лише те, що використовує обробник.
/// Існує для юніт-тестів (фейк замість великого SDK-інтерфейсу); прод-реалізація —
/// <see cref="TelegramSender"/> (тонкий адаптер над Telegram.Bot).
/// </summary>
public interface ITelegramSender
{
    Task<Message> SendMessageAsync(long chatId, string text, InlineKeyboardMarkup? markup, CancellationToken ct);

    /// <summary>Редагує текст повідомлення; кидає ApiRequestException у разі помилки Telegram.</summary>
    Task EditMessageTextAsync(long chatId, int messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct);

    Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, CancellationToken cancellationToken = default);

    Task SendChatActionAsync(long chatId, ChatAction action, CancellationToken ct);
}

/// <summary>Адаптер ITelegramSender → Telegram.Bot (HTML parse mode, без прев'ю лінків).</summary>
public sealed class TelegramSender(ITelegramBotClient client) : ITelegramSender
{
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    public Task<Message> SendMessageAsync(long chatId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
        => client.SendMessage(chatId, text, parseMode: ParseMode.Html, linkPreviewOptions: NoPreview,
            replyMarkup: markup, cancellationToken: ct);

    public Task EditMessageTextAsync(long chatId, int messageId, string text, InlineKeyboardMarkup? markup, CancellationToken ct)
        => client.EditMessageText(chatId, messageId, text, parseMode: ParseMode.Html, linkPreviewOptions: NoPreview,
            replyMarkup: markup, cancellationToken: ct);

    public Task AnswerCallbackQueryAsync(string callbackQueryId, string? text = null, bool showAlert = false, CancellationToken cancellationToken = default)
        => client.AnswerCallbackQuery(callbackQueryId, text: text, showAlert: showAlert, cancellationToken: cancellationToken);

    public Task SendChatActionAsync(long chatId, ChatAction action, CancellationToken ct)
        => client.SendChatAction(chatId, action, cancellationToken: ct);
}
