using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/lists")]
public sealed class ListsController(MovieDiscoveryDbContext db) : ControllerBase
{
    /// <summary>Список профілю: {itemId, ...TitleCard, rating, createdAt}.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? profileId,
        [FromQuery] string? kind,
        [FromQuery] string? lang = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(kind) && !Core.Domain.ListKinds.IsValid(kind))
            return BadRequest(new { error = "invalid_request", message = "kind має бути watchlist | watched | read" });

        var langSafe = SearchController.Lang(lang);
        var profile = await ResolveProfileAsync(profileId, ct);
        if (profile is null)
            return NotFound(new { error = "not_found", message = "Профіль не знайдено" });

        var query = db.ListEntries.AsNoTracking()
            .Include(l => l.Title)
            .Where(l => l.ProfileId == profile.Id);
        if (!string.IsNullOrWhiteSpace(kind))
            query = query.Where(l => l.Kind == kind);

        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(ct);

        return Ok(new
        {
            profileId = profile.Id,
            items = items.Select(l =>
            {
                var card = TitleMapper.ToCard(l.Title, langSafe);
                return new
                {
                    itemId = l.Id,
                    card.Id,
                    card.TmdbId,
                    card.Type,
                    card.Title,
                    card.OriginalTitle,
                    card.Year,
                    card.Genres,
                    card.Countries,
                    card.RatingTmdb,
                    card.RatingImdb,
                    card.PosterUrl,
                    card.RuntimeMin,
                    card.Seasons,
                    card.Overview,
                    kind = l.Kind,
                    rating = l.Rating,
                    createdAt = l.CreatedAt,
                    watchedAt = l.WatchedAt,
                    format = card.Format,
                    episodes = card.Episodes,
                    chapters = card.Chapters,
                    volumes = card.Volumes,
                    studios = card.Studios,
                    authors = card.Authors,
                    scoreMal = card.ScoreMal,
                    scoreAnilist = card.ScoreAnilist,
                    scoreShikimori = card.ScoreShikimori,
                };
            }).ToList(),
        });
    }

    /// <summary>Додати тайтл у список. Неіснуючий tmdbId/titleId → 404; дублікат → 409;
    /// kind=read на не-мангу → 400 (аніме/манга мають NULL tmdb_id — використовуйте titleId).</summary>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] AddListItemRequest req, CancellationToken ct)
    {
        if (!Core.Domain.ListKinds.IsValid(req.Kind))
            return BadRequest(new { error = "invalid_request", message = "kind має бути watchlist | watched | read" });
        if (req.Rating is < 1 or > 10)
            return BadRequest(new { error = "invalid_request", message = "rating має бути 1–10" });

        var profile = await ResolveProfileAsync(req.ProfileId, ct);
        if (profile is null)
            return NotFound(new { error = "not_found", message = "Профіль не знайдено" });

        Core.Domain.Title? title;
        if (req.TitleId is int titleId)
            title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == titleId, ct);
        else if (req.TmdbId != 0)
            title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.TmdbId == req.TmdbId, ct);
        else
            return BadRequest(new { error = "invalid_request", message = "Потрібен tmdbId або titleId" });

        if (title is null)
            return NotFound(new { error = "title_not_found", message = req.TitleId is int
                ? $"Тайтл з id={req.TitleId} не знайдено"
                : $"Тайтл з tmdbId={req.TmdbId} не знайдено" });

        if (!Core.Domain.ListKinds.AllowedForType(req.Kind, title.Type))
            return BadRequest(new { error = "invalid_request", message = "kind=read допустимий лише для манги (type=manga)" });

        var exists = await db.ListEntries
            .AnyAsync(l => l.ProfileId == profile.Id && l.Kind == req.Kind && l.TitleId == title.Id, ct);
        if (exists)
            return Conflict(new { error = "already_in_list", message = "Тайтл уже у списку" });

        var entry = new Core.Domain.ListEntry
        {
            ProfileId = profile.Id,
            Kind = req.Kind,
            TitleId = title.Id,
            Rating = req.Rating,
            CreatedAt = DateTime.UtcNow,
            WatchedAt = req.Kind is "watched" or "read" ? DateTime.UtcNow : null,
        };
        db.ListEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return Created($"/api/lists/{entry.Id}", new
        {
            id = entry.Id,
            profileId = entry.ProfileId,
            kind = entry.Kind,
            titleId = title.Id,
            tmdbId = title.TmdbId,
            rating = entry.Rating,
            createdAt = entry.CreatedAt,
        });
    }

    /// <summary>Оновити оцінку запису списку.</summary>
    [HttpPatch("{itemId:int}")]
    public async Task<IActionResult> Patch(int itemId, [FromBody] PatchListItemRequest req, CancellationToken ct)
    {
        if (req.Rating is < 1 or > 10)
            return BadRequest(new { error = "invalid_request", message = "rating має бути 1–10" });

        var entry = await db.ListEntries.FirstOrDefaultAsync(l => l.Id == itemId, ct);
        if (entry is null)
            return NotFound(new { error = "not_found", message = "Запис списку не знайдено" });

        entry.Rating = req.Rating;
        if (entry.Kind == "watched" && req.Rating.HasValue && entry.WatchedAt is null)
            entry.WatchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new { id = entry.Id, rating = entry.Rating });
    }

    [HttpDelete("{itemId:int}")]
    public async Task<IActionResult> Delete(int itemId, CancellationToken ct)
    {
        var entry = await db.ListEntries.FirstOrDefaultAsync(l => l.Id == itemId, ct);
        if (entry is null)
            return NotFound(new { error = "not_found", message = "Запис списку не знайдено" });

        db.ListEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Профіль за id або дефолтний (Е1: єдиний локальний профіль).</summary>
    private async Task<Core.Domain.Profile?> ResolveProfileAsync(int? profileId, CancellationToken ct)
    {
        if (profileId.HasValue)
            return await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId.Value, ct);
        return await db.Profiles.AsNoTracking()
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Id)
            .FirstOrDefaultAsync(ct);
    }

    public sealed class AddListItemRequest
    {
        public int ProfileId { get; set; }
        public string Kind { get; set; } = "watchlist";
        public int TmdbId { get; set; }
        /// <summary>Локальний id тайтла — для аніме/манги (tmdb_id NULL).</summary>
        public int? TitleId { get; set; }
        public int? Rating { get; set; }
    }

    public sealed class PatchListItemRequest
    {
        public int? Rating { get; set; }
    }
}
