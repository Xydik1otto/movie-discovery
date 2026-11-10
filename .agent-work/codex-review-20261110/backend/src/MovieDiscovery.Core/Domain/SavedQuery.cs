using System.Text.Json;

namespace MovieDiscovery.Core.Domain;

/// <summary>Збережений multi-seed запит — таблиця queries.</summary>
public class SavedQuery
{
    public int Id { get; set; }
    public int? ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Масив int tmdb_id, збережений у jsonb.</summary>
    public JsonDocument Seeds { get; set; } = null!;
    public JsonDocument? Filters { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
