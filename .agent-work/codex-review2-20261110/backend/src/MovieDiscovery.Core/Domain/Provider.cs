namespace MovieDiscovery.Core.Domain;

/// <summary>Провайдер перегляду (стрімінг/прокат/покупка) — таблиця providers.</summary>
public class Provider
{
    public int Id { get; set; }
    public int TitleId { get; set; }
    public Title Title { get; set; } = null!;
    public string ProviderName { get; set; } = ""; // колонка provider
    public string Kind { get; set; } = "stream"; // stream | rent | buy | free
    public string? Url { get; set; }
    public string Source { get; set; } = "justwatch";
    public string Region { get; set; } = "UA";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // UX-04 (хвиля 2): мови озвучки/субтитрів конкретного оффера — csv ISO-639-1
    // («en,ru»), нормалізовані й відсортовані; NULL = джерело мов НЕ заявило
    // (невідома мова, ніколи не uk/ru за замовчуванням). Сигнатура входить
    // до UNIQUE(title_id, provider, kind, source, region, COALESCE(audio_langs,'')).
    public string? AudioLangs { get; set; } // audio_langs
    public string? SubLangs { get; set; }   // sub_langs

    // UX-04: чесна ціна платного оффера, як її віддає джерело («3,99₴» + код валюти UAH);
    // NULL = ціна в даних відсутня — фронт нічого не вигадує.
    public string? Price { get; set; }    // retail_price (локалізований рядок джерела)
    public string? Currency { get; set; } // currency (ISO-4217, якщо джерело дало)
}
