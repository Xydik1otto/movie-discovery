using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MovieDiscovery.Api.Contracts;
using MovieDiscovery.Infrastructure.Persistence;
using MovieDiscovery.Infrastructure.Services;

namespace MovieDiscovery.Api.Controllers;

[ApiController]
[Route("api/lists")]
public sealed class ListsController(
    MovieDiscoveryDbContext db,
    UserListService userLists,
    TasteProfileService taste,
    GuestProfileService guest,
    BotIdentityService bots) : ControllerBase
{
    /// <summary>Список профілю: {itemId, ...TitleCard, rating, createdAt}.
    /// MD-05: X-Bot-Key+X-Telegram-User-Id → власний профіль telegram-юзера (profileId клієнта ігнорується).
    /// Cookie-юзер → його постійний профіль (FR-16), чужий profileId → 403.
    /// Гість (MD-01) → лише designated guest profile: чужий profileId → 403, невідомий → 404.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? profileId,
        [FromQuery] string? kind,
        [FromQuery] string? lang = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(kind) && !Core.Domain.ListKinds.IsValid(kind))
            return BadRequest(new { error = "invalid_request", message = "kind має бути watchlist | watched | read | liked | disliked" });

        var langSafe = SearchController.Lang(lang);
        var bot = await ResolveBotAsync(ct);
        if (bot.Error is not null) return bot.Error;

        Core.Domain.Profile? profile;
        if (bot.Profile is { } botProfile)
        {
            profile = botProfile; // MD-05: bot-режим — profileId клієнта ігнорується
        }
        else
        {
            var (resolved, foreign) = await ResolveProfileAsync(profileId, ct);
            if (foreign)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden" });
            profile = resolved;
        }
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

    /// <summary>Додати тайтл у список (legacy-шлях із kind у body). Неіснуючий tmdbId/titleId → 404; дублікат → 409;
    /// kind=read на не-мангу → 400 (аніме/манга мають NULL tmdb_id — використовуйте titleId).
    /// Cookie-юзер: profileId з body ігнорується — пишемо в ЙОГО постійний профіль (FR-16).</summary>
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] AddListItemRequest req, CancellationToken ct)
    {
        if (!Core.Domain.ListKinds.IsValid(req.Kind))
            return BadRequest(new { error = "invalid_request", message = "kind має бути watchlist | watched | read | liked | disliked" });
        if (req.Rating is < 1 or > 10)
            return BadRequest(new { error = "invalid_request", message = "rating має бути 1–10" });

        var bot = await ResolveBotAsync(ct);
        if (bot.Error is not null) return bot.Error;

        Core.Domain.Profile? profile;
        if (bot.Profile is { } botProfile)
        {
            profile = botProfile; // MD-05: profileId клієнта ігнорується
        }
        else if (TryUserId(out var userId))
        {
            var r = await userLists.AddAsync(userId, req.Kind, req.TitleId, req.TmdbId, req.Rating, ct);
            return MapResult(r, created: true);
        }
        else
        {
            var (resolved, foreign) = await ResolveProfileAsync(req.ProfileId == 0 ? null : req.ProfileId, ct);
            if (foreign)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden" });
            if (resolved is null)
                return NotFound(new { error = "not_found", message = "Профіль не знайдено" });
            profile = resolved;
        }

        var title = await FindTitleAsync(req.TitleId, req.TmdbId, ct);
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

        var entry = AddGuestEntry(profile.Id, req.Kind, title.Id, req.Rating);
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

    /// <summary>POST /api/lists/{kind} (FR-17): kind у маршруті, зокрема liked|disliked.
    /// Cookie-юзер → його профіль + interaction_log + Recompute; гість → гостьовий профіль як зараз.</summary>
    [HttpPost("{kind}")]
    public async Task<IActionResult> PostKind(string kind, [FromBody] PostKindRequest req, CancellationToken ct)
    {
        if (!Core.Domain.ListKinds.IsValid(kind))
            return BadRequest(new { error = "invalid_request", message = "kind має бути watchlist | watched | read | liked | disliked" });

        var bot = await ResolveBotAsync(ct);
        if (bot.Error is not null) return bot.Error;

        Core.Domain.Profile? profile;
        if (bot.Profile is { } botProfile)
        {
            profile = botProfile; // MD-05: profileId клієнта ігнорується
        }
        else if (TryUserId(out var userId))
        {
            var r = await userLists.AddAsync(userId, kind, req.TitleId, req.TmdbId, req.Rating, ct);
            return MapResult(r, created: true);
        }
        else
        {
            var (resolved, foreign) = await ResolveProfileAsync(req.ProfileId == 0 ? null : req.ProfileId, ct);
            if (foreign)
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden" });
            if (resolved is null)
                return NotFound(new { error = "not_found", message = "Профіль не знайдено" });
            profile = resolved;
        }

        var title = await FindTitleAsync(req.TitleId, req.TmdbId, ct);
        if (title is null)
            return NotFound(new { error = "title_not_found", message = "Тайтл не знайдено" });
        if (!Core.Domain.ListKinds.AllowedForType(kind, title.Type))
            return BadRequest(new { error = "invalid_request", message = "kind=read допустимий лише для манги (type=manga)" });

        var exists = await db.ListEntries
            .AnyAsync(l => l.ProfileId == profile.Id && l.Kind == kind && l.TitleId == title.Id, ct);
        if (exists)
            return Conflict(new { error = "already_in_list", message = "Тайтл уже у списку" });

        var entry = AddGuestEntry(profile.Id, kind, title.Id, req.Rating);
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

    /// <summary>POST /api/lists/watched {titleId, rating?} (FR-17): переглянуто з оцінкою 1–10.</summary>
    [HttpPost("watched")]
    public Task<IActionResult> PostWatched([FromBody] PostKindRequest req, CancellationToken ct) =>
        PostKind(Core.Domain.ListKinds.Watched, req, ct);

    /// <summary>GET /api/lists/stats (FR-17): {watchedCount, likedCount, dislikedCount, avgRating, topGenres}.</summary>
    [Authorize]
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        if (!TryUserId(out var userId))
            return Unauthorized(new { error = "unauthorized", message = "Потрібен вхід" });
        var s = await userLists.StatsAsync(userId, ct);
        return Ok(new
        {
            watchedCount = s.WatchedCount,
            likedCount = s.LikedCount,
            dislikedCount = s.DislikedCount,
            avgRating = s.AvgRating,
            topGenres = s.TopGenres.Select(g => new { genre = g.Genre, count = g.Count }),
        });
    }

    /// <summary>POST /api/lists/migrate-guest (FR-16): ідемпотентне перенесення гостьових items у профіль юзера.</summary>
    [Authorize]
    [HttpPost("migrate-guest")]
    public async Task<IActionResult> MigrateGuest([FromBody] MigrateGuestRequest req, CancellationToken ct)
    {
        if (!TryUserId(out var userId))
            return Unauthorized(new { error = "unauthorized", message = "Потрібен вхід" });
        var (moved, skipped) = await userLists.MigrateGuestAsync(userId, req.Items ?? [], ct);
        return Ok(new { moved, skipped });
    }

    /// <summary>Оновити оцінку запису списку. MD-01/MD-05: власник — bot-профіль, cookie-юзер
    /// (свій запис через userLists) або designated guest profile; чужий запис → 404 (не розкриває існування).</summary>
    [HttpPatch("{itemId:int}")]
    public async Task<IActionResult> Patch(int itemId, [FromBody] PatchListItemRequest req, CancellationToken ct)
    {
        if (req.Rating is < 1 or > 10)
            return BadRequest(new { error = "invalid_request", message = "rating має бути 1–10" });

        var bot = await ResolveBotAsync(ct);
        if (bot.Error is not null) return bot.Error;

        Core.Domain.Profile? owner;
        if (bot.Profile is { } botProfile)
        {
            owner = botProfile; // MD-05: bot оновлює записи ЛИШЕ свого профілю
        }
        else if (TryUserId(out var userId))
        {
            var r = await userLists.UpdateRatingAsync(userId, itemId, req.Rating, ct);
            return MapResult(r, created: false);
        }
        else
        {
            // MD-01: анонім може змінювати записи ЛИШЕ designated guest profile.
            owner = await guest.ResolveAsync(ct);
        }

        var entry = await db.ListEntries.FirstOrDefaultAsync(l => l.Id == itemId, ct);
        if (entry is null || owner is null || entry.ProfileId != owner.Id)
            return NotFound(new { error = "not_found", message = "Запис списку не знайдено" });

        entry.Rating = req.Rating;
        if (entry.Kind == "watched" && req.Rating.HasValue && entry.WatchedAt is null)
            entry.WatchedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Ok(new { id = entry.Id, rating = entry.Rating });
    }

    /// <summary>Видалити запис списку. MD-01/MD-05: власник — bot-профіль, cookie-юзер
    /// (свій запис через userLists) або designated guest profile; чужий запис → 404.</summary>
    [HttpDelete("{itemId:int}")]
    public async Task<IActionResult> Delete(int itemId, CancellationToken ct)
    {
        var bot = await ResolveBotAsync(ct);
        if (bot.Error is not null) return bot.Error;

        Core.Domain.Profile? owner;
        if (bot.Profile is { } botProfile)
        {
            owner = botProfile; // MD-05: bot видаляє записи ЛИШЕ свого профілю
        }
        else if (TryUserId(out var userId))
        {
            var removed = await userLists.DeleteAsync(userId, itemId, ct);
            return removed ? NoContent() : NotFound(new { error = "not_found", message = "Запис списку не знайдено" });
        }
        else
        {
            // MD-01: анонім може видаляти записи ЛИШЕ designated guest profile.
            owner = await guest.ResolveAsync(ct);
        }

        var entry = await db.ListEntries.FirstOrDefaultAsync(l => l.Id == itemId, ct);
        if (entry is null || owner is null || entry.ProfileId != owner.Id)
            return NotFound(new { error = "not_found", message = "Запис списку не знайдено" });

        db.ListEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>MD-05: X-Bot-Key + X-Telegram-User-Id → bot-режим (власний профіль telegram-юзера,
    /// profileId клієнта ігнорується). Заголовок X-Bot-Key відсутній/порожній → не bot-запит.
    /// Невірний ключ (або вимкнений Bot:ApiKey) → 403 forbidden; валідний ключ без числового
    /// X-Telegram-User-Id → 400 invalid_request.</summary>
    private async Task<(IActionResult? Error, Core.Domain.Profile? Profile)> ResolveBotAsync(CancellationToken ct)
    {
        var key = Request.Headers["X-Bot-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
            return (null, null); // не bot-запит — звичайні правила (cookie / анонім)

        if (!bots.KeyMatches(key))
            return (StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden" }), null);

        var raw = Request.Headers["X-Telegram-User-Id"].FirstOrDefault();
        if (!long.TryParse(raw, out var telegramUserId))
            return (BadRequest(new { error = "invalid_request", message = "X-Telegram-User-Id має бути числовим" }), null);

        return (null, await bots.ResolveAsync(telegramUserId, ct));
    }

    /// <summary>Cookie-юзер → його постійний профіль (створюємо зв'язку user_profiles за потреби; FR-16),
    /// чужий profileId з клієнта → forbidden (MD-01); гість → ЛИШЕ designated guest profile:
    /// довільний profileId відхиляється, профілі акаунтів анонімному шляху недоступні;
    /// якщо legacy guest profile приріплений до акаунта — гостьовий серверний шлях вимкнено (null → 404).</summary>
    private async Task<(Core.Domain.Profile? Profile, bool Forbidden)> ResolveProfileAsync(int? profileId, CancellationToken ct)
    {
        if (TryUserId(out var userId))
        {
            var up = await taste.ResolveAsync(userId, ct);
            var own = await db.Profiles.AsNoTracking().FirstAsync(p => p.Id == up.ProfileId, ct);
            // MD-01: авторизований — профіль ЗАВЖДИ власний; profileId клієнта ігнорується
            // (чужі профілі недосяжні в будь-якому разі).
            return (own, false);
        }

        var guestProfile = await guest.ResolveAsync(ct);
        if (guestProfile is null)
            return (null, false); // гостьовий серверний шлях вимкнено → 404 без деталей

        if (!profileId.HasValue || profileId.Value == 0 || profileId.Value == guestProfile.Id)
            return (guestProfile, false);

        // MD-01: чужий profileId — 403 (профіль існує) або 404 (невідомий) без розкриття приватних даних.
        var exists = await db.Profiles.AsNoTracking().AnyAsync(p => p.Id == profileId.Value, ct);
        return (null, exists);
    }

    private Core.Domain.ListEntry AddGuestEntry(int profileId, string kind, int titleId, int? rating)
    {
        var entry = new Core.Domain.ListEntry
        {
            ProfileId = profileId,
            Kind = kind,
            TitleId = titleId,
            Rating = rating,
            CreatedAt = DateTime.UtcNow,
            WatchedAt = kind is Core.Domain.ListKinds.Watched or Core.Domain.ListKinds.Read ? DateTime.UtcNow : null,
        };
        db.ListEntries.Add(entry);
        return entry;
    }

    private async Task<Core.Domain.Title?> FindTitleAsync(int? titleId, int? tmdbId, CancellationToken ct)
    {
        if (titleId is int id)
            return await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tmdbId is { } tid && tid != 0)
            return await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.TmdbId == tid, ct);
        return null;
    }

    private bool TryUserId(out string userId)
    {
        userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        return User.Identity?.IsAuthenticated == true && userId.Length > 0;
    }

    private IActionResult MapResult(ListActionResult r, bool created)
    {
        if (r.Ok)
        {
            return created
                ? Created($"/api/lists/{r.ItemId}", new
                {
                    id = r.ItemId, kind = r.Kind, titleId = r.TitleId, tmdbId = r.TmdbId,
                    rating = r.Rating, createdAt = r.CreatedAt, profileUpdated = r.ProfileUpdated,
                })
                : Ok(new { id = r.ItemId, rating = r.Rating });
        }
        var status = r.Error switch
        {
            "already_in_list" => 409,
            "title_not_found" or "not_found" => 404,
            _ => 400,
        };
        return StatusCode(status, new { error = r.Error, message = r.Message });
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

    public sealed class PostKindRequest
    {
        public int ProfileId { get; set; }
        public int TmdbId { get; set; }
        /// <summary>Локальний id тайтла — основний шлях (liked/disliked/watched).</summary>
        public int? TitleId { get; set; }
        public int? Rating { get; set; }
    }

    public sealed class PatchListItemRequest
    {
        public int? Rating { get; set; }
    }

    public sealed class MigrateGuestRequest
    {
        public List<GuestListItem>? Items { get; set; }
    }
}
