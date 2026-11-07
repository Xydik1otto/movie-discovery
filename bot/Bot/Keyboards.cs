using MovieDiscovery.Bot.Api;
using Telegram.Bot.Types.ReplyMarkups;

namespace MovieDiscovery.Bot;

/// <summary>Інлайн-клавіатури бота. Callback-дані компактні (ліміт Telegram 64 байти).</summary>
public static class Keyboards
{
    /// <summary>Головне меню /start: підказки-команди як callback-кнопки.</summary>
    public static InlineKeyboardMarkup MainMenu(string lang) => new(new[]
    {
        new[] { Btn("menu.search", "menu.searchBtn", lang), Btn("menu.similar", "menu.similarBtn", lang) },
        new[] { Btn("menu.text", "menu.textBtn", lang), Btn("menu.lists", "menu.listsBtn", lang) },
        new[] { Btn("menu.lang", "menu.langBtn", lang), Btn("menu.help", "menu.helpBtn", lang) },
    });

    /// <summary>
    /// Кнопки дій на кожен результат (номер у мітці відповідає номеру у списку):
    /// «➕ N» → wl:{tmdbId} (watchlist), «✅ N» → wd:{tmdbId} (watched); плюс пагінація пошуку sr:prev / sr:next.
    /// </summary>
    public static InlineKeyboardMarkup ResultsActions(
        IReadOnlyList<TitleCard> items, int firstNumber, bool hasPrev, bool hasNext, string lang)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        for (var i = 0; i < items.Count; i++)
        {
            var tmdbId = items[i].TmdbId;
            var n = firstNumber + i;
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(Strings.F(lang, "btn.addWatchlistN", n), $"wl:{tmdbId}"),
                InlineKeyboardButton.WithCallbackData(Strings.F(lang, "btn.addWatchedN", n), $"wd:{tmdbId}"),
            });
        }

        if (hasPrev || hasNext)
        {
            var nav = new List<InlineKeyboardButton>();
            if (hasPrev)
                nav.Add(InlineKeyboardButton.WithCallbackData(Strings.S(lang, "btn.prev"), "sr:prev"));
            if (hasNext)
                nav.Add(InlineKeyboardButton.WithCallbackData(Strings.S(lang, "btn.next"), "sr:next"));
            rows.Add(nav);
        }

        return new InlineKeyboardMarkup(rows);
    }

    /// <summary>Пагінація списків: lp:w:2 / lp:d:3 (w = watchlist, d = watched).</summary>
    public static InlineKeyboardMarkup ListPages(string kind, int page, int totalPages, string lang)
    {
        var shortKind = kind == "watchlist" ? "w" : "d";
        var nav = new List<InlineKeyboardButton>();
        if (page > 1)
            nav.Add(InlineKeyboardButton.WithCallbackData(Strings.S(lang, "btn.prev"), $"lp:{shortKind}:{page - 1}"));
        if (page < totalPages)
            nav.Add(InlineKeyboardButton.WithCallbackData(Strings.S(lang, "btn.next"), $"lp:{shortKind}:{page + 1}"));
        return nav.Count == 0
            ? new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton>())
            : new InlineKeyboardMarkup(nav);
    }

    private static InlineKeyboardButton Btn(string data, string labelKey, string lang)
        => InlineKeyboardButton.WithCallbackData(Strings.S(lang, labelKey), data);
}
