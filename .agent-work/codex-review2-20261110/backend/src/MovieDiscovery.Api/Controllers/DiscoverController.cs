using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>Режим «Кінотека-свайпи» (FR-18). Тільки для авторизованих (FR-16).</summary>
[ApiController]
[Authorize]
[Route("api/discover")]
public sealed class DiscoverController(DiscoverService discover) : ControllerBase
{
    /// <summary>GET /api/discover?count=10 → {items:[TitleCard], mode} (без pauseAfterStreak — скасовано власником).</summary>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int count = 10,
        [FromQuery] string? lang = null,
        [FromQuery] bool allowAdult = false,
        CancellationToken ct = default)
    {
        var (titles, mode) = await discover.GetFeedAsync(UserId(), count, SearchController.Lang(lang), allowAdult, ct);
        var items = titles.Select(t => TitleMapper.ToCard(t, SearchController.Lang(lang))).ToList();
        return Ok(new { items, mode });
    }

    public sealed record DiscoverActionRequest(int TitleId, string Action, int? Rating);

    /// <summary>POST /api/discover/action → 200 {profileUpdated}.</summary>
    [HttpPost("action")]
    public async Task<IActionResult> Action([FromBody] DiscoverActionRequest req, CancellationToken ct)
    {
        var result = await discover.HandleActionAsync(UserId(), req.TitleId, req.Action, req.Rating, ct);
        if (!result.Ok)
        {
            var status = result.Error switch
            {
                "title_not_found" => 404,
                "invalid_request" => 400,
                _ => 400,
            };
            return StatusCode(status, new { error = result.Error, message = result.Message });
        }
        return Ok(new { profileUpdated = result.ProfileUpdated });
    }

    private static string UserId(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";

    private string UserId() => UserId(User);
}
