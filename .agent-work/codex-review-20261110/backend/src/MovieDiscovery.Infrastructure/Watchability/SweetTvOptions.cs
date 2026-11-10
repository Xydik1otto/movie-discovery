using Microsoft.Extensions.Options;
using MovieDiscovery.Infrastructure.AnimeManga;

namespace MovieDiscovery.Infrastructure.Watchability;

/// <summary>Опції sweet.tv-парсера (розділ SweetTv конфігу): чесний User-Agent і базовий URL.</summary>
public sealed class SweetTvOptions
{
    public string BaseUrl { get; set; } = "https://sweet.tv";

    /// <summary>Чесний UA бота (NFR-6); robots.txt sweet.tv: /search і /payment НЕ парсаться.</summary>
    public string UserAgent { get; set; } = "KinoNavigatorBot/1.0";

    /// <summary>Запобіжник: максимум карток за один прогін.</summary>
    public int MaxCards { get; set; } = 1000;
}

/// <summary>sweet.tv: 1 req / 2 c (NFR-6); тестам дозволено override (напр. TimeSpan.Zero).</summary>
public sealed class SweetTvThrottle(TimeSpan? minInterval = null) : IntervalThrottle(minInterval ?? TimeSpan.FromSeconds(2));
