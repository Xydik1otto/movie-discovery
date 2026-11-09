namespace MovieDiscovery.Bot.Api;

/// <summary>Карточка тайтла — форма TitleCard з PLAN.md (camelCase JSON).</summary>
public sealed class TitleCard
{
    public int Id { get; set; }
    public int? TmdbId { get; set; } // null для більшості рядків каталогу IMDb/аніме/манґи
    public string Type { get; set; } = "movie"; // movie | series | show
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public List<string>? Genres { get; set; }
    public List<string>? Countries { get; set; }
    public double? RatingTmdb { get; set; }
    public double? RatingImdb { get; set; }
    public string? PosterUrl { get; set; }
    public int? RuntimeMin { get; set; }
    public int? Seasons { get; set; }
    public string? Overview { get; set; }

    // Розширення у відповідях /api/similar/* : score + reasons (локалізовані)
    public double? Score { get; set; }
    public List<string>? Reasons { get; set; }

    // Розширення у відповіді /api/lists : оцінка користувача (1-10) і час додавання
    public int? Rating { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>Відповідь /api/search: items + total + page + pageSize.</summary>
public sealed class SearchResponse
{
    public List<TitleCard> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>Відповідь /api/similar/by-seeds, /api/similar/by-text та /api/lists: { "items": [...] }.</summary>
public sealed class ItemsResponse
{
    public List<TitleCard> Items { get; set; } = [];
}

/// <summary>
/// Опційні фільтри пошуку (MD-06, паритет з web FR-8): усі поля необов'язкові,
/// null/порожні поля у query не потрапляють. Жанри — канонічні англійські назви
/// (lowercase); бекенд сам нормалізує значення (TmdbGenres.NormalizeToEn).
/// </summary>
public sealed record SearchFilters
{
    public string? Type { get; init; } // movie | series | anime | manga
    public IReadOnlyList<string>? Genres { get; init; }
    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }
    public int? MinRating { get; init; }

    public bool IsEmpty => Type is null
        && (Genres is null || Genres.Count == 0)
        && YearFrom is null && YearTo is null && MinRating is null;
}
