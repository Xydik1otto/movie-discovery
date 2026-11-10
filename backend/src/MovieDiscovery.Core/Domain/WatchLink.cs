namespace MovieDiscovery.Core.Domain;

/// <summary>
/// Посилання «де подивитись» (FR-14) — таблиця watch_links.
/// Провайдери: sweettv | archive | tmdb-streaming | ...
/// kind: free | subscription | rent | buy; region: UA | US | *.
/// UNIQUE(title_id, provider, kind, region) — повторний sync НЕ дублює.
/// </summary>
public class WatchLink
{
    public int Id { get; set; }
    public int TitleId { get; set; }
    public Title Title { get; set; } = null!;
    public string Provider { get; set; } = "";
    public string Kind { get; set; } = "free";
    public string Region { get; set; } = "UA";
    public string? Url { get; set; }
    public bool IsAvailable { get; set; } = true;
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    // ── Озвучки/якість (FR-19) ──
    public string? AudioLang { get; set; } // ukr|russ|eng|jpn|kor|zho|original|multi (легасі, одиночне)
    public int? Quality { get; set; }      // max height px: 480|720|1080|2160
    public string? Dub { get; set; }       // ukr|russ|eng|original|multi

    // ── UX-04 (хвиля 2): ПОВНИЙ набір мов озвучки/субтитрів сторінки тайтла ──
    // csv ISO-639-1 («en,ru,uk»), нормалізований WatchLanguages; NULL = джерело
    // мов НЕ заявило. Сигнатура входить до UNIQUE(title,provider,kind,region,COALESCE(audio_langs,'')).
    public string? AudioLangs { get; set; } // audio_langs
    public string? SubLangs { get; set; }   // sub_langs
}
