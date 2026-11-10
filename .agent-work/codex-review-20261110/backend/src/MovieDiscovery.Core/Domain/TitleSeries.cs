namespace MovieDiscovery.Core.Domain;

/// <summary>
/// Франшиза/сага (Wikidata P179 «part of the series»): imdb_id → серія (QID + назви
/// uk/ru/en) і prev/next частини за P155/P156. PK — imdb_id: один рядок на тайтл;
/// групування — за SeriesQid. Синк title-series перезаписує колонки серії повністю
/// (дані Wikidata — авторитетні).
/// </summary>
public class TitleSeries
{
    public string ImdbId { get; set; } = "";
    public string SeriesQid { get; set; } = "";
    public string? SeriesLabelUk { get; set; }
    public string? SeriesLabelRu { get; set; }
    public string? SeriesLabelEn { get; set; }
    public string? PrevImdb { get; set; }
    public string? NextImdb { get; set; }
    public DateTime UpdatedAt { get; set; }
}
