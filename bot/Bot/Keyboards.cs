using MovieDiscovery.Bot.Api;
using Telegram.Bot.Types.ReplyMarkups;

namespace MovieDiscovery.Bot;

/// <summary>Інлайн-клавіатури бота. Callback-дані компактні (ліміт Telegram 64 байти).</summary>
public static class Keyboards
{
    /// <summary>Канонічні жанри-пресети для конструктора фільтрів (MD-06); значення — англійські lowercase.</summary>
    public static readonly IReadOnlyList<string> FilterGenres =
    [
        "action", "comedy", "drama", "thriller", "horror", "science fiction",
        "animation", "adventure", "crime", "romance", "fantasy", "documentary",
    ];

    /// <summary>Типи для конструктора фільтрів; "off" = будь-який.</summary>
    public static readonly IReadOnlyList<string> FilterTypes = ["movie", "series", "anime", "manga"];

    /// <summary>Пресети років (from, to); 0 = без межі.</summary>
    public static readonly IReadOnlyList<(int From, int To)> FilterYears = [(0, 1999), (2000, 2009), (2010, 2019), (2020, 0)];

    /// <summary>Пресети мінімального рейтингу.</summary>
    public static readonly IReadOnlyList<int> FilterRatings = [6, 7, 8, 9];

    /// <summary>CR-06: пресети країн (ISO-2 коди, як у FR-19 web-фільтрів).</summary>
    public static readonly IReadOnlyList<string> FilterCountries = ["UA", "US", "GB", "KR", "JP"];

    /// <summary>CR-06: пресети тривалості (min, max у хвилинах); 0 = без межі. Callback: fi:runtime:min-max.</summary>
    public static readonly IReadOnlyList<(int Min, int Max)> FilterRuntimes = [(0, 59), (60, 120), (121, 0)];

    /// <summary>CR-06: статуси тайтла (значення як у БД: airing/finished).</summary>
    public static readonly IReadOnlyList<string> FilterStatuses = ["airing", "finished"];

    /// <summary>Головне меню /start: підказки-команди як callback-кнопки.</summary>
    public static InlineKeyboardMarkup MainMenu(string lang) => new(new[]
    {
        new[] { Btn("menu.search", "menu.searchBtn", lang), Btn("menu.similar", "menu.similarBtn", lang) },
        new[] { Btn("menu.text", "menu.textBtn", lang), Btn("menu.lists", "menu.listsBtn", lang) },
        new[] { Btn("menu.find", "menu.findBtn", lang), Btn("menu.lang", "menu.langBtn", lang) },
        new[] { Btn("menu.help", "menu.helpBtn", lang) },
    });

