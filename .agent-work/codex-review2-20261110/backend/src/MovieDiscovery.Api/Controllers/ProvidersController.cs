using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/title/{id:int}/providers")]
public sealed class ProvidersController(TitleService titles) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int id, [FromQuery] string? region = "UA", CancellationToken ct = default)
    {
        var title = await titles.ResolveAsync(id, ct);
        if (title is null)
            return NotFound(new { error = "not_found", message = "Тайтл не знайдено" });

        var regionSafe = string.IsNullOrWhiteSpace(region) ? "UA" : region.Trim().ToUpperInvariant();
        var providers = await titles.GetProvidersAsync(title, regionSafe, ct);
        return Ok(new
        {
            providers = providers.Select(p => new ProviderInfo(p.ProviderName, p.Kind, p.Url, p.Source)).ToList(),
        });
    }
}
