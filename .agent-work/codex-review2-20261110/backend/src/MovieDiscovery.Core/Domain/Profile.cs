namespace MovieDiscovery.Core.Domain;

/// <summary>Профіль користувача — таблиця profiles.</summary>
public class Profile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ListEntry> Lists { get; set; } = [];
}
