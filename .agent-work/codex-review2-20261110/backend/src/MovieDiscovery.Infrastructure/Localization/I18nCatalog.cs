namespace MovieDiscovery.Infrastructure.Localization;

/// <summary>
/// Каталог i18n (uk/en) — копія backend-дефолту за PLAN §i18n. Ключі збіжні з фронтовими.
/// Російської мови нема — свідоме рішення власника.
/// </summary>
public static class I18nCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Uk = new Dictionary<string, string>
    {
        ["nav.home"] = "Головна",
        ["nav.similar"] = "Схожі",
        ["nav.lists"] = "Списки",
        ["nav.settings"] = "Налаштування",
        ["search.placeholder"] = "Знайти фільм, серіал або шоу…",
        ["search.button"] = "Знайти",
        ["filters.type"] = "Тип",
        ["filters.genre"] = "Жанр",
        ["filters.year"] = "Рік",
        ["filters.country"] = "Країна",
        ["filters.rating"] = "Рейтинг",
        ["filters.runtime"] = "Тривалість",
        ["filters.apply"] = "Застосувати",
        ["filters.reset"] = "Скинути",
        ["title.similar"] = "Схожі тайтли",
        ["title.why"] = "Чому схоже?",
        ["title.watchlist"] = "Хочу подивитись",
        ["title.watched"] = "Переглянуто",
        ["title.watchedAdd"] = "Позначити переглянутим",
        ["title.providers"] = "Де подивитись",
        ["title.trailer"] = "Трейлер",
        ["similar.seedsTitle"] = "Еталони добірки",
        ["similar.seedsHint"] = "Додайте 1–10 фільмів або серіалів",
        ["similar.byText"] = "Пошук за описом",
        ["similar.byTextPlaceholder"] = "Опишіть, що хочете подивитись…",
        ["lists.watchlist"] = "Хочу подивитись",
        ["lists.watched"] = "Переглянуте",
        ["lists.empty"] = "Список порожній",
        ["common.loading"] = "Завантаження…",
        ["common.error"] = "Сталася помилка",
        ["api.unavailable"] = "Сервіс недоступний",
        ["lang.switch"] = "Мова",
    };

    public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        ["nav.home"] = "Home",
        ["nav.similar"] = "Similar",
        ["nav.lists"] = "Lists",
        ["nav.settings"] = "Settings",
        ["search.placeholder"] = "Find a movie, series or show…",
        ["search.button"] = "Search",
        ["filters.type"] = "Type",
        ["filters.genre"] = "Genre",
        ["filters.year"] = "Year",
        ["filters.country"] = "Country",
        ["filters.rating"] = "Rating",
        ["filters.runtime"] = "Runtime",
        ["filters.apply"] = "Apply",
        ["filters.reset"] = "Reset",
        ["title.similar"] = "Similar titles",
        ["title.why"] = "Why similar?",
        ["title.watchlist"] = "Watchlist",
        ["title.watched"] = "Watched",
        ["title.watchedAdd"] = "Mark as watched",
        ["title.providers"] = "Where to watch",
        ["title.trailer"] = "Trailer",
        ["similar.seedsTitle"] = "Reference picks",
        ["similar.seedsHint"] = "Add 1–10 movies or series",
        ["similar.byText"] = "Search by description",
        ["similar.byTextPlaceholder"] = "Describe what you want to watch…",
        ["lists.watchlist"] = "Watchlist",
        ["lists.watched"] = "Watched",
        ["lists.empty"] = "List is empty",
        ["common.loading"] = "Loading…",
        ["common.error"] = "Something went wrong",
        ["api.unavailable"] = "Service unavailable",
        ["lang.switch"] = "Language",
    };

    /// <summary>Каталог для uk/en; невідома мова → null (російської нема свідомо).</summary>
    public static IReadOnlyDictionary<string, string>? For(string? lang) =>
        string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase) ? En
        : string.Equals(lang, "uk", StringComparison.OrdinalIgnoreCase) ? Uk
        : null;
}
