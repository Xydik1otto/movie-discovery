namespace MovieDiscovery.Core.Domain;

/// <summary>
/// Профіль смаку юзера (FR-17) — таблиця user_profiles. jsonb-поля — мапи «ключ → вага»
/// (жанр / keyword / декада / актор), серіалізовані рядком JSON; negative_genres — жанр → cooldown-вага.
/// ProfileId — постійний профіль списків юзера (зв'язка зі списками, FR-16).
/// </summary>
public class UserProfile
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int ProfileId { get; set; }
    public Profile Profile { get; set; } = null!;

    public string? TasteGenres { get; set; }    // жанр → вага
    public string? TasteKeywords { get; set; }  // keyword → вага
    public string? TasteEra { get; set; }       // декада ("1990s") → вага
    public string? TasteActors { get; set; }    // актор → вага
    public string? NegativeGenres { get; set; } // жанр → cooldown-вага (дизлайки)

    public bool OnboardingDone { get; set; }
    public string? Quiz { get; set; }           // відповіді онбордингу (jsonb)
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
