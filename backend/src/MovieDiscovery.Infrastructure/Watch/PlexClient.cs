using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MovieDiscovery.Infrastructure.Watch;

/// <summary>Опції Plex-синку (розділ Plex конфігу). Бюджет $0 — лише публічні сторінки watch.plex.tv.</summary>
public sealed class PlexOptions
{
    public string BaseUrl { get; set; } = "https://watch.plex.tv";

    /// <summary>Чесний UA (robots.txt watch.plex.tv: /movie-database/* і /discover дозволені;
    /// /library/*, /libraries/*, /search/*, /watch/*, /live/*, /embed/* — Disallow, не парсаться).</summary>
    public string UserAgent { get; set; } = "KinoNavigatorBot/1.0 (movie-discovery; contact via repo issues)";

    /// <summary>Пауза між year-сторінками (ввічливість до Plex edge).</summary>
    public int RequestDelayMs { get; set; } = 700;

    /// <summary>Максимум year-сторінок за один прогін (сторінки йдуть від нових років до старих).</summary>
    public int MaxPages { get; set; } = 60;

    /// <summary>Найстаріший рік каталогу (на landing /movie-database роки з 1920).</summary>
    public int YearFrom { get; set; } = 1920;

    /// <summary>Найновіший рік; 0 = авто (поточний рік + 1 — Plex веде майбутні прем'єри).</summary>
    public int YearTo { get; set; } = 0;

    /// <summary>Ретраї транзитних 429/502/503/504 на сторінку (Plex edge буває 503 на окремих роках).</summary>
    public int MaxRetries { get; set; } = 3;

    // ── UX-04 (хвиля 2): перевірка «Where to Watch» сторінки тайтла (tri-state) ──
    /// <summary>Таймаут одного перевірочного title-запиту, мс (on-demand у /watch — не довгий sync-таймаут).</summary>
    public int AvailabilityTimeoutMs { get; set; } = 10_000;

    /// <summary>TTL кешу вердикту доступності (хвилини): без кешу кожен /watch бив би по Plex; 12 год — свіжість/навантаження.</summary>
    public int AvailabilityCacheMinutes { get; set; } = 720;

    /// <summary>Максимальний розмір кешу вердиктів (title-URL → вердикт); перевищення — prune найстаріших.</summary>
    public int AvailabilityCacheMax { get; set; } = 2000;
}

/// <summary>Елемент каталогу Plex: href /movie/{slug} + назва + рік (null якщо сторінка року не відома).</summary>
public sealed record PlexCatalogItem(string Slug, string Title, int? Year, string Href);

/// <summary>
/// Парсер Plex за РЕАЛЬНОЮ структурою зразків (розвідка 09.11, зразки в scratch):
/// 1) /movie-database/{year} — server-render індекс року: &lt;a href="/movie/{slug}"&gt;&lt;span&gt;Назва&lt;/span&gt;&lt;/a&gt;
///    (слаг може бути і з роком «american-pie-1999», і без «the-matrix» — канонічний редірект обидва працює);
/// 2) /discover — картки aria-label="Назва (Рік)" href="/movie/{slug}" (рік у підписі картки);
/// 3) RSC-політ-чанки self.__next_f.push([N,"…"]) — у них "previewData":{"title":…} поруч з
///    "link":{"url":"/movie/{slug}","external":false} (потокові рядки хабів; року в них немає).
/// </summary>
public static partial class PlexParser
{
    /// <summary>Шлях year-сторінки каталогу (єдине джерело правди для клієнта й тестів).</summary>
    public static string YearPagePath(int year) => $"/movie-database/{year}";

    /// <summary>Повний URL тайтла з href виду /movie/{slug}.</summary>
    public static string MovieUrl(string baseUrl, string href) => baseUrl.TrimEnd('/') + href;

    // ── Індекс року на landing /movie-database ──

    [GeneratedRegex("href=\"/movie-database/(?<y>\\d{4})\"")]
    private static partial Regex YearIndexLink();

    /// <summary>Роки з landing-сторінки /movie-database (відсортовані за зростанням).</summary>
    public static List<int> ParseYearIndex(string html)
    {
        var years = new SortedSet<int>();
        foreach (Match m in YearIndexLink().Matches(html ?? ""))
            if (int.TryParse(m.Groups["y"].Value, out var y) && y is >= 1880 and <= 2100)
                years.Add(y);
        return [.. years];
    }

    // ── Year-сторінка: &lt;a href="/movie/{slug}"&gt;&lt;span&gt;Назва&lt;/span&gt;&lt;/a&gt; ──

    [GeneratedRegex("<a[^>]*href=\"(?<href>/movie/(?<slug>[a-z0-9\\-]+))\"[^>]*>\\s*<span[^>]*>(?<title>[^<]+)</span>")]
    private static partial Regex YearPageLink();

