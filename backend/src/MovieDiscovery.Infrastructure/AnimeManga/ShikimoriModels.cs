using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>Опції Shikimori API. User-Agent обов'язковий; X-Application-Name — після реєстрації застосунку.</summary>
public sealed class ShikimoriOptions
{
    public string UserAgent { get; set; } = "KinoNavigator/0.1 (dev build)";
    public string? ApplicationName { get; set; }
}

public sealed class ShikimoriUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
}

/// <summary>Запис user rate (список аніме/манги користувача Shikimori).</summary>
public sealed class ShikimoriRate
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("target_id")] public long? TargetId { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }    // 0–10, 0 = не оцінено
    [JsonPropertyName("status")] public string? Status { get; set; }  // watching | completed | rewatching | on_hold | dropped | planned
    [JsonPropertyName("episodes")] public int? Episodes { get; set; }
    [JsonPropertyName("anime")] public ShikimoriMedia? Anime { get; set; }
    [JsonPropertyName("manga")] public ShikimoriMedia? Manga { get; set; }
}

/// <summary>Тайтл Shikimori у відповіді rates. Поле «russian» НІКОЛИ не потрапляє в title_uk.</summary>
public sealed class ShikimoriMedia
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }       // ромадзі/оригінал
    [JsonPropertyName("russian")] public string? Russian { get; set; } // російська назва — ігноруємо повністю
    [JsonPropertyName("image")] public JsonElement? Image { get; set; } // string-шлях або об'єкт (залежить від версії API)
    [JsonPropertyName("kind")] public string? Kind { get; set; }       // tv | movie | ova | ona | special | manga | manhwa | ...
    [JsonPropertyName("score")] public double? Score { get; set; }     // 0–10
    [JsonPropertyName("status")] public string? Status { get; set; }   // released | ongoing | anons
    [JsonPropertyName("episodes")] public int? Episodes { get; set; }
    [JsonPropertyName("chapters")] public int? Chapters { get; set; }
    [JsonPropertyName("volumes")] public int? Volumes { get; set; }
    [JsonPropertyName("aired_on")] public string? AiredOn { get; set; }    // "2009-04-05"
    [JsonPropertyName("released_on")] public string? ReleasedOn { get; set; }
    [JsonPropertyName("season")] public string? Season { get; set; }   // "spring_2009"
    [JsonPropertyName("genres")] public List<ShikimoriGenre>? Genres { get; set; }
    [JsonPropertyName("studios")] public List<ShikimoriNamed>? Studios { get; set; }
}

public sealed class ShikimoriGenre
{
    [JsonPropertyName("name")] public string? Name { get; set; }     // англійська назва — використовуємо її
    [JsonPropertyName("russian")] public string? Russian { get; set; } // ігноруємо
}

public sealed class ShikimoriNamed
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}
