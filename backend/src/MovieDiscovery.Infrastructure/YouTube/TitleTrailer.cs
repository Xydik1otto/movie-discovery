namespace MovieDiscovery.Infrastructure.YouTube;

/// <summary>
/// Кандидат трейлера тайтла (UX-05) — таблиця title_trailers.
/// На відміну від titles.trailer_yt_id (швидкий шлях, один безмовний id),
/// тут зберігаються ВСІ перевірені кандидати з мовою, офіційністю, джерелом
/// і статусом верифікації. UNIQUE(title_id, yt_id) — повторний sync не дублює.
/// </summary>
public class TitleTrailer
{
    public int Id { get; set; }
    public int TitleId { get; set; }
    public Core.Domain.Title Title { get; set; } = null!;

    /// <summary>YouTube video id (11 символів).</summary>
    public string YtId { get; set; } = "";

    /// <summary>
    /// Мова трейлера: uk | ru | en | de | fr | es | ja | ko | zh | multi.
    /// NULL = невідома (чесний unknown — не вигадуємо і не автопідміняємо).
    /// </summary>
    public string? Lang { get; set; }

    /// <summary>Офіційність: канальний allowlist (студії/дистриб'ютори) або «official trailer»-фраза.</summary>
    public bool Official { get; set; }

    /// <summary>Джерело рядка: youtube (search-синк) | titles (швидкий шлях titles.trailer_yt_id).</summary>
    public string Source { get; set; } = "youtube";

    /// <summary>Момент останньої верифікації (oEmbed + videos.list).</summary>
    public DateTime? VerifiedAt { get; set; }

    /// <summary>
    /// Статус верифікації: ok | gone | nonembeddable | private. NULL — ще не верифіковано.
    /// не-ok кандидати не показуються як програвані (крім свідомого fast-path винятку).
    /// </summary>
    public string? VerifiedStatus { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
