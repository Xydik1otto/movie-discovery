using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Infrastructure.Localization;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/i18n")]
public sealed class I18nController : ControllerBase
{
    /// <summary>Плоский словник ключ=текст (uk, en). Інші мови не підтримуються (російської нема свідомо).</summary>
    [HttpGet("{lang}")]
    public IActionResult Get(string lang)
    {
        var catalog = I18nCatalog.For(lang);
        if (catalog is null)
            return NotFound(new { error = "not_found", message = "Підтримуються лише uk та en" });
        return Ok(catalog);
    }
}
