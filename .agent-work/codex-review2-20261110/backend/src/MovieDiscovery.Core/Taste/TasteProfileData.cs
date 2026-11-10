using System.Text.Json;

namespace MovieDiscovery.Core.Taste;

/// <summary>Розібраний профіль смаку (без I/O) — вхід DiscoverScorer.</summary>
public sealed class TasteProfileData
{
    public Dictionary<string, double> Genres { get; init; } = [];
    public Dictionary<string, double> Keywords { get; init; } = [];
    public Dictionary<string, double> Era { get; init; } = [];
    public Dictionary<string, double> Actors { get; init; } = [];
    public Dictionary<string, double> NegativeGenres { get; init; } = [];
    /// <summary>Жанри-цікавості з онбордингу (quiz picks + mood) — для explore-пулу discover.</summary>
    public List<string> QuizGenres { get; init; } = [];

    /// <summary>Профіль без смакових сигналів (до онбордингу/дій) — discover працює на popularity+safe.</summary>
    public bool IsEmpty =>
        Genres.Count == 0 && Keywords.Count == 0 && Era.Count == 0 && Actors.Count == 0;
}

/// <summary>Серіалізація мап «ключ → вага» ↔ jsonb (рядок JSON).</summary>
public static class TasteJson
{
    public static string Serialize(IReadOnlyDictionary<string, double> map) =>
        JsonSerializer.Serialize(map);

    public static Dictionary<string, double> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
