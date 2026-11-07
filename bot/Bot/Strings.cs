using System.Globalization;

namespace MovieDiscovery.Bot;

/// <summary>
/// Локалізація uk/en (російської мови в бота нема — свідоме рішення власника, див. TZ).
/// Ключі обох словників мають збігатися (перевіряється юніт-тестом).
/// </summary>
public static class Strings
{
    public const string Uk = "uk";
    public const string En = "en";

    private static readonly IReadOnlyDictionary<string, string> UkDict = new Dictionary<string, string>
    {
        ["start.greeting"] =
            "👋 Привіт! Я Кіногід — допоможу знайти фільм, серіал чи телешоу та підберу схоже: " +
            "за еталонами або за описом побажання.\n\nСкористайтесь меню нижче або /help.",

        ["help.text"] =
            "ℹ️ <b>Команди</b>:\n" +
            "/search &lt;назва&gt; — пошук фільмів і серіалів (10 на сторінку)\n" +
            "/similar Назва1; Назва2; Назва3 — схоже за еталонами (1–10)\n" +
            "/text &lt;опис побажання&gt; — добірка за вільним текстом\n" +
            "/watchlist [сторінка] — список «Хочу подивитись»\n" +
            "/watched [сторінка] — список «Переглянуте»\n" +
            "/lang uk|en — мова інтерфейсу\n\n" +
            "У результатах пошуку: ➕ — додати в «Хочу подивитись», ✅ — у «Переглянуте».\n" +
            "Можна просто надіслати назву без команди — виконаю пошук.",

        ["menu.searchBtn"] = "🔍 Пошук",
        ["menu.similarBtn"] = "🎭 Схожі",
        ["menu.textBtn"] = "✍️ Побажання",
        ["menu.listsBtn"] = "📋 Списки",
        ["menu.langBtn"] = "🌐 Мова",
        ["menu.helpBtn"] = "ℹ️ Допомога",

        ["menu.search"] = "🔍 Надішліть команду:\n<code>/search назва</code>\nАбо просто напишіть назву в чат.",
        ["menu.similar"] = "🎭 Надішліть команду:\n<code>/similar Назва1; Назва2; Назва3</code>\n(від 1 до 10 еталонів, розділювач «;»)",
        ["menu.text"] = "✍️ Надішліть команду:\n<code>/text опис побажання</code>\nНаприклад: /text легкий ситком з детективним сюжетом",
        ["menu.lists"] = "📋 Списки:\n<code>/watchlist [сторінка]</code> — «Хочу подивитись»\n<code>/watched [сторінка]</code> — «Переглянуте»",
        ["menu.lang"] = "🌐 Мова: <code>/lang uk</code> або <code>/lang en</code>",

        ["search.usage"] = "🔍 Використання: /search &lt;назва&gt;",
        ["search.header"] = "🔍 Результати за «{0}» — сторінка {1}/{2} · знайдено: {3}",
        ["search.none"] = "🤷 Нічого не знайшов за «{0}». Спробуйте іншу назву.",
        ["search.pageEnd"] = "🤷 Це остання сторінка.",
        ["search.needFirst"] = "Спочатку виконайте /search &lt;назва&gt;.",
        ["search.legend"] = "➕ — у «Хочу подивитись» · ✅ — у «Переглянуте»",

        ["similar.usage"] = "🎭 Використання: /similar Назва1; Назва2; Назва3\n(від 1 до 10 еталонів, розділювач «;»)",
        ["similar.tooMany"] = "🎭 Максимум 10 еталонів (зараз: {0}). Приберіть зайві.",
        ["similar.missing"] = "🤷 Не знайшов такий тайтл, уточніть назву: {0}",
        ["similar.header"] = "🎭 Схожі за еталонами: {0}",

        ["text.usage"] = "✍️ Використання: /text &lt;опис побажання&gt;\nНаприклад: /text легкий ситком з детективним сюжетом",
        ["text.header"] = "✍️ Добірка за описом: «{0}»",

        ["lists.empty"] = "📋 Список порожній. Знайдіть тайтл через /search і натисніть ➕ або ✅.",
        ["lists.watchlist.header"] = "Хочу подивитись — сторінка {0}/{1} · усього: {2}",
        ["lists.watched.header"] = "Переглянуте — сторінка {0}/{1} · усього: {2}",
        ["lists.myRating"] = "моя оцінка {0}/10",

        ["lang.switched"] = "🌐 Мову змінено: українська.",
        ["lang.usage"] = "🌐 Використання: /lang uk|en.",
        ["lang.current"] = "Поточна мова: українська.",

        ["cmd.unknown"] = "🤷 Невідома команда. Спробуйте /help.",

        ["err.notfound"] = "🤷 Не знайшов такий тайтл, уточніть назву.",
        ["err.semantic"] = "🚫 Семантичний індекс недоступний. Спробуйте пізніше або скористайтесь /search чи /similar.",
        ["err.api"] = "😵 Сервіс недоступний. Спробуйте пізніше.",
        ["err.timeout"] = "⏳ Сервер не відповів вчасно. Спробуйте ще раз.",
        ["err.generic"] = "Щось пішло не так. Спробуйте ще раз.",

        ["add.done.watchlist"] = "➕ Додано в «Хочу подивитись»",
        ["add.done.watched"] = "✅ Додано в «Переглянуте»",
        ["add.already"] = "ℹ️ Вже у цьому списку.",
        ["add.fail"] = "😕 Не вдалося додати. Спробуйте ще раз.",

        ["btn.prev"] = "◀️ Назад",
        ["btn.next"] = "Далі ▶️",
        ["btn.addWatchlistN"] = "➕ {0}",
        ["btn.addWatchedN"] = "✅ {0}",

        ["match.score"] = "збіг {0}%",
        ["title.seasons"] = "сезонів: {0}",
    };

