using MovieDiscovery.Core.Domain;

namespace MovieDiscovery.Infrastructure.Imdb;

/// <summary>
/// Ідемпотентне ядро злиття IMDb-патчів у titles за imdb_id (FR-15). Чисті функції (без I/O),
/// покриті тестами: повторний sync оновлює, не дублює; title_uk (sweet.tv/TMDB) і type
/// наявного тайтла НЕ затираються.
/// </summary>
public static class ImdbUpsertCore
{
    /// <summary>Знайти наявний тайтл за imdb_id; не знайдено → сам патч як нова сутність.</summary>
    public static (Title Title, bool Created) Resolve(IReadOnlyList<Title> candidates, ImdbTitlePatch patch)
    {
        var existing = candidates.FirstOrDefault(t => t.ImdbId == patch.ImdbId);
        if (existing is null)
        {
            return (ToEntity(patch), true);
        }
        Merge(existing, patch);
        return (existing, false);
    }

    /// <summary>Патч → нова сутність Title (created_at/updated_at — зараз).</summary>
    public static Title ToEntity(ImdbTitlePatch patch) => new()
    {
        ImdbId = patch.ImdbId,
        Type = patch.Type,
        TitleEn = patch.TitleEn,
        TitleUk = null, // uk-назви — sweet.tv/TMDB, не з IMDb
        Year = patch.Year,
        RuntimeMin = patch.RuntimeMin,
        Genres = patch.Genres,
        RatingImdb = patch.RatingImdb,
        VotesImdb = patch.VotesImdb,
        Popularity = patch.Popularity,
        IsAdult = patch.IsAdult,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// Оновити наявний тайтл патчем: метадані IMDb — авторитетні (назва/рік/тривалість/жанри/
    /// рейтинг/голоси/isAdult). type наявного тайтла і title_uk НЕ змінюються; рейтинг/голоси
    /// оновлюються лише коли є (не null); popularity — коли є голоси. Повторний apply — стабільний.
    /// </summary>
    public static void Merge(Title target, ImdbTitlePatch patch)
    {
        if (!string.IsNullOrWhiteSpace(patch.TitleEn)) target.TitleEn = patch.TitleEn;
        if (patch.Year is > 0) target.Year = patch.Year;
        if (patch.RuntimeMin is > 0) target.RuntimeMin = patch.RuntimeMin;
        if (patch.Genres is { Length: > 0 }) target.Genres = patch.Genres;
        if (patch.RatingImdb is > 0) target.RatingImdb = patch.RatingImdb;
        if (patch.VotesImdb is > 0)
        {
            target.VotesImdb = patch.VotesImdb;
            target.Popularity = patch.Popularity;
        }
        target.IsAdult = target.IsAdult || patch.IsAdult; // is_adult ніколи не знімається заднім числом
        if (target.ImdbId is null) target.ImdbId = patch.ImdbId; // дозаповнення (тайтл без imdb_id не матчиться тут, але безпечно)
        target.UpdatedAt = DateTime.UtcNow;
    }
}