    /// <summary>Тайтли year-сторінки: слаг + назва (HTML-entities декодовані); рік = рік сторінки; дублі слагів злиті.</summary>
    public static List<PlexCatalogItem> ParseYearPageLinks(string html, int year)
    {
        var items = new List<PlexCatalogItem>();
        var seen = new HashSet<string>();
        foreach (Match m in YearPageLink().Matches(html ?? ""))
        {
            var href = m.Groups["href"].Value;
            if (!seen.Add(href)) continue; // той самий тайтл анкориться двічі (thumb/overlay)
            var title = WebUtilityDecode(m.Groups["title"].Value).Trim();
            if (title.Length == 0) continue;
            items.Add(new PlexCatalogItem(m.Groups["slug"].Value, title, year, href));
        }
        return items;
    }

    // ── Картки хабів (/discover): aria-label="Назва (Рік)" + href="/movie/{slug}" ──

    [GeneratedRegex("aria-label=\"(?<label>[^\"]+)\"[^>]*href=\"(?<href>/movie/(?<slug>[a-z0-9\\-]+))\"")]
    private static partial Regex CardAriaFirst();

    [GeneratedRegex("href=\"(?<href>/movie/(?<slug>[a-z0-9\\-]+))\"[^>]*aria-label=\"(?<label>[^\"]+)\"")]
    private static partial Regex CardHrefFirst();

    [GeneratedRegex("[ ]*\\((?<y>\\d{4})\\)\\s*$")]
    private static partial Regex LabelYear();

    /// <summary>Картки хаба (фільми; /show/ не підходить під /movie/-регекс): назва+рік з aria-label.</summary>
    public static List<PlexCatalogItem> ParseCardList(string html)
    {
        var items = new List<PlexCatalogItem>();
        var seen = new HashSet<string>();
        foreach (var re in new[] { CardAriaFirst(), CardHrefFirst() })
            foreach (Match m in re.Matches(html ?? ""))
            {
                var href = m.Groups["href"].Value;
                if (!seen.Add(href)) continue;
                var label = WebUtilityDecode(m.Groups["label"].Value).Trim();
                var yearMatch = LabelYear().Match(label);
                var year = yearMatch.Success ? int.Parse(yearMatch.Groups["y"].Value) : (int?)null;
                var title = yearMatch.Success ? label[..yearMatch.Index].TrimEnd() : label;
                if (title.Length == 0) continue;
                items.Add(new PlexCatalogItem(m.Groups["slug"].Value, title, year, href));
            }
        return items;
    }

    // ── RSC-політ-чанки (self.__next_f.push) ──

    /// <summary>Конкатенація рядків усіх push-чанків у порядку документа (як це робить браузер).</summary>
    public static string ParseFlightChunks(string html)
    {
        const string marker = "self.__next_f.push([";
        var payload = new StringBuilder();
        var html_ = html ?? "";
        var i = html_.IndexOf(marker, StringComparison.Ordinal);
        while (i >= 0)
        {
            var p = i + marker.Length;
            while (p < html_.Length && html_[p] is >= '0' and <= '9') p++; // номер chunk'а
            if (p + 1 < html_.Length && html_[p] == ',' && html_[p + 1] == '"')
            {
                p += 2;
                var raw = new StringBuilder();
                while (p < html_.Length)
                {
                    var c = html_[p];
                    if (c == '\\' && p + 1 < html_.Length) { raw.Append(c).Append(html_[p + 1]); p += 2; continue; }
                    if (c == '"') break; // незекрапована лапка — кінець строкового літерала
                    raw.Append(c);
                    p++;
                }
                payload.Append(DecodeJsString(raw.ToString()));
            }
            i = html_.IndexOf(marker, Math.Max(p, i + marker.Length), StringComparison.Ordinal);
        }
        return payload.ToString();
    }

