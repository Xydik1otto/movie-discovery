using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Storage;

namespace MovieDiscovery.Api.Controllers;

/// <summary>
/// Віддача постерів з ВЛАСНОГО S3-сховища MinIO (наказ власника: «фото плакатів — окрема БД
/// MinIO … віддавання на сайт з власного сховища»). GET /api/img/posters/{*key} — стрім з бакета
/// (FileStreamResult, contentType за розширенням, Cache-Control public,max-age=31536000,immutable —
/// ключі незмінні: {titleId:x8}{ext}); 404 — ключа нема, сховище вимкнено (fail-closed) або ключ
/// не пройшов санітарну перевірку. ?meta=1 — метадані (PosterImageResponse) замість бінарки.
/// Записує в бакет лише PosterStorageSync (адмін-джоба); контролер — лише читання.
/// </summary>
[ApiController]
public sealed class PosterImagesController(IPosterStorage storage) : ControllerBase
{
    /// <summary>Ключі в бакеті живуть під префіксом posters/ — маршрут уже містить його сегмент.</summary>
    private const string KeyPrefix = "posters/";

    [HttpGet("api/img/posters/{*key}")]
    public async Task<IActionResult> Get(string? key, CancellationToken ct)
    {
        var normalized = NormalizeKey(key);
        if (normalized is null)
            return NotFound(new { error = "not_found", message = "Некоректний ключ постера" });

        if (!storage.Enabled)
            return NotFound(new { error = "not_found", message = "Сховище постерів вимкнено (fail-closed)" });

        var fullKey = KeyPrefix + normalized;
        var stream = await storage.GetAsync(fullKey, ct);
        if (stream is null)
            return NotFound(new { error = "not_found", message = "Постера немає у сховищі" });

        var contentType = ContentTypeForExtension(normalized);
        if (Request.Query.ContainsKey("meta"))
        {
            // Метадані замість бінарки: стрім уже прочитано — розмір відомий.
            long size = stream.CanSeek ? stream.Length : 0;
            await stream.DisposeAsync();
            return Ok(new PosterImageResponse(fullKey, contentType, size));
        }

        // Ключі незмінні (ім'я = titleId + ext вмісту) → агресивний браузерний кеш назавжди.
        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        return File(stream, contentType); // FileStreamResult
    }

    /// <summary>
    /// Санітарна перевірка ключа: непорожній, без "..", без ведучих слешів (запобігання path-traversal
    /// у вигаданих шляхах; S3 сам по собі плоский — але чесні межі краще за довіру до вхідного рядка).
    /// </summary>
    internal static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim();
        if (k.Length == 0 || k.Length > 256) return null;
        if (k.Contains("..") || k.StartsWith('/') || k.StartsWith('\\')) return null;
        foreach (var ch in k)
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '.' and not '-' and not '_' and not '/')
                return null;
        return k;
    }

    /// <summary>Content-Type за розширенням ключа (сховище зберігає файл під ext за Content-Type синку).</summary>
    internal static string ContentTypeForExtension(string key) => key.ToLowerInvariant() switch
    {
        var k when k.EndsWith(".webp") => "image/webp",
        var k when k.EndsWith(".png") => "image/png",
        var k when k.EndsWith(".jpg") || k.EndsWith(".jpeg") => "image/jpeg",
        _ => "application/octet-stream",
    };
}
