namespace MovieDiscovery.Api.Contracts;

/// <summary>Admin-опції: ключ заголовка X-Admin-Key для /api/admin/*.</summary>
public sealed class AdminOptions
{
    public string Key { get; set; } = "";
}
