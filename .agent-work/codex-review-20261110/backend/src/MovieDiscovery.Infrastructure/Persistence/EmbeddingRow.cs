using Pgvector;

namespace MovieDiscovery.Infrastructure.Persistence;

/// <summary>Вектор тайтла (pgvector vector(1024)) — таблиця embeddings.</summary>
public class EmbeddingRow
{
    public int TitleId { get; set; }
    public Core.Domain.Title Title { get; set; } = null!;
    public string Model { get; set; } = "";
    public int Dim { get; set; } = 1024;
    public Vector Vec { get; set; } = null!;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
