using System.Text.Json.Serialization;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>Сторінка відповіді Jikan v4 (pagination + data).</summary>
public sealed class JikanPage<T>
{
    [JsonPropertyName("pagination")] public JikanPagination? Pagination { get; set; }
    [JsonPropertyName("data")] public List<T> Data { get; set; } = [];
}

public sealed class JikanPagination
{
    [JsonPropertyName("last_visible_page")] public int? LastVisiblePage { get; set; }
    [JsonPropertyName("has_next_page")] public bool? HasNextPage { get; set; }
    [JsonPropertyName("items")] public JikanPageItems? Items { get; set; }
}

public sealed class JikanPageItems
{
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("total")] public int? Total { get; set; }
    [JsonPropertyName("per_page")] public int? PerPage { get; set; }
}

/// <summary>
/// Аніме або манга у відповіді Jikan (поля, які використовуємо). Jikan опускає відсутні поля
/// і подає числа як строки — DTO повністю nullable-толерантний.
/// </summary>
public sealed class JikanMedia
{
    [JsonPropertyName("mal_id")] public int MalId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("title_english")] public string? TitleEnglish { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; } // TV | Movie | OVA | ... | Manga | Manhwa | Light Novel
    [JsonPropertyName("episodes")] public int? Episodes { get; set; }
    [JsonPropertyName("chapters")] public int? Chapters { get; set; }
    [JsonPropertyName("volumes")] public int? Volumes { get; set; }
    [JsonPropertyName("score")] public double? Score { get; set; }
    [JsonPropertyName("members")] public int? Members { get; set; }
    [JsonPropertyName("favorites")] public int? Favorites { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; } // Finished Airing | Currently Airing | Publishing ...
    [JsonPropertyName("year")] public int? Year { get; set; }
    [JsonPropertyName("aired")] public JikanPeriod? Aired { get; set; }
    [JsonPropertyName("published")] public JikanPeriod? Published { get; set; }
    [JsonPropertyName("synopsis")] public string? Synopsis { get; set; }
    [JsonPropertyName("genres")] public List<JikanNamed>? Genres { get; set; }
    [JsonPropertyName("studios")] public List<JikanNamed>? Studios { get; set; }
    [JsonPropertyName("authors")] public List<JikanNamed>? Authors { get; set; }
    [JsonPropertyName("images")] public JikanImages? Images { get; set; }
}

public sealed class JikanNamed
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class JikanPeriod
{
    [JsonPropertyName("from")] public DateTime? From { get; set; }
    [JsonPropertyName("to")] public DateTime? To { get; set; }
}

public sealed class JikanImages
{
    [JsonPropertyName("jpg")] public JikanImageSet? Jpg { get; set; }
}

public sealed class JikanImageSet
{
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("large_image_url")] public string? LargeImageUrl { get; set; }
}

/// <summary>Запис списку користувача MAL (animelist/mangalist через Jikan).</summary>
public sealed class JikanUserEntry
{
    [JsonPropertyName("status")] public string? Status { get; set; } // watching | completed | on_hold | dropped | plan_to_watch | reading | plan_to_read
    [JsonPropertyName("score")] public double? Score { get; set; }   // 0–10, 0 = не оцінено
    [JsonPropertyName("anime")] public JikanMedia? Anime { get; set; }
    [JsonPropertyName("manga")] public JikanMedia? Manga { get; set; }
}
