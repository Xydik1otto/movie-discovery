namespace MovieDiscovery.Core.Domain;

/// <summary>
/// Повна історія взаємодій юзера («пам'ять про користувача», FR-17) — таблиця interaction_log.
/// Профіль агрегується з цього журналу, тому жоден сигнал не втрачається.
/// </summary>
public class InteractionLog
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int TitleId { get; set; }
    public Title Title { get; set; } = null!;
    public string Action { get; set; } = ""; // like | dislike | rate | watchlist | watched | skip | open
    public int? Value { get; set; } // оцінка (rate/watched); множник ваги (like з онбордингу = 2 лайки)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
