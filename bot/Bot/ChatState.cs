using MovieDiscovery.Bot.Api;
using System.Collections.Concurrent;

namespace MovieDiscovery.Bot;

/// <summary>Останній пошуковий запит чата (для пагінації кнопками). Filters — фільтри MD-06 (якщо пошук ішов через /find).</summary>
public sealed record SearchState(string Query, int Page, int Total, int PageSize, FilterDraft? Filters = null)
{
    public int TotalPages => PageSize > 0 ? Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize)) : 1;
}

/// <summary>
/// Чернетка фільтрів пошуку (MD-06, /find): усі поля опційні; Genres — канонічні
/// англійські назви lowercase (бекенд нормалізує сам). Іммутабельний — кожна зміна
/// будує новий екземпляр через with.
/// </summary>
public sealed record FilterDraft(string Query)
{
    public string? Type { get; init; } // movie | series | anime | manga
    public IReadOnlyList<string> Genres { get; init; } = [];
    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }
    public int? MinRating { get; init; }

    /// <summary>Перемкнути жанр: був у списку — прибрати, не було — додати.</summary>
    public FilterDraft ToggleGenre(string genre)
        => Genres.Contains(genre)
            ? this with { Genres = Genres.Where(g => g != genre).ToList() }
            : this with { Genres = [.. Genres, genre] };

    public bool HasSelection => Type is not null || Genres.Count > 0
        || YearFrom is not null || YearTo is not null || MinRating is not null;

    public SearchFilters ToApiFilters() => new()
    {
        Type = Type,
        Genres = Genres.Count > 0 ? Genres : null,
        YearFrom = YearFrom,
        YearTo = YearTo,
        MinRating = MinRating,
    };
}

/// <summary>
/// In-memory стан чатів ( словник chatId → мова / останній пошук / чернетка фільтрів ).
/// ВАЖЛИВО (Е0/Е1, свідоме спрощення за ТЗ): після рестарту контейнера мова чату
/// скидається на uk, а останній пошуковий запит і фільтри втрачаються. Постійне сховище — пізніше.
/// </summary>
public sealed class ChatState
{
    private readonly ConcurrentDictionary<long, string> _langByChat = new();
    private readonly ConcurrentDictionary<long, SearchState> _searchByChat = new();
    private readonly ConcurrentDictionary<long, FilterDraft> _filterByChat = new();

    public string GetLang(long chatId) => _langByChat.TryGetValue(chatId, out var lang) ? lang : Strings.Uk;

    public void SetLang(long chatId, string lang) => _langByChat[chatId] = lang;

    public SearchState? GetSearch(long chatId) => _searchByChat.TryGetValue(chatId, out var s) ? s : null;

    public void SetSearch(long chatId, SearchState state) => _searchByChat[chatId] = state;

    public FilterDraft? GetFilter(long chatId) => _filterByChat.TryGetValue(chatId, out var f) ? f : null;

    public void SetFilter(long chatId, FilterDraft draft) => _filterByChat[chatId] = draft;

    public void ClearFilter(long chatId) => _filterByChat.TryRemove(chatId, out _);
}
