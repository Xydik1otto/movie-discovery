using System.Text.Json;
using MovieDiscovery.Core.Similarity;

namespace MovieDiscovery.Api.Contracts;

/// <summary>Форма TitleCard (PLAN.md) — використовується всюди.</summary>
public class TitleCard
{
    public int Id { get; set; }
    public int? TmdbId { get; set; } // null для аніме/манги з зовнішніх джерел
    public string Type { get; set; } = "movie";
    public string Title { get; set; } = "";
    public string? OriginalTitle { get; set; }
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Countries { get; set; } = [];
    public double? RatingTmdb { get; set; }
    public double? RatingImdb { get; set; }
    public string? PosterUrl { get; set; }
    public int? RuntimeMin { get; set; }
    public int? Seasons { get; set; }
    public string? Overview { get; set; }
    public string? TrailerYtId { get; set; } // офіційний YouTube-трейлер (FR-20 sync); null → не знайдено

    // ── Аніме/манга-специфіка (null для звичайних тайтлів; FR-12) ──
    public string? Format { get; set; }
    public int? Episodes { get; set; }
    public int? Chapters { get; set; }
    public int? Volumes { get; set; }
    public List<string>? Studios { get; set; }
    public List<string>? Authors { get; set; }
    public double? ScoreMal { get; set; }
    public double? ScoreAnilist { get; set; }
    public double? ScoreShikimori { get; set; }

    // ── «Де подивитись» (FR-14): компактні бейджі, лише is_available=true ──
    public List<CardWatchProvider> WatchProviders { get; set; } = [];

    // ── Трейлери (UX-05): заповнюється ЛИШЕ в деталях тайтла; на каталожних картках null ──
    public List<TrailerInfo>? Trailers { get; set; }
}

/// <summary>Елемент відповіді схожості: TitleCard + score + reasons (1–3 локалізовані рядки).</summary>
public sealed class SimilarItem : TitleCard
{
    public double Score { get; set; }
    public List<string> Reasons { get; set; } = [];
}

public sealed record ProviderInfo(string Provider, string Kind, string? Url, string Source);

/// <summary>
/// Кандидат трейлера (UX-05): ytId, lang (uk|ru|en|…|NULL=unknown), official, source,
/// verifiedAt (ISO) і verifiedStatus (ok|gone|nonembeddable|private|NULL).
/// Деградовані статуси віддаються лише для швидкого шляху titles.trailer_yt_id —
/// фронт показує їх як повідомлення/безпечне зовнішнє посилання, НЕ як iframe.
/// </summary>
public sealed record TrailerInfo(
    string YtId,
    string? Lang,
    bool Official,
    string Source,
    DateTime? VerifiedAt,
    string? VerifiedStatus = null);

/// <summary>Картка тайтла з повними деталями.</summary>
public sealed class TitleDetailResponse : TitleCard
{
    public List<string> Actors { get; set; } = [];
    public string? Director { get; set; }
    public List<string> Keywords { get; set; } = [];
    public string? OverviewFull { get; set; }
    public string? BackdropUrl { get; set; }
    // TrailerYtId успадковується з TitleCard — FR-20+: тепер на всіх картках, не лише в деталях
    public List<ProviderInfo> Providers { get; set; } = [];
    public string? Status { get; set; }
    public string? OriginalLanguage { get; set; }
}

/// <summary>
/// Мапінг Title → TitleCard з локалізацією uk/ru/en (рішення власника 10.11: ru —
/// повноцінна мова відповідей; uk-режим → uk ?? ru ?? en, ru-режим → ru ?? uk ?? en)
/// і жанрів (en→uk — ru-назв жанрів нема, ru-режим лишає uk-жанри).
/// </summary>
public static class TitleMapper
{
    public static TitleCard ToCard(Core.Domain.Title t, string lang)
    {
        // Канонізація мови одне джерело правди — SearchController.Lang (uk|ru|en, кейс-незалежно).
        var mode = Controllers.SearchController.Lang(lang);
        var isAnimeOrManga = t.Type is "anime" or "manga";
        return new TitleCard
        {
            Id = t.Id,
            TmdbId = t.TmdbId,
            Type = t.Type,
            // Рішення власника 09.11: українська назва → російський фолбек (якщо
            // офіційної uk немає) → оригінальна en. Назви — з Wikidata/IMDb akas.
            // 10.11: окремий ru-режим — title_ru з фолбеком ru → uk → en.
            Title = mode switch
            {
                "ru" => FirstNonEmpty(t.TitleRu) ?? FirstNonEmpty(t.TitleUk) ?? t.TitleEn,
                "en" => t.TitleEn,
                _ => FirstNonEmpty(t.TitleUk) ?? FirstNonEmpty(t.TitleRu) ?? t.TitleEn,
            },
            OriginalTitle = t.TitleEn,
            Year = t.Year,
            Genres = mode == "en" ? [.. t.Genres] : t.Genres.Select(Infrastructure.Tmdb.TmdbGenres.ToUk).ToList(),
            Countries = [.. t.Countries],
            RatingTmdb = t.RatingTmdb,
            RatingImdb = t.RatingImdb,
            PosterUrl = ImgUrl(t.PosterPath, "w500"),
            RuntimeMin = t.RuntimeMin,
            Seasons = t.Seasons,
            // Описи: uk-режим uk → ru → en; ru-режим ru → uk → en (рішення власника 09.11/10.11;
            // ru — Wikidata schema:description)
            Overview = mode switch
            {
                "ru" => FirstNonEmpty(t.OverviewRu) ?? FirstNonEmpty(t.OverviewUk) ?? t.OverviewEn,
                "en" => t.OverviewEn,
                _ => FirstNonEmpty(t.OverviewUk) ?? FirstNonEmpty(t.OverviewRu) ?? t.OverviewEn,
            },
            TrailerYtId = t.TrailerYtId,
            Format = isAnimeOrManga ? t.Format : null,
            Episodes = isAnimeOrManga ? t.Episodes : null,
            Chapters = isAnimeOrManga ? t.Chapters : null,
            Volumes = isAnimeOrManga ? t.Volumes : null,
            Studios = isAnimeOrManga && t.Studios is { Length: > 0 } ? [.. t.Studios] : null,
            Authors = isAnimeOrManga && t.Authors is { Length: > 0 } ? [.. t.Authors] : null,
            ScoreMal = isAnimeOrManga ? t.ScoreMal : null,
            ScoreAnilist = isAnimeOrManga ? t.ScoreAnilist : null,
            ScoreShikimori = isAnimeOrManga ? t.ScoreShikimori : null,
        };
    }

    /// <summary>Шлях постера TMDB → URL; абсолютні URL (Jikan/AniList/Shikimori CDN) — як є.</summary>
    public static string? ImgUrl(string? path, string size) =>
        string.IsNullOrWhiteSpace(path) ? null
        : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path
        : $"https://image.tmdb.org/t/p/{size}{path}";

    private static string? FirstNonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
