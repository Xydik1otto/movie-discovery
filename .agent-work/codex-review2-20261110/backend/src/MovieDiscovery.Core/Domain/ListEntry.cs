namespace MovieDiscovery.Core.Domain;

/// <summary>Запис у списку профілю (watchlist / watched) — таблиця lists.</summary>
public class ListEntry
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public Profile Profile { get; set; } = null!;
    public string Kind { get; set; } = "watchlist"; // watchlist | watched
    public int TitleId { get; set; }
    public Title Title { get; set; } = null!;
    public int? Rating { get; set; } // 1..10
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? WatchedAt { get; set; }
}