    /// <summary>
    /// Кнопки дій на кожен результат (номер у мітці відповідає номеру у списку):
    /// «➕ N» → wl:{id} (watchlist), «✅ N» → wd:{id} (watched), де id — ВНУТРІШНІЙ TitleCard.Id.
    /// Для рядків з Id ≤ 0 (пошкоджені дані) кнопки НЕ додаються — жодних 0/негативних payload'ів.
    /// Плюс пагінація пошуку sr:prev / sr:next.
    /// </summary>
    public static InlineKeyboardMarkup ResultsActions(
        IReadOnlyList<TitleCard> items, int firstNumber, bool hasPrev, bool hasNext, string lang)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        for (var i = 0; i < items.Count; i++)
        {
            var id = items[i].Id;
            if (id <= 0)
                continue;

            var n = firstNumber + i;
            rows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(Strings.F(lang, "btn.addWatchlistN", n), $"wl:{id}"),
                InlineKeyboardButton.WithCallbackData(Strings.F(lang, "btn.addWatchedN", n), $"wd:{id}"),
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

    /// <summary>
    /// Конструктор фільтрів (MD-06): тип / жанри / роки / рейтинг + go/reset/cancel.
    /// Дані callback'ів: fi:type:{v}|fi:genre:{g}|fi:year:{from}-{to}|fi:rating:{r}|fi:go|fi:reset|fi:cancel.
    /// Усі значення короткі ASCII — ліміт 64 байти не порушується.
    /// </summary>
    public static InlineKeyboardMarkup FilterBuilder(FilterDraft draft, string lang)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();

        // тип
        var typeRow = new List<InlineKeyboardButton>
        {
            BtnRaw("fi:type:off", Marked(draft.Type is null, Strings.S(lang, "fi.type.any"))),
        };
        typeRow.AddRange(FilterTypes.Select(t =>
            BtnRaw($"fi:type:{t}", Marked(draft.Type == t, Strings.S(lang, $"fi.type.{t}")))));
        rows.Add(typeRow);

        // жанри (2 рядки по 6, перемикачі)
        for (var i = 0; i < FilterGenres.Count; i += 6)
        {
            rows.Add(FilterGenres.Skip(i).Take(6).Select(g =>
                BtnRaw($"fi:genre:{g}", Marked(draft.Genres.Contains(g), Strings.S(lang, $"fi.genre.{g}")))));
        }

        // роки
        var yearLabels = new[] { "fi.year.past", "fi.year.2000s", "fi.year.2010s", "fi.year.2020s" };
        var yearRow = new List<InlineKeyboardButton>();
        for (var i = 0; i < FilterYears.Count; i++)
        {
            var (from, to) = FilterYears[i];
            var selected = draft.YearFrom == (from == 0 ? null : from) && draft.YearTo == (to == 0 ? null : to);
            yearRow.Add(BtnRaw($"fi:year:{from}-{to}", Marked(selected, Strings.S(lang, yearLabels[i]))));
        }

        rows.Add(yearRow);

        // рейтинг
        rows.Add(FilterRatings.Select(r =>
            BtnRaw($"fi:rating:{r}", Marked(draft.MinRating == r, Strings.F(lang, "fi.rating.n", r)))));

        // CR-06: країни (одиночний вибір; повторний клік по вибраній — скидає)
        rows.Add(FilterCountries.Select(c =>
            BtnRaw($"fi:country:{c}", Marked(draft.Country == c, c))));
        // CR-06: тривалість (пресети хвилин)
        var runtimeLabels = new[] { "fi.runtime.short", "fi.runtime.feature", "fi.runtime.long" };
        var runtimeRow = new List<InlineKeyboardButton>();
        for (var i = 0; i < FilterRuntimes.Count; i++)
        {
            var (min, max) = FilterRuntimes[i];
            var selected = draft.RuntimeMin == (min == 0 ? null : min) && draft.RuntimeMax == (max == 0 ? null : max);
            runtimeRow.Add(BtnRaw($"fi:runtime:{min}-{max}", Marked(selected, Strings.S(lang, runtimeLabels[i]))));
        }
        rows.Add(runtimeRow);
        // CR-06: статус тайтла (airing/finished)
        rows.Add(FilterStatuses.Select(s =>
            BtnRaw($"fi:status:{s}", Marked(draft.Status == s, Strings.S(lang, $"fi.status.{s}")))));

        // керування
        rows.Add(new[]
        {
            Btn("fi:go", Strings.S(lang, "fi.btnGo"), lang),
            Btn("fi:reset", Strings.S(lang, "fi.btnReset"), lang),
            Btn("fi:cancel", Strings.S(lang, "fi.btnCancel"), lang),
        });

        return new InlineKeyboardMarkup(rows);
    }

    private static string Marked(bool selected, string label) => selected ? "✓ " + label : label;

    private static InlineKeyboardButton Btn(string data, string labelKey, string lang)
        => InlineKeyboardButton.WithCallbackData(Strings.S(lang, labelKey), data);

    private static InlineKeyboardButton BtnRaw(string data, string label)
        => InlineKeyboardButton.WithCallbackData(label, data);
}
