using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>Онбординг (FR-18): статичні 10 питань + відповіді → cold-start профіль смаку.</summary>
[ApiController]
[Authorize]
[Route("api/onboarding")]
public sealed class OnboardingController(TasteProfileService taste) : ControllerBase
{
    [HttpGet("questions")]
    public IActionResult Questions() => Ok(new { questions = OnboardingQuestions.Build() });

    public sealed record AnswersRequest(
        string[] Picks,
        string[] Mood,
        string[] Era,
        int[] FavoriteTitles,
        bool Skipped);

    /// <summary>200 {profileUpdated, onboardingDone}: picks×0.6, favorites як 2 лайки, quiz у jsonb.</summary>
    [HttpPost("answers")]
    public async Task<IActionResult> Answers([FromBody] AnswersRequest req, CancellationToken ct)
    {
        if (req.FavoriteTitles.Length > 3)
            return BadRequest(new { error = "invalid_request", message = "favoriteTitles — максимум 3 тайтли" });

        var up = await taste.ApplyOnboardingAsync(
            UserId, req.Picks ?? [], req.Mood ?? [], req.Era ?? [],
            req.FavoriteTitles ?? [], req.Skipped, ct);
        return Ok(new { profileUpdated = true, onboardingDone = up.OnboardingDone });
    }

    private string UserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
}
