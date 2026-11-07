using System.Collections.Concurrent;

namespace MovieDiscovery.Bot;

/// <summary>Останній пошуковий запит чата (для пагінації кнопками).</summary>
public sealed record SearchState(string Query, int Page, int Total, int PageSize)
{
    public int TotalPages => PageSize > 0 ? Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize)) : 1;
}

/// <summary>
/// In-memory стан чатів ( словник chatId → мова / останній пошук ).
/// ВАЖЛИВО (Е0/Е1, свідоме спрощення за ТЗ): після рестарту контейнера мова чату
/// скидається на uk, а останній пошуковий запит втрачається. Постійне сховище — пізніше.
/// </summary>
public sealed class ChatState
{
    private readonly ConcurrentDictionary<long, string> _langByChat = new();
    private readonly ConcurrentDictionary<long, SearchState> _searchByChat = new();

    public string GetLang(long chatId) => _langByChat.TryGetValue(chatId, out var lang) ? lang : Strings.Uk;

    public void SetLang(long chatId, string lang) => _langByChat[chatId] = lang;

    public SearchState? GetSearch(long chatId) => _searchByChat.TryGetValue(chatId, out var s) ? s : null;

    public void SetSearch(long chatId, SearchState state) => _searchByChat[chatId] = state;
}
