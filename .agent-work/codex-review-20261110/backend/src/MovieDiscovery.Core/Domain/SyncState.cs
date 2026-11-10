namespace MovieDiscovery.Core.Domain;

/// <summary>Стан синхронізації джерела — таблиця sync_state.</summary>
public class SyncState
{
    public int Id { get; set; }
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime? LastRunAt { get; set; }
    public int? LastPages { get; set; }
    public int? LastCount { get; set; }
}