    private static readonly IReadOnlyDictionary<string, string> EnDict = new Dictionary<string, string>
    {
        ["start.greeting"] =
            "👋 Hi! I'm CineGuide — I'll help you find movies, series or shows and suggest similar ones: " +
            "by reference titles or by a free-text wish.\n\nUse the menu below or /help.",

        ["help.text"] =
            "ℹ️ <b>Commands</b>:\n" +
            "/search &lt;title&gt; — search movies &amp; series (10 per page)\n" +
            "/similar Title1; Title2; Title3 — similar by 1–10 reference titles\n" +
            "/text &lt;wish description&gt; — picks by free text\n" +
            "/watchlist [page] — Watchlist\n" +
            "/watched [page] — Watched\n" +
            "/lang uk|en — interface language\n\n" +
            "In search results: ➕ — add to Watchlist, ✅ — to Watched.\n" +
            "You can just send a title without a command — I'll search it.",

        ["menu.searchBtn"] = "🔍 Search",
        ["menu.similarBtn"] = "🎭 Similar",
        ["menu.textBtn"] = "✍️ Wish",
        ["menu.listsBtn"] = "📋 Lists",
        ["menu.langBtn"] = "🌐 Language",
        ["menu.helpBtn"] = "ℹ️ Help",

        ["menu.search"] = "🔍 Send:\n<code>/search title</code>\nOr just type a title in the chat.",
        ["menu.similar"] = "🎭 Send:\n<code>/similar Title1; Title2; Title3</code>\n(1–10 reference titles, separator «;»)",
        ["menu.text"] = "✍️ Send:\n<code>/text wish description</code>\nExample: /text light sitcom with detective plot",
        ["menu.lists"] = "📋 Lists:\n<code>/watchlist [page]</code> — Watchlist\n<code>/watched [page]</code> — Watched",
        ["menu.lang"] = "🌐 Language: <code>/lang uk</code> or <code>/lang en</code>",

        ["search.usage"] = "🔍 Usage: /search &lt;title&gt;",
        ["search.header"] = "🔍 Results for «{0}» — page {1}/{2} · total: {3}",
        ["search.none"] = "🤷 Nothing found for «{0}». Try another title.",
        ["search.pageEnd"] = "🤷 This is the last page.",
        ["search.needFirst"] = "Run /search &lt;title&gt; first.",
        ["search.legend"] = "➕ — add to Watchlist · ✅ — add to Watched",

        ["similar.usage"] = "🎭 Usage: /similar Title1; Title2; Title3\n(1–10 reference titles, separator «;»)",
        ["similar.tooMany"] = "🎭 Max 10 reference titles (got {0}). Remove the extras.",
        ["similar.missing"] = "🤷 Can't find that title, please refine the name: {0}",
        ["similar.header"] = "🎭 Similar to your reference titles: {0}",

        ["text.usage"] = "✍️ Usage: /text &lt;wish description&gt;\nExample: /text light sitcom with detective plot",
        ["text.header"] = "✍️ Picks for your description: «{0}»",

        ["lists.empty"] = "📋 The list is empty. Find a title via /search and tap ➕ or ✅.",
        ["lists.watchlist.header"] = "Watchlist — page {0}/{1} · total: {2}",
        ["lists.watched.header"] = "Watched — page {0}/{1} · total: {2}",
        ["lists.myRating"] = "my rating {0}/10",

        ["lang.switched"] = "🌐 Language switched: English.",
        ["lang.usage"] = "🌐 Usage: /lang uk|en.",
        ["lang.current"] = "Current language: English.",

        ["cmd.unknown"] = "🤷 Unknown command. Try /help.",

        ["err.notfound"] = "🤷 Can't find that title, please refine the name.",
        ["err.semantic"] = "🚫 Semantic index unavailable. Try again later or use /search or /similar.",
        ["err.api"] = "😵 Service unavailable. Try again later.",
        ["err.timeout"] = "⏳ The server didn't respond in time. Please try again.",
        ["err.generic"] = "Something went wrong. Please try again.",

        ["add.done.watchlist"] = "➕ Added to Watchlist",
        ["add.done.watched"] = "✅ Added to Watched",
        ["add.already"] = "ℹ️ Already in this list.",
        ["add.fail"] = "😕 Couldn't add. Please try again.",

        ["btn.prev"] = "◀️ Prev",
        ["btn.next"] = "Next ▶️",
        ["btn.addWatchlistN"] = "➕ {0}",
        ["btn.addWatchedN"] = "✅ {0}",

        ["match.score"] = "match {0}%",
        ["title.seasons"] = "seasons: {0}",
    };

    public static IReadOnlyDictionary<string, string> For(string? lang) => lang == En ? EnDict : UkDict;

    /// <summary>Текст за ключем: мова чата → fallback uk → сам ключ.</summary>
    public static string S(string? lang, string key)
        => For(lang).TryGetValue(key, out var value) ? value
            : UkDict.TryGetValue(key, out var uk) ? uk
            : key;

    /// <summary>Форматований текст за ключем (аргументи — invariant).</summary>
    public static string F(string? lang, string key, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, S(lang, key), args);
}
