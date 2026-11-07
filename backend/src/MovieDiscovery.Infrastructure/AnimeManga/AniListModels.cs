using System.Text.Json.Serialization;

namespace MovieDiscovery.Infrastructure.AnimeManga;

/// <summary>Загальна оболонка GraphQL-відповіді AniList (data + errors).</summary>
public sealed class AniListResponse<T>
{
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("errors")] public List<AniListError>? Errors { get; set; }
}

public sealed class AniListError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class AniListUserData
{
    [JsonPropertyName("User")] public AniListUser? User { get; set; }
}

public sealed class AniListUser
{
    [JsonPropertyName("id")] public int Id { get; set; }
}

public sealed class AniListCollectionData
{
    [JsonPropertyName("MediaListCollection")] public AniListCollection? MediaListCollection { get; set; }
}

public sealed class AniListCollection
{
    [JsonPropertyName("lists")] public List<AniListList> Lists { get; set; } = [];
}

public sealed class AniListList
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("entries")] public List<AniListEntry> Entries { get; set; } = [];
}

public sealed class AniListEntry
{
    [JsonPropertyName("status")] public string? Status { get; set; } // CURRENT | PLANNING | COMPLETED | DROPPED | PAUSED | REPEATING
    [JsonPropertyName("score")] public double Score { get; set; }    // POINT_10: 0–10 (користувачі POINT_100 можуть віддати 0–100)
    [JsonPropertyName("media")] public AniListMedia? Media { get; set; }
}

public sealed class AniListMedia
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("title")] public AniListTitle? Title { get; set; }
    [JsonPropertyName("coverImage")] public AniListCover? CoverImage { get; set; }
    [JsonPropertyName("episodes")] public int? Episodes { get; set; }
    [JsonPropertyName("chapters")] public int? Chapters { get; set; }
    [JsonPropertyName("volumes")] public int? Volumes { get; set; }
    [JsonPropertyName("format")] public string? Format { get; set; } // TV | MOVIE | OVA | ONA | SPECIAL | MANGA | NOVEL | ONE_SHOT
    [JsonPropertyName("genres")] public List<string>? Genres { get; set; }
    [JsonPropertyName("studios")] public AniListStudios? Studios { get; set; }
    [JsonPropertyName("startDate")] public AniListDate? StartDate { get; set; }
    [JsonPropertyName("averageScore")] public int? AverageScore { get; set; } // 0–100 → зберігаємо /10
    [JsonPropertyName("popularity")] public int? Popularity { get; set; }
}

public sealed class AniListTitle
{
    [JsonPropertyName("romaji")] public string? Romaji { get; set; }
    [JsonPropertyName("english")] public string? English { get; set; }
    [JsonPropertyName("ukrainian")] public string? Ukrainian { get; set; } // ЄДИНИЙ справжній uk-джерело назви
}

public sealed class AniListCover
{
    [JsonPropertyName("large")] public string? Large { get; set; }
}

public sealed class AniListStudios
{
    [JsonPropertyName("nodes")] public List<AniListNamed> Nodes { get; set; } = [];
}

public sealed class AniListNamed
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class AniListDate
{
    [JsonPropertyName("year")] public int? Year { get; set; }
}
