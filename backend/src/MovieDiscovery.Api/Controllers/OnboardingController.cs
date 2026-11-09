using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

/// <summary>Онбординг (FR-18): статичні 10 питань + відповіді → cold-start профіль смаку.
/// MD-02: усі масиви nullable і null-safe (відсутні поля в JSON → 200, не 500); серверна валідація:
/// favoriteTitles ≤ 3 (дублікати згортаються) і кожен > 0; picks/mood/era ≤ 20 непорожніх значень.</summary>
[ApiController]
[Authorize]
[Route("api/onboarding")]
public sealed class OnboardingController(TasteProfileService taste, OnboardingPostersService posters) : ControllerBase
{
    [HttpGet("questions")]
    public async Task<IActionResult> Questions(CancellationToken ct)
    {
        var questions = OnboardingQuestions.Build();
        await posters.EnrichAsync(questions, ct);
        return Ok(new { questions });
    }

    public sealed record AnswersRequest(
        string[]? Picks,
        string[]? Mood,
        string[]? Era,
        int[]? FavoriteTitles,
        bool Skipped);

    /// <summary>200 {profileUpdated, onboardingDone}: picks×0.6, favorites як 2 лайки, quiz у jsonb.</summary>
    [HttpPost("answers")]
    public async Task<IActionResult> Answers([FromBody] AnswersRequest req, CancellationToken ct)
    {
        // MD-02: null-safe — немає .Length на null (відсутнє favoriteTitles у JSON більше не падає в 500).
        var favoriteTitles = (req.FavoriteTitles ?? []).Distinct().ToList();
        if (favoriteTitles.Any(t => t <= 0) || favoriteTitles.Count > 3)
            return BadRequest(new { error = "invalid_request", message = "favoriteTitles — до 3 додатних internal id тайтлів" });

        if (!ValidStringArray(req.Picks) || !ValidStringArray(req.Mood) || !ValidStringArray(req.Era))
            return BadRequest(new { error = "invalid_request", message = "picks/mood/era — масиви до 20 непорожніх рядків" });

        var up = await taste.ApplyOnboardingAsync(
            UserId,
            (req.Picks ?? []).Select(p => p.Trim()).ToArray(),
            (req.Mood ?? []).Select(p => p.Trim()).ToArray(),
            (req.Era ?? []).Select(p => p.Trim()).ToArray(),
            favoriteTitles,
            req.Skipped, ct);
        return Ok(new { profileUpdated = true, onboardingDone = up.OnboardingDone });
    }

    /// <summary>MD-02: масив — максимум 20 значень, кожен непорожній після trim (null → валідний «порожній»).</summary>
    private static bool ValidStringArray(string[]? values) =>
        values is null || (values.Length <= 20 && values.All(v => !string.IsNullOrWhiteSpace(v)));

    private string UserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";
}