    /// <summary>Строковий JSON-літерал → текст (незекрапована лапка в хвості/бій литерала — сире значення).</summary>
    internal static string DecodeJsString(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + raw + "\"") ?? raw;
        }
        catch (JsonException)
        {
            return raw; // обрізаний/нестандартний чанк — беремо як є (парсер лінків толерантний)
        }
    }

    [GeneratedRegex("\"previewData\":\\{\"title\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex FlightPreviewTitle();

    [GeneratedRegex("\"accessibilityLabel\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex FlightAccessibilityLabel();

    [GeneratedRegex("\"link\":\\{\"url\":\"(?<href>/movie/[a-z0-9\\-]+)\",\"external\":false\\}")]
    private static partial Regex FlightMovieLink();

    /// <summary>
    /// Тайтли з RSC-політу: "link":{"url":"/movie/{slug}","external":false} — фільмові лінки.
    /// Назва (за надійністю): 1) "accessibilityLabel" СТРОГО ДО лінка в межах ≤900 символів —
    /// назва того самого content-елемента (сусідній елемент відділений ≥1000 символів actions-блоба,
    /// тому off-by-one неможливий); 2) "previewData":{"title":…} до лінка в межах ≤1500 (фрагменти
    /// виду previewData→label→id→link). Після лінка НЕ шукаємо: у trending-хабах previewData
    /// сусіда стоїть блискуче близько перед наступним лінком — це дає зсув назв на один елемент.
    /// Року в чанках немає → Year=null. Реальні форми перевірені на зразку /discover (09.11).
    /// </summary>
    public static List<PlexCatalogItem> ParseFlightMovieLinks(string payload)
    {
        var items = new List<PlexCatalogItem>();
        var seen = new HashSet<string>();
        var payload_ = payload ?? "";
        var labels = new List<(int Index, string Title)>();
        foreach (Match t in FlightAccessibilityLabel().Matches(payload_))
            labels.Add((t.Index, DecodeJsString(t.Groups[1].Value)));
        var previews = new List<(int Index, string Title)>();
        foreach (Match t in FlightPreviewTitle().Matches(payload_))
            previews.Add((t.Index, DecodeJsString(t.Groups[1].Value)));

        foreach (Match m in FlightMovieLink().Matches(payload_))
        {
            var href = m.Groups["href"].Value;
            if (!seen.Add(href)) continue;
            var slug = href["/movie/".Length..];
            var title = NearestBefore(labels, m.Index, 900);
            if (title.Length == 0) title = NearestBefore(previews, m.Index, 1500);
            if (title.Length == 0) title = slug; // фолбек — слаг (чесно, без чужих назв)
            items.Add(new PlexCatalogItem(slug, title, null, href));
        }
        return items;
    }

    /// <summary>Найближча назва СТРОГО до позиції лінка в межах window; інакше порожньо.</summary>
    private static string NearestBefore(List<(int Index, string Title)> entries, int linkIndex, int window)
    {
        for (var k = entries.Count - 1; k >= 0; k--)
        {
            var (idx, title) = entries[k];
            if (idx >= linkIndex) continue;
            if (linkIndex - idx > window) return ""; // вийшли за межі елемента — далі лише чужі назви
            return title;
        }
        return "";
    }

    /// <summary>HTML-entities → текст (&amp;#39; → ', &amp;amp; → &amp;) — System.Net.WebUtility.</summary>
    internal static string WebUtilityDecode(string s) => System.Net.WebUtility.HtmlDecode(s);

    // ── Нормалізація назв (матчинг) ──

    /// <summary>
    /// Нормалізована назва для матчингу: нижній регістр (invariant), ТІЛЬКИ літери/цифри
    /// (юнікод: кирилиця, латиниця з діакритикою та CJK проходять), розділові знаки/дефіси/апострофи/пробіли — геть.
    /// "Mr. Holland's Opus" → "mrhollandsopus", "2 Fast 2 Furious" → "2fast2furious".
    /// </summary>
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var sb = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        return sb.ToString();
    }
}

/// <summary>
/// HTTP-клієнт watch.plex.tv: year-сторінки /movie-database/{year} (server-render, robots Allow).
/// Ретраї транзитних 429/502/503/504 (Cloudflare edge віддає 503 на окремих роках — перевірено
/// живцем: 1999/2000/2010/2027 = 200, 2022..2025 стійко 503 понад хвилину) з Retry-After-фолбеком.
/// </summary>
public sealed class PlexClient(HttpClient http, IOptions<PlexOptions> options, ILogger? log = null)
{
    private readonly PlexOptions _opt = options.Value;

    /// <summary>HTML year-сторінки або null (404 / ретраї вичерпано — рік пропускається, sync іде далі).</summary>
    public async Task<string?> GetYearPageHtmlAsync(int year, CancellationToken ct = default)
    {
        var url = PlexParser.MovieUrl(_opt.BaseUrl, PlexParser.YearPagePath(year));
        var maxAttempts = Math.Max(1, _opt.MaxRetries);
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(_opt.UserAgent);
            req.Headers.Accept.ParseAdd("text/html");

            using var resp = await http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;
            if (status is 429 or 502 or 503 or 504)
            {
                if (attempt >= maxAttempts)
                {
                    log?.LogWarning("plex: {Url} {Status} після {Attempts} спроб — рік пропущено", url, status, attempt);
                    return null;
                }
                var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                log?.LogWarning("plex: {Url} {Status} — пауза {Retry}s (спроба {Attempt}/{Max})",
                    url, status, delay.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(delay, ct);
                continue;
            }
            if (status == 404)
            {
                log?.LogWarning("plex: {Url} 404 — рік пропущено", url);
                return null;
            }
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }
}
