using System.Text.Json;

namespace MovieDiscovery.Core.Domain;

/// <summary>Фонова джоба імпорту списків трекера (FR-13) — таблиця import_jobs.</summary>
public class ImportJob
{
    public int Id { get; set; }
    public string Source { get; set; } = ""; // mal | anilist | shikimori
    public string Username { get; set; } = "";
    public int? ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public string Status { get; set; } = "queued"; // queued | running | done | failed
    public JsonDocument? Stats { get; set; }       // {animeTotal, mangaTotal, matched, created, ...}
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}
